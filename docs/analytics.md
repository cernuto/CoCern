# Analytics

The hub can copy its database into a snapshot every hour and query that copy with DuckDB. Queries never touch the live database. Analytics is off by default.

The manager's `analytics_query` tool arrives in a later step. Until then the queries are reachable through the `IAnalyticsQueries` port in code and tests.

## Turn it on

1. Install DuckDB's `sqlite` extension once. It needs internet access the first time:

   ```
   dotnet PkRelay.Host.dll --install-analytics-extension <folder> [platform]
   ```

   The platform defaults to this machine's, such as `windows_amd64` or `linux_amd64`. A folder only works for the same DuckDB version and platform. The download is refused if it is larger than 64 MB unpacked.

   `Build-PkRelay.ps1` does this for you. For the tests it installs this machine's build into `artifacts/duckdb_extensions`. With `-Publish` it publishes the hub for `win-x64` and installs the `windows_amd64` build into `artifacts/host/duckdb_extensions`, where the hub looks by default. Loading it offline on a Windows hub has not been tested yet.

   For CI, install into a cached folder and set `PKRELAY_DUCKDB_EXTENSIONS` to it before `dotnet test`. Without it, the tests download the extension once into the temp folder.

2. Set `Relay:Analytics:Enabled` to `true` and restart the hub.

The hub takes a snapshot at startup and then on every interval. If a snapshot fails, the hub logs why and keeps the previous one.

## Settings

| Setting | Default | What it does |
| --- | --- | --- |
| `Relay:Analytics:Enabled` | false | Take snapshots and answer analytics queries. |
| `Relay:Analytics:SnapshotInterval` | `01:00:00` | Time between snapshots. At least 1 minute. |
| `Relay:Analytics:Directory` | `analytics` next to the database (`data/analytics`) | Where snapshots live. A relative path starts at the content root. |
| `Relay:Analytics:ExtensionDirectory` | `duckdb_extensions` next to the hub | Where the `sqlite` extension is installed. |
| `Relay:Analytics:QueryTimeout` | `00:00:10` | Longest a query may run. At most 5 minutes. |
| `Relay:Analytics:MaxRows` | 50 | Rows kept per result; the result says when more were cut. At most 500. |

## What a snapshot holds

A snapshot is a backup of `relay.db` that keeps only board and lock state. These stay in the live database:

- session state (private inboxes, attention notes and bindings)
- control and discovery state (the session directory and machine credentials)
- the `requests` replay caches and any session token
- the search index

Sessions show up only through `activity_log`. Attention outcomes come from there as well.

The backup reads the last committed state on its own read-only connection, so the hub keeps committing while it runs.

The copy is then turned into a DuckDB file. It is deleted once a newer snapshot has replaced it and no query is still using it. Old snapshots from an earlier run are deleted at startup.

Queries run with file access turned off and settings locked. Ad hoc SQL must be one `SELECT`, and anything else is refused. DuckDB's `sqlite` extension is used only to copy the snapshot, never while queries run, because its `sqlite_scan` can read any SQLite file even with file access off.

## Views

| View | One row per |
| --- | --- |
| `tasks` | Task: state, owner, created, closed, claim, message and report counts |
| `tool_calls` | Public tool call, from `activity_log` |
| `context_by_role` | Role and day: calls, sessions, bytes and tokens |
| `messages` | Task message |
| `action_reports` | Action report on a task |
| `claim_conflicts` | Claim refused because someone else holds it (`Busy` or `OwnerConflict`), per resource |
| `attention_outcomes` | Attention offer outcome |
| `board_doc`, `task_doc`, `activity` | Board document, task document and activity row, for ad hoc queries |

Times are `*_ticks` (.NET UTC ticks) and `at_time` (UTC). `created` and `closed` come from activity, so they are empty once that activity is older than `Relay:Activity:RetentionDays`.

## Named queries

Every named query takes the repository and an optional `days` (1 to 14, default 7). It compares the last `days` days (`cur`) with the `days` days before that (`prev`), up to the time of the snapshot. Each row has the same columns:

| Column | Meaning |
| --- | --- |
| `key` | What the row measures: `all`, a role, a tool, a resource or a kind |
| `cur`, `prev` | The value in each period |
| `cur_n`, `prev_n` | How many rows each value comes from |

| Name | `key` | Value |
| --- | --- | --- |
| `tokens_per_task` | `all` | Average reported tokens per task with a report |
| `calls_per_task` | `all` | Average tool calls per task touched |
| `messages_per_task` | `all` | Average task messages per task touched |
| `context_by_role` | role | Average reply bytes per session |
| `blocked_time` | `all`, `open` | `all`: average hours blocked, for spells that ended in the period; spells still open are left out. `open`: blocked spells still open at the end of each period |
| `claim_conflicts_by_resource` | resource | Refused claims |
| `noisiest_tools` | tool | Reply bytes; `cur_n` is the number of calls |
| `manager_assignment_latency` | `all` | Average minutes from a task becoming `ready` to its first claim. This stands in for assignment time, which the hub does not record |
| `quality_guardrails` | `closures`, `reopened`, `failed_actions`, `retried_actions` | Counts |
| `attention_outcomes_by_kind` | `<kind>:<outcome>` | Outcomes; `cur_n` is all outcomes of that kind |

A refused `task_claim` counts once for each resource listed in the task's `packet.resources`, or once against `task:<id>` when the task lists none. A stale `expectedVersion` (`VersionConflict`) is not counted.
