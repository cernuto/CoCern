using System.Collections.Frozen;
using CoCern.Contracts;

namespace CoCern.Analytics;

/// <summary>
/// The fixed named queries. Every query returns the same columns: <c>key</c> (what the row measures), <c>cur</c> and <c>prev</c> (the
/// value in the current and the previous period), and <c>cur_n</c> and <c>prev_n</c> (how many rows each value was computed from).
/// Parameters: <c>$repository</c>; <c>$now</c>, <c>$from</c> and <c>$prev</c> in .NET ticks, so the current period is
/// [<c>$from</c>, <c>$now</c>) and the previous one [<c>$prev</c>, <c>$from</c>).
/// </summary>
public static class NamedQueries
{
    public const string TokensPerTask = AnalyticsQueryNames.TokensPerTask;
    public const string CallsPerTask = AnalyticsQueryNames.CallsPerTask;
    public const string MessagesPerTask = AnalyticsQueryNames.MessagesPerTask;
    public const string ContextByRole = AnalyticsQueryNames.ContextByRole;
    public const string BlockedTime = AnalyticsQueryNames.BlockedTime;
    public const string ClaimConflictsByResource = AnalyticsQueryNames.ClaimConflictsByResource;
    public const string NoisiestTools = AnalyticsQueryNames.NoisiestTools;
    public const string ManagerAssignmentLatency = AnalyticsQueryNames.ManagerAssignmentLatency;
    public const string QualityGuardrails = AnalyticsQueryNames.QualityGuardrails;
    public const string AttentionOutcomesByKind = AnalyticsQueryNames.AttentionOutcomesByKind;

    /// <summary>The columns every named query returns, in order.</summary>
    public static readonly IReadOnlyList<string> Columns = ["key", "cur", "prev", "cur_n", "prev_n"];

    private const string Window = "repository_id = $repository AND at_ticks >= $prev AND at_ticks < $now";

    /// <summary>One summary row named 'all': the average of <c>v</c> per period over <c>source(in_cur, v)</c>.</summary>
    private static string Average(string source) => $"""
        SELECT 'all' AS key, avg(v) FILTER (WHERE in_cur) AS cur, avg(v) FILTER (WHERE NOT in_cur) AS prev,
               count(*) FILTER (WHERE in_cur) AS cur_n, count(*) FILTER (WHERE NOT in_cur) AS prev_n
        FROM {source}
        """;

    /// <summary>Workflow writes per task with the state before each, so changes can be found even across the window's start.</summary>
    private const string WorkflowStates = """
        states AS (
            SELECT board_id, task_id, id, at_ticks, workflow_state,
                   lag(workflow_state) OVER (PARTITION BY board_id, task_id ORDER BY at_ticks, id) AS before
            FROM tool_calls
            WHERE repository_id = $repository AND ok AND tool = 'task_update' AND workflow_state IS NOT NULL AND task_id IS NOT NULL AND at_ticks < $now)
        """;

