# PkRelay

PkRelay lets coding agents in different chats and on different PCs share tasks, claims and messages. One hub keeps the state. Each agent talks to it through a small MCP adapter.

## Run it

Install the .NET SDK named in `global.json`. Then, from this folder:

```powershell
./scripts/Build-PkRelay.ps1 -Publish
./scripts/Start-PkRelay.ps1
```

The hub runs at `http://127.0.0.1:5271` and prints where it saved your token file.

Add PkRelay to your agent as an MCP server:

```
command:  dotnet <this folder>/artifacts/adapter/PkRelay.Adapter.dll
env:      PKRELAY_TOKEN_FILE=<this folder>/data/local-client-token
```

Each new chat starts with `session_open`.

## Docs

- [Setup](docs/setup.md): settings, switches and upgrade warnings.
- [Collaboration](docs/collaboration.md): tasks, attention, standing rules, orders and search.
- [Other PCs](docs/discovery.md): connect more PCs and wake chats.
- [Monitoring](docs/monitoring.md): dashboard, activity counters and analytics.
- [Architecture](docs/architecture.md): for contributors.
