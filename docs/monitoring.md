# Monitoring

Three ways to see what is happening: the live dashboard, the activity log, and analytics.

## Dashboard

Open `/dashboard/` on the hub. It shows sessions by PC, live requests and pushes, SQL table sizes, logs, each board's standing and orders, and token feedback. It is read-only and never opens sessions or reads inboxes.

To turn it on, allow an administrator client's principal:

```json
{ "Relay": { "Dashboard": { "ViewerPrincipalIds": ["your-admin-principal"] } } }
```

- Sign in with that client's token. The browser swaps it for a 30-minute cookie.
- Sign-in sends an administrator token. From another device, use trusted HTTPS only, and never click through a certificate warning.
- The Visual Studio launch profile already allows `PaulC`.
- Reload open dashboard tabs after you update the hub.

## Activity

The hub logs every tool call to the `activity_log` table: time, session, role, tool, result, sizes and duration. Never arguments or reply text. It also logs each wake and each attention offer and answer. Logging never slows or fails a call. On by default.

Report your tokens on `task_update` or `action_report`:

```json
"usage": { "inputTokens": 120000, "outputTokens": 3400 }
```

Report what you used since your last report. It goes to the log only.

### Counters

| Counter | Definition |
| --- | --- |
| Tokens | Reported `inputTokens` plus `outputTokens` on successful calls. A retry with the same `requestId` counts once. |
| Wakes | With attention on: new tier 1 items and idle-hold wakes. With it off: new messages without `wake: false`. |
| Inbox reads | Bursts of first-page `check_inbox` calls by one session. A gap of 60 seconds ends a burst. |
| Offers made | `task_update` replies with `includeNextWork` that offered a task. |
| Offers that ended blocked | An offer whose next `task_claim` by that session failed, or whose task became `blocked` first. |
| Duplicate status writes | A `task_update`, `send_message` or `context_put` that writes the same status text as the write before it. |
| Manager turns | Bursts of calls by a `manager` session. A gap of 60 seconds starts a new turn. |
| Closures | A task's workflow changing to `completed`. |
| Manager turns per closure | Manager turns divided by closures. |
| Time to accepted result | Median time from a task's first `ready` to its closure. |

Read raw rows from a copy of the database:

```sql
SELECT role, tool, COUNT(*) AS calls, SUM(input_tokens) AS input, SUM(output_tokens) AS output
FROM activity_log GROUP BY role, tool ORDER BY calls DESC;
```

| Setting | Default | What it does |
| --- | --- | --- |
| `Relay:Activity:Enabled` | `true` | Log activity. |
| `Relay:Activity:RetentionDays` | 30 | Days of raw rows kept. Older rows roll up into `activity_daily`. |

## Tokens

The hub counts the tokens in every request and reply, with the o200k tokenizer. The counts go into `activity_log` as `request_tokens` and `reply_tokens`. They match Codex exactly. For Claude they are estimates, so they are labeled "estimated tokens". Counting runs in the background. On by default.

Administrators see the tokens of the tool definitions each role carries in `get_status`, as `toolTokens`, and on the dashboard's Standing page.

Each tool has a token budget, and every reply is held to it. A budget sits at what the tool's byte limit allows (about 3 bytes per token, so 10,923 tokens under the 32 KB limit). So a normal reply never changes: in practice only a reply that would be cut at the byte limit anyway is compacted. The doc cache keeps its own 2,000-token budget. A reply over its budget is compacted in steps:

1. Drop optional detail (such as full task messages) and set `detailFetchRequired`.
2. Shorten a paged list to its first items, with `truncated`, a `more` count and `nextCursor`. Lists without a cursor, such as search hits, are never cut.
3. Cut long text to its first line. The ID stays, so you can fetch the rest.

A compacted reply says `compacted: true` and `compactStep`, and is logged as a `budget` row (`step1` to `step3`). IDs, versions, codes, grants and errors are never removed. A mutation only loses its optional detail. The 32 KB byte limit still applies. A measure-only tool logs an over-budget reply as `over` and sends it unchanged.

| Setting | Default | What it does |
| --- | --- | --- |
| `Relay:Tokens:Enabled` | `true` | Count tokens. |
| `Relay:Tokens:Enforce` | `true` | Hold replies to their budget. `false`: only measure. |
| `Relay:Tokens:EnforceTools:N` | every tool but the doc cache's | Enforce only the tools listed; the rest only measure. The doc cache tools cannot be listed. |

## Feedback

Fixed rules spot wasteful patterns and send a short nudge. No AI runs in the hub. On by default. Nothing fires until agents repeat a pattern.

**Tips** (`Relay:Feedback:Tips`) go to the agent as a lowest-tier `tip` attention item on its next milestone or inbox reply. They never wake a chat.

| Rule | Fires when |
| --- | --- |
| `board_reread` | 3 full board reads without `sinceRevision` in an hour. |
| `task_reread` | 4 full reads of one task in 30 minutes. A read that came back unchanged for `ifNoneMatch` does not count. |
| `long_messages` | Your last 5 messages average over 150 tokens. |
| `inbox_polling` | 6 `check_inbox` reads with nothing new in an hour. Background reads and `waitSeconds` waits do not count. |
| `repeated_status` | A duplicate status write (see the counters above). |

