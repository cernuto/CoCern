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
       background_call = 1 AS background_call,
       coalesce(call_origin, 'unknown') AS call_origin,
       CASE WHEN target LIKE 'task:%' THEN url_decode(split_part(target, '/', 2)) END AS board_id,
       CASE WHEN target LIKE 'task:%' THEN url_decode(split_part(target, '/', 3)) END AS task_id,
       CASE WHEN target LIKE 'resource:%' THEN url_decode(split_part(target, '/', 2)) END AS resource_id
FROM relay.activity_log;

-- One row per public tool call.
CREATE VIEW tool_calls AS SELECT * EXCLUDE (kind) FROM activity WHERE kind = 'call';

-- Weighted measurements for quiet status reads moved out of the raw log early. Detailed workflow/offer/inbox/manager records stay raw.
-- Older status measurements have UTC-hour precision and expire with ordinary raw retention; traffic byte/origin totals below are permanent.
CREATE VIEW tool_measurements AS
SELECT repository_id, at_ticks, session_id, role, tool, background_call, board_id, task_id,
       1::BIGINT AS calls, request_bytes, reply_bytes, request_tokens, reply_tokens, relay_tokens,
       CASE WHEN request_tokens IS NOT NULL AND reply_tokens IS NOT NULL THEN relay_tokens ELSE 0 END AS measured_tokens,
       CASE WHEN request_tokens IS NOT NULL AND reply_tokens IS NOT NULL THEN 1 ELSE 0 END::BIGINT AS measured_calls
FROM tool_calls
UNION ALL
SELECT repository_id, at_ticks, nullif(session_id, ''), nullif(role, ''), tool, true, NULL::VARCHAR, NULL::VARCHAR,
       calls, request_bytes, reply_bytes,
       CASE WHEN request_samples > 0 THEN request_tokens END, CASE WHEN reply_samples > 0 THEN reply_tokens END,
       request_tokens + reply_tokens, measured_tokens, measured_calls
FROM relay.activity_background_hourly;

CREATE VIEW activity_measurements AS
SELECT repository_id, at_ticks, kind, tool, code, 1::BIGINT AS calls FROM activity
UNION ALL
SELECT repository_id, at_ticks, 'call', tool, 'Ok', calls FROM relay.activity_background_hourly;

-- Traffic totals survive raw retention. Daily timestamps have day precision; unclassified historical totals stay unknown.
-- Reported origin describes the adapter path, not whether a model actually executed or consumed the reply.
CREATE VIEW tool_traffic AS
SELECT repository_id, at_ticks, tool, background_call, 1::BIGINT AS calls, request_bytes, reply_bytes FROM tool_calls
UNION ALL
SELECT d.repository_id, cocern_ticks(d.day), d.tool, t.background_call, t.calls, t.request_bytes, t.reply_bytes
FROM relay.activity_daily d, LATERAL (VALUES
    (false, d.model_calls, d.model_request_bytes, d.model_reply_bytes),
    (true, d.background_calls, d.background_request_bytes, d.background_reply_bytes),
    (NULL::BOOLEAN, d.calls - d.model_calls - d.background_calls, d.request_bytes - d.model_request_bytes - d.background_request_bytes, d.reply_bytes - d.model_reply_bytes - d.background_reply_bytes)
) t(background_call, calls, request_bytes, reply_bytes)
WHERE d.kind = 'call' AND t.calls > 0;

-- Explicit untrusted call paths are a separate dimension from model/background classification.
-- Old daily totals without a companion row remain unknown; never count both representations twice.
CREATE VIEW tool_origin_traffic AS
WITH daily AS (
    SELECT day, repository_id, tool, sum(calls) AS calls, sum(request_bytes) AS request_bytes, sum(reply_bytes) AS reply_bytes
    FROM relay.activity_daily WHERE kind = 'call' GROUP BY day, repository_id, tool),
covered AS (
    SELECT day, repository_id, tool, sum(calls) AS calls, sum(request_bytes) AS request_bytes, sum(reply_bytes) AS reply_bytes
    FROM relay.activity_origin_daily GROUP BY day, repository_id, tool)
SELECT repository_id, at_ticks, tool, call_origin, 1::BIGINT AS calls, request_bytes, reply_bytes FROM tool_calls
UNION ALL
SELECT repository_id, cocern_ticks(day), tool, call_origin, calls, request_bytes, reply_bytes FROM relay.activity_origin_daily
UNION ALL
SELECT d.repository_id, cocern_ticks(d.day), d.tool, 'unknown',
       d.calls - coalesce(c.calls, 0), d.request_bytes - coalesce(c.request_bytes, 0), d.reply_bytes - coalesce(c.reply_bytes, 0)
