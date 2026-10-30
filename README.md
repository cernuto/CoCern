# PkRelay

PkRelay preserves task context and coordinates work across PCs, chats, and sessions. Agents use their existing Roslyn CodeLens and Visual Studio MCP servers directly; PkRelay owns coordination records, advisory claims, reported outcomes, and durable messages.

The stack is .NET 10, ASP.NET Core, Orleans, SQLite, Serilog, OpenAPI, Scalar, and the official MCP C# SDK. One hub owns one local SQLite file. Do not put its live database on a shared drive or start a second hub against it.

## Build and start

Install the .NET SDK selected by `global.json`, then run from this directory:

```powershell
./scripts/Build-PkRelay.ps1
./scripts/Start-PkRelay.ps1 -RepositoryId pkrelay
```

The bootstrap starts a loopback development hub at `http://127.0.0.1:5271`, creates a private local client token under the ignored `data` directory, and passes it through process configuration. It prints the token's file location, never its value. Use `/scalar` for the API reference, `/openapi/v1.json` for OpenAPI, `/mcp` for authenticated remote MCP, and `/hubs/coordination` for session-scoped SignalR hints. No token is embedded in the API documentation.

Use `./scripts/Build-PkRelay.ps1 -Publish` to produce deployable host and adapter folders under `artifacts`. Start the published host with `-HostDll ./artifacts/host/PkRelay.Host.dll`. Do not rebuild the outputs of a running host; publish a new folder and switch deliberately.

## Connect an agent

Configure an MCP client to run the absolute `PkRelay.Adapter.dll` path with `dotnet`, supplying `PKRELAY_URL` and `PKRELAY_TOKEN_FILE` in its protected environment. Use the token path printed by the bootstrap. See [adapter setup](docs/adapter.md) for exact behavior, role surfaces, and optional SignalR hints. Keep credentials out of source control and shared task notes.

Each new chat calls `session_open` with a fresh request identity. Keep its returned session token private to that chat. Attach to an existing task for continuity; do not reuse another chat's binding or private inbox. Claim the task and required registered resources, do IDE work through the existing tools, then report meaningful milestones. Native provider chat tools remain responsible for authorized dispatch and waits.

For automatic local Codex follow-up, use the optional [wake adapter mode](docs/wake.md). It watches durable inbox changes in ordinary code and queues one bounded inbox-check prompt for an explicitly enrolled chat. The relay host remains coordination-only; existing model, tool and approval settings remain in force.

## Runtime boundaries

Claims are cooperative. They cannot stop manual IDE actions or grant device, database, deployment, or account permissions. An invoking/unknown action and unresolved restoration obligation remain blocking even after a lease expires. Reading an inbox does not acknowledge a message; explicit acknowledgment follows handling.

Remote machines require an authenticated HTTPS deployment and enrolled scoped clients. Remote ChatGPT use requires a configured external OAuth issuer and actual account/tunnel onboarding; local token setup does not establish that integration. Optional MCP Events, cross-provider wake mechanisms, automatic source transfer, and multi-hub deployment are outside this first build.

Read the [architecture](docs/architecture.md), [host setup](docs/host.md), [adapter setup](docs/adapter.md), and [verification results](docs/verification.md) for the durability, pagination, authorization, and recovery contracts.

## Reduce coordination overhead

Use [collaboration workflow](docs/collaboration.md) for durable board deltas, structured closure packets and reported criteria/blockers, and an optional next-work offer returned with a milestone. Blocked work stays blocked until an explicit evidenced update; offers require ordinary claims before execution. The complete suite now passes 117 tests, including HTTP/MCP, restart, stale-claim and cleanup regressions. These features reduce repeated reads and round trips; actual all-participant token savings remain unmeasured.