A session gets at most one tip an hour, and each rule at most once a day. If the pattern comes back within an hour, the tip counts as ignored. A rule followed less than `MinFollowRate` of the time, over 20 or more tips, switches itself off and stays off after a restart. Administrators see switched-off rules in `get_status`, as `tipRulesOff`, and on the Standing page.

`task_get` counts toward tips only when it carries your session handle.

**Alerts** (`Relay:Feedback:Alerts`) are extra checks for the [anomaly monitor](collaboration.md#anomaly-monitor), so they also need analytics. They reach manager sessions as `anomaly` items, at most once a day per kind.

| Check | Alerts when |
| --- | --- |
| `tokens_per_task_by_role` | A role's tokens per task are more than double the week before, over at least 20 tasks. |
| `compaction_rate_by_tool` | A tool compacts more than 30% of at least 20 replies. |
| `session_token_outliers` | A session's busiest hour is 5 times its role's median hour, over at least 10 hours. |

**Team rules** (`Relay:Feedback:ProposeRules`): when one rule fires for 5 different sessions in 7 days, the hub proposes that rule's line as a standing line, from `cocern-hub`. It changes nothing until an administrator approves it with `standing_put`.

| Setting | Default | What it does |
| --- | --- | --- |
| `Relay:Feedback:Tips` | `true` | Send tips to agents. |
| `Relay:Feedback:Alerts` | `true` | Alert managers. Needs analytics. |
| `Relay:Feedback:ProposeRules` | `true` | Propose standing lines. |
| `Relay:Feedback:MinFollowRate` | 0.2 | Below this, a tip rule switches itself off. |

## Analytics

Hourly snapshots of the database, queried with DuckDB. Queries never touch the live database. On by default, with the [anomaly monitor](collaboration.md#anomaly-monitor).

DuckDB reads the snapshots with its `sqlite` extension. The Windows x64 build comes bundled in `extensions/duckdb`, signed by DuckDB, with a SHA-256 manifest. The build checks the sums and unpacks it into `duckdb_extensions` beside the hub, so nothing is downloaded. DuckDB checks the signature again when it loads it.

Without a usable extension (any other platform, or a file that fails its sum), the hub still starts. It logs one warning, and `analytics_query` answers `AnalyticsUnavailable`. For another platform or DuckDB version, with internet access:

```
dotnet CoCern.Host.dll --install-analytics-extension <folder> [platform]
```

Then point `Relay:Analytics:ExtensionDirectory` at that folder. Tests use `COCERN_DUCKDB_EXTENSIONS` when set, then the bundled folder (Windows x64), and otherwise download the extension once into a temp cache.

Ask with `analytics_query`. It needs an administrator token:

```json
{ "repositoryId": "cocern", "name": "tokens_per_task", "params": { "days": 7 } }
```

```json
{ "cols": ["key", "cur", "prev", "cur_n", "prev_n"], "rows": [["all", 1050.0, 330.0, 1, 1]], "at": "2026-11-01T10:00:00Z" }
```

Each named query compares the last `days` days (`cur`, 1 to 14, default 7) with the days before (`prev`). `cur_n` and `prev_n` count the rows behind each value.

| Name | Measures |
| --- | --- |
| `tokens_per_task` | Average reported tokens per task. |
| `calls_per_task` | Average tool calls per task. |
| `messages_per_task` | Average task messages per task. |
| `context_by_role` | Average reply bytes per session, by role. |
| `blocked_time` | Hours tasks stayed blocked. |
| `claim_conflicts_by_resource` | Refused claims, by resource. |
| `noisiest_tools` | Reply bytes, by tool. |
| `manager_assignment_latency` | Minutes from `ready` to first claim. |
| `quality_guardrails` | Closures, reopened tasks, failed and retried actions. |
| `attention_outcomes_by_kind` | Attention answers, by kind. |

With ad hoc SQL on, `"name": "sql", "sql": "SELECT ..."` runs one `SELECT` over the views: `tasks`, `tool_calls`, `context_by_role`, `messages`, `action_reports`, `claim_conflicts`, `attention_outcomes`, `budget_events`, `board_doc`, `task_doc`, `activity`. `relay_tokens` in `activity` is the hub's own token count for a call. Every ad hoc query is written to the hub log.

Errors: `AdministratorRequired`, `AnalyticsUnavailable` (off, no snapshot yet, or no usable extension), `AnalyticsRefused` (ad hoc SQL off, or not one `SELECT`), `AnalyticsTimeout`, `AnalyticsFailed`, `InvalidArguments`, `PayloadTooLarge` (reply over 8 KB).

| Setting | Default | What it does |
| --- | --- | --- |
| `Relay:Analytics:Enabled` | `true` | Take snapshots and answer queries. |
| `Relay:Analytics:AllowAdHocSql` | `true` | Accept `sql` (administrators only, like every analytics query). |
| `Relay:Analytics:SnapshotInterval` | `01:00:00` | Time between snapshots. At least 1 minute. |
| `Relay:Analytics:Directory` | `analytics` beside the database | Where snapshots go. |
| `Relay:Analytics:ExtensionDirectory` | `duckdb_extensions` beside the hub | Where the extension is. The build puts the bundled Windows x64 one there. |
| `Relay:Analytics:QueryTimeout` | `00:00:10` | Longest query. At most 5 minutes. |
| `Relay:Analytics:MaxRows` | 50 | Rows per result. At most 500. |

Snapshots hold only board and lock state. Sessions, inboxes, credentials and tokens stay in the live database.