FROM daily d LEFT JOIN covered c USING (day, repository_id, tool)
WHERE d.calls > coalesce(c.calls, 0);

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

-- One row per action report on a task. A recovery's report has recovered = true (outcome 'abandoned' when it gave none); a writer's
-- report that replaced an abandoned status has overrode = 'abandoned'.
CREATE VIEW action_reports AS
SELECT t.repository_id, t.board_id, t.task_id, r.value ->> '$.actionId' AS action_id, TRY_CAST(r.value ->> '$.attempt' AS BIGINT) AS attempt,
       r.value ->> '$.outcome' AS outcome, r.value ->> '$.sessionId' AS session_id,
       coalesce(TRY_CAST(r.value ->> '$.recovered' AS BOOLEAN), false) AS recovered, r.value ->> '$.overrode' AS overrode,
       cocern_ticks(r.value ->> '$.at') AS at_ticks, cocern_at(cocern_ticks(r.value ->> '$.at')) AS at_time
FROM task_doc t, json_each(t.task -> '$.reports') r;

-- Calls, sessions, bytes and reported tokens per role and day.
CREATE VIEW context_by_role AS
SELECT repository_id, coalesce(role, '') AS role, CAST(at_time AS DATE) AS day, count(*) AS calls, count(DISTINCT session_id) AS sessions,
       sum(request_bytes) AS request_bytes, sum(reply_bytes) AS reply_bytes,
       coalesce(sum(input_tokens), 0) AS input_tokens, coalesce(sum(output_tokens), 0) AS output_tokens
FROM tool_calls GROUP BY ALL;

-- Coverage is conservative: daily totals prove raw calls were lost but cannot recover task transitions. A durable task with no
-- retained workflow write also has an unknown start. Current board counts are snapshot state, never historical durations.
CREATE VIEW workflow_history_coverage AS
WITH repositories AS (
    SELECT repository_id FROM board_doc UNION SELECT repository_id FROM tool_calls UNION SELECT repository_id FROM relay.activity_daily),
raw AS (
    SELECT repository_id, min(at_ticks) AS first_workflow_ticks FROM tool_calls
    WHERE ok AND tool = 'task_update' AND workflow_state IS NOT NULL GROUP BY repository_id),
rolled AS (
    SELECT repository_id, sum(calls) AS rolled_up_calls FROM relay.activity_daily WHERE kind = 'call' AND tool IN ('task_update', 'task_claim') GROUP BY repository_id),
current_state AS (
    SELECT t.repository_id, count(*) FILTER (WHERE NOT t.archived AND (t.task ->> '$.workflow.state') = 'blocked') AS current_blocked_tasks,
           bool_or((t.task ->> '$.workflow.state') IS NOT NULL AND NOT EXISTS (
               SELECT 1 FROM tool_calls c WHERE c.repository_id = t.repository_id AND c.board_id = t.board_id AND c.task_id = t.task_id
                   AND c.ok AND c.tool = 'task_update' AND c.workflow_state IS NOT NULL)) AS unobserved_workflow
    FROM task_doc t GROUP BY t.repository_id)
SELECT r.repository_id, raw.first_workflow_ticks, coalesce(rolled.rolled_up_calls, 0) AS rolled_up_calls,
       coalesce(rolled.rolled_up_calls, 0) > 0 OR coalesce(s.unobserved_workflow, false) AS history_incomplete,
       EXISTS (SELECT 1 FROM board_doc b WHERE b.repository_id = r.repository_id) AS has_current_board,
       coalesce(s.current_blocked_tasks, 0) AS current_blocked_tasks
FROM repositories r LEFT JOIN raw USING (repository_id) LEFT JOIN rolled USING (repository_id) LEFT JOIN current_state s USING (repository_id);

-- One row per refused claim: Busy or OwnerConflict, never VersionConflict. Only event-time resource identity is evidence.
-- A task packet is mutable, so task_claim without an event-time resource uses honest task:<id> attribution and never fans out.
CREATE VIEW claim_conflicts AS
SELECT c.id, c.at_ticks, c.at_time, c.repository_id, c.session_id, c.tool, c.code, c.board_id, c.task_id,
       coalesce(c.resource_id, 'task:' || c.task_id) AS resource_id
FROM tool_calls c
WHERE c.tool IN ('task_claim', 'claim_acquire') AND NOT c.ok AND c.code IN ('Busy', 'OwnerConflict');
