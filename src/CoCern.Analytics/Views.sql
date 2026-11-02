-- Views over one analytics snapshot, attached read-only as `relay`. They are created in DuckDB's in-memory catalog before the
-- configuration is locked. grain_state.payload is the envelope JSON; the aggregate document inside it is a JSON string again
-- ($.Value.Json), so board views unwrap twice. Times: *_ticks are .NET UTC ticks and at_time is the same moment as a UTC timestamp.

CREATE MACRO cocern_at(ticks) AS make_timestamp((ticks - 621355968000000000) // 10);
CREATE MACRO cocern_ticks(iso) AS epoch_us(TRY_CAST(iso AS TIMESTAMPTZ)) * 10 + 621355968000000000;

-- One row per board. The grain key is the escaped repository and board, joined by '/'.
CREATE VIEW board_doc AS
SELECT url_decode(split_part(grain_key, '/', 1)) AS repository_id,
       url_decode(split_part(grain_key, '/', 2)) AS board_id,
       json_extract_string(payload, '$.Value.Json')::JSON AS doc
FROM relay.grain_state WHERE state_name = 'board' AND payload IS NOT NULL;

-- One row per finished task moved off its board (task_archive), with the task JSON as it left.
CREATE VIEW archived_tasks AS
SELECT repository_id, board_id, task_id, cocern_at(completed_ticks) AS completed_at, cocern_at(archived_ticks) AS archived_at, json::JSON AS task
FROM relay.task_archive;

-- Every task, on its board or archived, so the views below (and the named queries over them) still count archived tasks.
CREATE VIEW task_doc AS
SELECT b.repository_id, b.board_id, t.key AS task_id, t.value AS task, false AS archived
FROM board_doc b, json_each(b.doc -> '$.tasks') t
UNION ALL
SELECT repository_id, board_id, task_id, task, true AS archived FROM archived_tasks;

-- Every activity row. relay_tokens are the hub's own estimated tokens (o200k) for the request and reply. Targets are 'task:<repo>/<board>/<task>' and 'resource:<repo>/<resource>', each part escaped.
CREATE VIEW activity AS
SELECT id, at_ticks, cocern_at(at_ticks) AS at_time, kind, repository_id, session_id, role, tool, ok = 1 AS ok, code,
       request_bytes, reply_bytes, duration_ms, input_tokens, output_tokens, request_id, target, workflow_state, offer_target,
       request_tokens, reply_tokens, coalesce(request_tokens, 0) + coalesce(reply_tokens, 0) AS relay_tokens,
       CASE WHEN target LIKE 'task:%' THEN url_decode(split_part(target, '/', 2)) END AS board_id,
       CASE WHEN target LIKE 'task:%' THEN url_decode(split_part(target, '/', 3)) END AS task_id,
       CASE WHEN target LIKE 'resource:%' THEN url_decode(split_part(target, '/', 2)) END AS resource_id
FROM relay.activity_log;

-- One row per public tool call.
CREATE VIEW tool_calls AS SELECT * EXCLUDE (kind) FROM activity WHERE kind = 'call';

-- One row per reply over its token budget: code 'over' (measure-only), 'over:attention' or 'over:nextWork', or 'step1' to 'step3' (compacted).
CREATE VIEW budget_events AS
SELECT id, at_ticks, at_time, repository_id, session_id, role, tool, code, reply_tokens
FROM activity WHERE kind = 'budget';

-- One row per attention offer outcome: shown, accepted, later or dismissed.
CREATE VIEW attention_outcomes AS
SELECT id, at_ticks, at_time, repository_id, session_id, tool AS item_kind, code AS outcome, target AS item_id
FROM activity WHERE kind = 'attention';

-- One row per task. `created` is its first logged call and `closed` its last completed write, both null once that activity
-- has been rolled up (after Relay:Activity:RetentionDays).
CREATE VIEW tasks AS
WITH calls AS (
    SELECT repository_id, board_id, task_id, min(at_ticks) AS first_ticks,
           max(at_ticks) FILTER (WHERE ok AND tool = 'task_update' AND workflow_state = 'completed') AS completed_ticks
    FROM tool_calls WHERE task_id IS NOT NULL GROUP BY ALL)
SELECT t.repository_id, t.board_id, t.task_id,
       t.task ->> '$.workflow.state' AS state,
       nullif(t.task ->> '$.owner', '') AS owner,
       cocern_at(c.first_ticks) AS created,
       CASE WHEN t.task ->> '$.workflow.state' = 'completed' THEN cocern_at(c.completed_ticks) END AS closed,
       coalesce(TRY_CAST(t.task ->> '$.claimGeneration' AS BIGINT), 0) AS claim_count,
       coalesce(json_array_length(t.task -> '$.messages'), 0) AS message_count,
       coalesce(json_array_length(t.task -> '$.reports'), 0) AS report_count,
       TRY_CAST(t.task ->> '$.updatedAt' AS TIMESTAMPTZ) AS updated_at,
       t.archived
FROM task_doc t LEFT JOIN calls c USING (repository_id, board_id, task_id);

-- One row per task message.
CREATE VIEW messages AS
SELECT t.repository_id, t.board_id, t.task_id, m.value ->> '$.messageId' AS message_id, m.value ->> '$.kind' AS kind,
       m.value ->> '$.sessionId' AS session_id, cocern_ticks(m.value ->> '$.at') AS at_ticks, cocern_at(cocern_ticks(m.value ->> '$.at')) AS at_time,
       length(m.value ->> '$.text') AS text_length
FROM task_doc t, json_each(t.task -> '$.messages') m;

-- One row per action report on a task.
CREATE VIEW action_reports AS
SELECT t.repository_id, t.board_id, t.task_id, r.value ->> '$.actionId' AS action_id, TRY_CAST(r.value ->> '$.attempt' AS BIGINT) AS attempt,
       r.value ->> '$.outcome' AS outcome, r.value ->> '$.sessionId' AS session_id,
       cocern_ticks(r.value ->> '$.at') AS at_ticks, cocern_at(cocern_ticks(r.value ->> '$.at')) AS at_time
FROM task_doc t, json_each(t.task -> '$.reports') r;

-- Calls, sessions, bytes and reported tokens per role and day.
CREATE VIEW context_by_role AS
SELECT repository_id, coalesce(role, '') AS role, CAST(at_time AS DATE) AS day, count(*) AS calls, count(DISTINCT session_id) AS sessions,
       sum(request_bytes) AS request_bytes, sum(reply_bytes) AS reply_bytes,
       coalesce(sum(input_tokens), 0) AS input_tokens, coalesce(sum(output_tokens), 0) AS output_tokens
FROM tool_calls GROUP BY ALL;

-- One row per refused claim and resource: a claim refused because someone else holds it (Busy, or OwnerConflict). A stale
-- expectedVersion (VersionConflict) is not a conflict. A refused claim_acquire names its resource. A refused task_claim counts once
-- for each resource listed in the task's packet.resources, or once against the task itself ('task:<id>') when it lists none.
CREATE VIEW claim_conflicts AS
WITH expected AS (
    SELECT repository_id, board_id, task_id, unnest(json_extract_string(task, '$.workflow.packet.resources[*]')) AS resource_id FROM task_doc)
SELECT c.id, c.at_ticks, c.at_time, c.repository_id, c.session_id, c.tool, c.code, c.board_id, c.task_id,
       coalesce(c.resource_id, e.resource_id, 'task:' || c.task_id) AS resource_id
FROM tool_calls c
LEFT JOIN expected e ON c.tool = 'task_claim' AND e.repository_id = c.repository_id AND e.board_id = c.board_id AND e.task_id = c.task_id
WHERE c.tool IN ('task_claim', 'claim_acquire') AND NOT c.ok AND c.code IN ('Busy', 'OwnerConflict');
