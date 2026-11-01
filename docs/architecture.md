# Architecture

PkRelay stores coordination only: tasks, claims, messages, notes and reported outcomes. It never runs IDE tools, models or builds. Agents do that with their own tools and report back.

## Parts

| Project | What it holds |
| --- | --- |
| `PkRelay.Contracts` | The tool catalog and schemas, error codes and limits (`RelayLimits`). Shared by hub and adapter. |
| `PkRelay.Domain` | The rules, as pure code. `CoordinationStateMachine.Apply` checks every call, then hands it to one aggregate: control, session, task board or lock. Discovery (`DiscoveryStateMachine`) and the anomaly monitor (`AnomalyMonitor`) have their own. |
| `PkRelay.Core` | `RelayApplication`: checks the request, authenticates the session, routes each tool, and composes calls that span aggregates. Also the Orleans grains and the ports below. |
| `PkRelay.Storage` | SQLite: grain state, due work, the search index and the activity log. |
| `PkRelay.Analytics` | DuckDB snapshots and named queries. Loaded only when analytics is on. |
| `PkRelay.Host` | ASP.NET Core: REST, MCP, SignalR, auth, the due-work pump, the dashboard host. |
| `PkRelay.Dashboard` | The Blazor WebAssembly dashboard. |
| `PkRelay.Adapter` | The stdio MCP adapter, the node, setup, and the wake watchers. Depends only on Contracts. |

Dependencies point inward. Domain knows nothing about Orleans or storage.

| Grain | Key | Holds |
| --- | --- | --- |
| `SessionGrain` | session ID | Inbox, binding, attention items. |
| `TaskBoardGrain` | repository + board | Tasks, workflow, actions, standing, orders. |
| `LockGrain` | resource | One advisory claim. |
| `ControlGrain` | `hub` | Pause/stop, the session directory, manager routing. |
| `DiscoveryGrain` | `hub` | Joined machines and their announced chats. |
| `AnomalyMonitorGrain` | repository | The next anomaly check. |

Ports in Core, implemented outside it: `IDueWorkSource`, `IFullTextSearch`, `IActivitySink`, `IActivityCounterSource`, `IAnalyticsQueries`, `ICoordinationNotifier`.

## How a call flows

1. A client calls `POST /api/tools/{tool}` or `/mcp`. The stdio adapter forwards to the REST route.
2. The host authenticates the token. Identity comes only from the token, never from the JSON.
3. `RelayApplication.ExecuteAsync` validates the request and checks the session handle.
4. It calls the aggregate's grain. The grain runs `CoordinationStateMachine.Apply` on a copy of its state.
5. On success, one SQLite transaction saves the new state, its outbox of effects for other grains, its due work, and its search rows.
6. `DueWorkDispatcher` delivers outbox effects and due work. Receivers ignore effects they already applied.
7. A message to a session sends a SignalR hint, `coordination.changed`. The inbox stays the source of truth.
8. The call is recorded in `activity_log` by a background writer, outside the transaction.

## Where state lives

| Place | What |
| --- | --- |
| `data/relay.db` | One SQLite file, schema version 2, WAL. Tables `grain_state`, `due_work`, `hub_metadata`, `search_index` and `search_docs`, `activity_log` and `activity_daily`. Only the hub opens it. |
| `data/analytics/` | DuckDB snapshots, when analytics is on. |
| `data/hub-certificate.pfx` | The hub's HTTPS certificate, when the network listener is on. |
| `%LOCALAPPDATA%\PkRelay\node` | On each PC: the node's credential, pinned fingerprint, session handles and wake database. |

## Rules to keep

- Claims are advisory. The hub cannot stop a tool call or a manual IDE action.
- Every fixed limit lives in `RelayLimits`.
- Keep tool descriptions and schemas short. They are sent on every model turn, and a test caps their size.

Design history is in `docs/superpowers/` and `docs/manager-retrospective-2026-10-29.md`. What has been tested is in [Verification](verification.md).