    private static readonly FrozenDictionary<string, string> Queries = new Dictionary<string, string>
    {
        // Reported tokens per task with any report. An exact retry (same session, tool and requestId) counts once.
        [TokensPerTask] = $"""
            WITH reports AS (
                SELECT min(at_ticks) >= $from AS in_cur, board_id, task_id, max(coalesce(input_tokens, 0) + coalesce(output_tokens, 0)) AS tokens
                FROM tool_calls
                WHERE {Window} AND ok AND task_id IS NOT NULL AND (input_tokens IS NOT NULL OR output_tokens IS NOT NULL)
                GROUP BY board_id, task_id, session_id, tool, coalesce(request_id, CAST(id AS VARCHAR))),
            per_task AS (SELECT in_cur, sum(tokens) AS v FROM reports GROUP BY in_cur, board_id, task_id)
            {Average("per_task")}
            """,
        // Tool calls naming a task, per task touched.
        [CallsPerTask] = $"""
            WITH per_task AS (
                SELECT at_ticks >= $from AS in_cur, count(*) AS v FROM tool_calls
                WHERE {Window} AND task_id IS NOT NULL GROUP BY in_cur, board_id, task_id)
            {Average("per_task")}
            """,
        // Task messages per task touched by a call or a message.
        [MessagesPerTask] = $"""
            WITH touched AS (
                SELECT DISTINCT at_ticks >= $from AS in_cur, board_id, task_id FROM tool_calls WHERE {Window} AND task_id IS NOT NULL
                UNION
                SELECT DISTINCT at_ticks >= $from, board_id, task_id FROM messages WHERE {Window}),
            counted AS (
                SELECT at_ticks >= $from AS in_cur, board_id, task_id, count(*) AS n FROM messages WHERE {Window} GROUP BY ALL),
            per_task AS (SELECT t.in_cur, coalesce(c.n, 0) AS v FROM touched t LEFT JOIN counted c USING (in_cur, board_id, task_id))
            {Average("per_task")}
            """,
        // Reply bytes (what enters an agent's context) per session, by role.
        [ContextByRole] = $"""
            WITH per_session AS (
                SELECT at_ticks >= $from AS in_cur, coalesce(role, '') AS role, session_id, sum(reply_bytes) AS v FROM tool_calls
                WHERE {Window} AND session_id IS NOT NULL GROUP BY ALL)
            SELECT role AS key, avg(v) FILTER (WHERE in_cur) AS cur, avg(v) FILTER (WHERE NOT in_cur) AS prev,
                   count(*) FILTER (WHERE in_cur) AS cur_n, count(*) FILTER (WHERE NOT in_cur) AS prev_n
            FROM per_session GROUP BY role ORDER BY coalesce(cur, 0) DESC, key
            """,
        // 'all': hours a task stayed blocked, for spells that ended in the period; spells still open are left out.
        // 'open': blocked spells still open at the end of each period ($now and $from).
        [BlockedTime] = $"""
            WITH {WorkflowStates},
            changes AS (
                SELECT workflow_state, at_ticks, lead(at_ticks) OVER (PARTITION BY board_id, task_id ORDER BY at_ticks, id) AS ended
                FROM states WHERE before IS DISTINCT FROM workflow_state),
            spells AS (
                SELECT ended >= $from AS in_cur, (ended - at_ticks) / 36000000000.0 AS v FROM changes
                WHERE workflow_state = 'blocked' AND ended >= $prev),
            blocked AS (SELECT at_ticks, ended FROM changes WHERE workflow_state = 'blocked')
            {Average("spells")}
            UNION ALL
            SELECT 'open', count(*) FILTER (WHERE ended IS NULL), count(*) FILTER (WHERE at_ticks < $from AND (ended IS NULL OR ended >= $from)),
                   count(*) FILTER (WHERE ended IS NULL), count(*) FILTER (WHERE at_ticks < $from AND (ended IS NULL OR ended >= $from))
            FROM blocked
            """,
        // Refused claims per resource (see the claim_conflicts view).
        [ClaimConflictsByResource] = $"""
            SELECT resource_id AS key, count(*) FILTER (WHERE at_ticks >= $from) AS cur, count(*) FILTER (WHERE at_ticks < $from) AS prev,
                   count(*) FILTER (WHERE at_ticks >= $from) AS cur_n, count(*) FILTER (WHERE at_ticks < $from) AS prev_n
            FROM claim_conflicts WHERE {Window} GROUP BY resource_id ORDER BY cur DESC, key
            """,
        // Reply bytes and calls per tool and reported origin, including daily totals after raw retention.
        [NoisiestTools] = $"""
            SELECT tool || ' [' || CASE WHEN background_call THEN 'background' WHEN NOT background_call THEN 'model-facing' ELSE 'unknown' END || ']' AS key,
                   sum(reply_bytes) FILTER (WHERE at_ticks >= $from) AS cur, sum(reply_bytes) FILTER (WHERE at_ticks < $from) AS prev,
                   coalesce(sum(calls) FILTER (WHERE at_ticks >= $from), 0)::BIGINT AS cur_n, coalesce(sum(calls) FILTER (WHERE at_ticks < $from), 0)::BIGINT AS prev_n
            FROM tool_traffic WHERE {Window} GROUP BY tool, background_call ORDER BY coalesce(cur, 0) DESC, key
            """,
        // Minutes from a task's workflow becoming ready to its first claim, for claims in the period: a proxy for how long ready work
        // waits for an owner, since the hub records no assignment.
        [ManagerAssignmentLatency] = $"""
            WITH events AS (
                SELECT board_id, task_id, id, at_ticks, CASE WHEN tool = 'task_claim' THEN 'claim' ELSE workflow_state END AS ev
                FROM tool_calls
                WHERE repository_id = $repository AND ok AND task_id IS NOT NULL AND at_ticks < $now
                  AND (tool = 'task_claim' OR (tool = 'task_update' AND workflow_state IS NOT NULL))),
            ordered AS (SELECT *, lag(ev) OVER (PARTITION BY board_id, task_id ORDER BY at_ticks, id) AS before FROM events),
            changes AS (
                SELECT ev, at_ticks, lead(ev) OVER w AS next_ev, lead(at_ticks) OVER w AS next_at
                FROM ordered WHERE before IS DISTINCT FROM ev WINDOW w AS (PARTITION BY board_id, task_id ORDER BY at_ticks, id)),
            waits AS (
                SELECT next_at >= $from AS in_cur, (next_at - at_ticks) / 600000000.0 AS v FROM changes
                WHERE ev = 'ready' AND next_ev = 'claim' AND next_at >= $prev)
            {Average("waits")}
            """,
        // Counts: closures, reopened tasks (completed, then ready or blocked again), failed actions and actions a recovery abandoned
        // (external effects unknown), each attempt counted once at its first such report (a recovery or an acceptance repeats the outcome),
        // and retried actions (each attempt after the first).
        [QualityGuardrails] = $"""
            WITH {WorkflowStates},
            events AS (
                SELECT CASE WHEN workflow_state = 'completed' THEN 'closures' ELSE 'reopened' END AS key, at_ticks FROM states
                WHERE at_ticks >= $prev AND before IS DISTINCT FROM workflow_state AND (workflow_state = 'completed' OR before = 'completed')
                UNION ALL
                SELECT key, first_at FROM (
                    SELECT CASE outcome WHEN 'failed' THEN 'failed_actions' ELSE 'abandoned_actions' END AS key, min(at_ticks) AS first_at
                    FROM action_reports WHERE repository_id = $repository AND outcome IN ('failed', 'abandoned')
                    GROUP BY board_id, task_id, action_id, attempt, outcome)
                WHERE first_at >= $prev AND first_at < $now
                UNION ALL
                SELECT 'retried_actions', first_at FROM (
                    SELECT min(at_ticks) AS first_at FROM action_reports WHERE repository_id = $repository AND attempt > 1
                    GROUP BY board_id, task_id, action_id, attempt)
                WHERE first_at >= $prev AND first_at < $now),
            keys AS (SELECT unnest(['abandoned_actions', 'closures', 'reopened', 'failed_actions', 'retried_actions']) AS key)
            SELECT k.key, count(e.at_ticks) FILTER (WHERE e.at_ticks >= $from) AS cur, count(e.at_ticks) FILTER (WHERE e.at_ticks < $from) AS prev,
                   count(e.at_ticks) FILTER (WHERE e.at_ticks >= $from) AS cur_n, count(e.at_ticks) FILTER (WHERE e.at_ticks < $from) AS prev_n
            FROM keys k LEFT JOIN events e USING (key) GROUP BY k.key ORDER BY k.key
            """,
        // Offer outcomes per item kind, as '<kind>:<outcome>'. cur_n and prev_n are all outcomes of that kind, so cur / cur_n is a share.
        [AttentionOutcomesByKind] = $"""
            SELECT item_kind || ':' || outcome AS key,
                   count(*) FILTER (WHERE at_ticks >= $from) AS cur, count(*) FILTER (WHERE at_ticks < $from) AS prev,
                   sum(count(*) FILTER (WHERE at_ticks >= $from)) OVER (PARTITION BY item_kind) AS cur_n,
                   sum(count(*) FILTER (WHERE at_ticks < $from)) OVER (PARTITION BY item_kind) AS prev_n
            FROM attention_outcomes WHERE {Window} GROUP BY item_kind, outcome ORDER BY key
            """,
        // Manager alerts (Relay:Feedback:Alerts), run by the anomaly monitor only.
        // A role's relay tokens (the hub's own counts of requests and replies) per task its sessions touched.
        [AnalyticsQueryNames.TokensPerTaskByRole] = $"""
            WITH per_role AS (
                SELECT at_ticks >= $from AS in_cur, coalesce(role, '') AS role, CAST(sum(relay_tokens) AS DOUBLE) AS tokens,
                       count(DISTINCT board_id || '/' || task_id) AS tasks
                FROM tool_calls WHERE {Window} AND (request_tokens IS NOT NULL OR reply_tokens IS NOT NULL) GROUP BY ALL)
            SELECT role AS key, max(tokens / nullif(tasks, 0)) FILTER (WHERE in_cur) AS cur, max(tokens / nullif(tasks, 0)) FILTER (WHERE NOT in_cur) AS prev,
                   coalesce(max(tasks) FILTER (WHERE in_cur), 0) AS cur_n, coalesce(max(tasks) FILTER (WHERE NOT in_cur), 0) AS prev_n
            FROM per_role GROUP BY role ORDER BY coalesce(cur, 0) DESC, key
            """,
        // The share of a tool's replies that were compacted to their token budget.
        [AnalyticsQueryNames.CompactionRateByTool] = $"""
            SELECT tool AS key,
                   count(*) FILTER (WHERE kind = 'budget' AND code LIKE 'step%' AND at_ticks >= $from) / nullif(count(*) FILTER (WHERE kind = 'call' AND at_ticks >= $from), 0) AS cur,
                   count(*) FILTER (WHERE kind = 'budget' AND code LIKE 'step%' AND at_ticks < $from) / nullif(count(*) FILTER (WHERE kind = 'call' AND at_ticks < $from), 0) AS prev,
                   count(*) FILTER (WHERE kind = 'call' AND at_ticks >= $from) AS cur_n, count(*) FILTER (WHERE kind = 'call' AND at_ticks < $from) AS prev_n
            FROM activity WHERE {Window} AND kind IN ('call', 'budget') AND tool IS NOT NULL GROUP BY tool ORDER BY coalesce(cur, 0) DESC, key
            """,
        // Each session's busiest hour of relay tokens in the current period, against the median session-hour of its role over both periods.
        [AnalyticsQueryNames.SessionTokenOutliers] = $"""
            WITH hours AS (
                SELECT at_ticks >= $from AS in_cur, coalesce(role, '') AS role, session_id, at_ticks // 36000000000 AS hour, CAST(sum(relay_tokens) AS DOUBLE) AS v
                FROM tool_calls WHERE {Window} AND session_id IS NOT NULL AND (request_tokens IS NOT NULL OR reply_tokens IS NOT NULL) GROUP BY ALL),
            medians AS (SELECT role, median(v) AS m, count(*) AS n FROM hours GROUP BY role),
            peaks AS (SELECT role, session_id, max(v) AS v FROM hours WHERE in_cur GROUP BY ALL)
            SELECT p.session_id AS key, p.v AS cur, m.m AS prev, m.n AS cur_n, m.n AS prev_n
            FROM peaks p JOIN medians m USING (role) ORDER BY p.v / nullif(m.m, 0) DESC NULLS LAST, key
            """
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Every name, in the order the documentation lists them.</summary>
    public static IReadOnlyList<string> Names => AnalyticsQueryNames.All;

    /// <summary>Parameters a named query accepts besides the repository, which the hub always supplies.</summary>
    public static readonly IReadOnlyList<string> Parameters = ["days"];

    public static bool TryGet(string name, out string sql) => Queries.TryGetValue(name, out sql!);
}
