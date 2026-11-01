<p align="center"><img src="docs/assets/cocern-logo.png" alt="CoCern: coordinate, analyze" width="320"></p>

# CoCern

CoCern lets coding agents in different chats and on different PCs share tasks, claims and messages. One hub keeps the state. Each agent talks to it through a small MCP adapter.

## Run it

Install the .NET SDK named in `global.json`. Then, from this folder:

```powershell
./scripts/Build-CoCern.ps1 -Publish
./scripts/Start-CoCern.ps1
```

The hub runs at `http://127.0.0.1:5271` and prints where it saved your token file.

Add CoCern to your agent as an MCP server:

```
command:  dotnet <this folder>/artifacts/adapter/CoCern.Adapter.dll
env:      COCERN_TOKEN_FILE=<this folder>/data/local-client-token
```

Each new chat starts with `session_open`.

## What's in the stack

**Features:** shared task board with dependencies and claims · attention offers and urgent-only wakes · standing preferences and grants · standing orders (off by default) · full-text search · document cache (markdown, text, OpenAPI) · activity counters and token-aware feedback · analytics with DuckDB · anomaly alerts · live dashboard.

**Built with:** .NET 10 · Microsoft Orleans · SQLite with FTS5 · DuckDB · Lucene.Net · Microsoft.ML.Tokenizers · YamlDotNet · MCP.

## Docs

- [Setup](docs/setup.md): settings, switches, upgrade warnings and the move from the old name.
- [Collaboration](docs/collaboration.md): tasks, attention, standing rules, orders and search.
- [Other PCs](docs/discovery.md): connect more PCs and wake chats.
- [Monitoring](docs/monitoring.md): dashboard, activity counters and analytics.
- [Architecture](docs/architecture.md): for contributors.
