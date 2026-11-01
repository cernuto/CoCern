# Monitoring

Three ways to see what is happening: the live dashboard, the activity log, and analytics.

## Dashboard

Open `/dashboard/` on the hub. It shows sessions by PC, live requests and pushes, SQL table sizes, logs, and each board's standing and orders. It is read-only and never opens sessions or reads inboxes.

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

## Analytics

Hourly snapshots of the database, queried with DuckDB. Queries never touch the live database. **Off by default.**

1. Install DuckDB's `sqlite` extension. `Build-PkRelay.ps1` does this for you. By hand, with internet access:

   ```
   dotnet PkRelay.Host.dll --install-analytics-extension <folder> [platform]
   ```

   The hub looks in `duckdb_extensions` beside itself. For CI, install into a cached folder and set `PKRELAY_DUCKDB_EXTENSIONS` to it before `dotnet test`.

2. Set `Relay:Analytics:Enabled` to `true` and restart. This also starts the [anomaly monitor](collaboration.md#anomaly-monitor).

Ask with `analytics_query`. It needs an administrator token:

```json
{ "repositoryId": "pkrelay", "name": "tokens_per_task", "params": { "days": 7 } }
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

With ad hoc SQL on, `"name": "sql", "sql": "SELECT ..."` runs one `SELECT` over the views: `tasks`, `tool_calls`, `context_by_role`, `messages`, `action_reports`, `claim_conflicts`, `attention_outcomes`, `board_doc`, `task_doc`, `activity`. Every ad hoc query is written to the hub log.

Errors: `AdministratorRequired`, `AnalyticsUnavailable` (off, or no snapshot yet), `AnalyticsRefused` (ad hoc SQL off, or not one `SELECT`), `AnalyticsTimeout`, `AnalyticsFailed`, `InvalidArguments`, `PayloadTooLarge` (reply over 8 KB).

| Setting | Default | What it does |
| --- | --- | --- |
| `Relay:Analytics:Enabled` | `false` | Take snapshots and answer queries. |
| `Relay:Analytics:AllowAdHocSql` | `false` | Accept `sql`. |
| `Relay:Analytics:SnapshotInterval` | `01:00:00` | Time between snapshots. At least 1 minute. |
| `Relay:Analytics:Directory` | `analytics` beside the database | Where snapshots go. |
| `Relay:Analytics:ExtensionDirectory` | `duckdb_extensions` beside the hub | Where the extension is. |
| `Relay:Analytics:QueryTimeout` | `00:00:10` | Longest query. At most 5 minutes. |
| `Relay:Analytics:MaxRows` | 50 | Rows per result. At most 500. |

Snapshots hold only board and lock state. Sessions, inboxes, credentials and tokens stay in the live database.
