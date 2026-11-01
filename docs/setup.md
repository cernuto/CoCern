# Setup

## Start the hub

```powershell
./scripts/Build-PkRelay.ps1 -Publish       # builds, runs the tests, publishes to artifacts/host and artifacts/adapter
./scripts/Start-PkRelay.ps1                # loopback hub on http://127.0.0.1:5271
```

- The start script makes a private administrator token in `data/local-client-token`. `-RepositoryId <id>` sets its repository (default `pkrelay`).
- To run the published hub: `./scripts/Start-PkRelay.ps1 -HostDll ./artifacts/host/PkRelay.Host.dll`.
- Never rebuild the folder of a running hub or adapter. Publish to a new folder and switch.
- One hub owns one database file. Do not put it on a shared drive or start a second hub on it.
- The first start of this hub on an older database moves it to schema 2, after saving a backup beside it: `relay.db.pre-migration-<id>.db`.
- An older hub refuses a schema 2 database. To roll back, stop the hub and restore that backup.
- The hub needs SQLite with FTS5. Without it, the hub stops at startup and leaves the database unchanged.

## Connect an agent

Run the adapter as an MCP server: `dotnet <path>/PkRelay.Adapter.dll`.

| Variable | Default | What it does |
| --- | --- | --- |
| `PKRELAY_URL` | `http://127.0.0.1:5271` | Hub address. Plain HTTP works only on loopback. |
| `PKRELAY_TOKEN_FILE` | | File holding the client token. Wins over `PKRELAY_TOKEN`. |
| `PKRELAY_TOKEN` | | The token itself, for clients with protected settings. |
| `PKRELAY_PROFILE` | | `chat` shows only `session_open`, `send_message`, `check_inbox`, `ack` and `chat_directory`. Use it for chats that only message. |
| `PKRELAY_ROLE` | | `admin` also lists administrator tools. The hub still checks the token. |
| `PKRELAY_SIGNALR` | `false` | `true` prints a hint on standard error when the session's inbox changes. |

With none of the first three set, the adapter uses this PC's node. See [Other PCs](discovery.md).

Each chat opens its own session with `session_open` and keeps its session token private.

### Spend fewer tokens

Every model call re-reads the whole chat, so fewer calls matter most.

- Wait for mail with `check_inbox` and `waitSeconds` (up to 50) instead of polling.
- Pass the `etag` from `task_get`, `get_status`, `worker_list`, `context_get` or `chat_directory` back as `ifNoneMatch`. An unchanged read returns only `{"unchanged":true,"etag":...}`.
- Acknowledge in your reply: `send_message` with `ackMessageIds`.
- Use `supersede: true` for a repeated status message, and `wake: false` for a note nobody must act on now.
- Renew claims rarely: `claim_renew` accepts `leaseSeconds` up to 1800.
- Send your session handle on `task_get` too. With [tips](monitoring.md#feedback) on, the hub can then point out rereads.

## Switches and warnings

| Setting | Default | Page |
| --- | --- | --- |
| `Relay:Activity:Enabled` | on | [Monitoring](monitoring.md#activity) |
| `Relay:Tokens:Enabled` | on | [Monitoring](monitoring.md#tokens) |
| `Relay:Tokens:EnforceTools` | none | [Monitoring](monitoring.md#tokens) |
| `Relay:Feedback:Tips` | off | [Monitoring](monitoring.md#feedback) |
| `Relay:Feedback:Alerts` | off | [Monitoring](monitoring.md#feedback) |
| `Relay:Feedback:ProposeRules` | off | [Monitoring](monitoring.md#feedback) |
| `Relay:Search:Enabled` | on | [Collaboration](collaboration.md#search) |
| `Relay:Attention:Enabled` | on | [Collaboration](collaboration.md#attention) |
| `Relay:Analytics:Enabled` | off | [Monitoring](monitoring.md#analytics) |
| `Relay:Analytics:AllowAdHocSql` | off | [Monitoring](monitoring.md#analytics) |
| `Relay:Orders:Enabled` | off | [Collaboration](collaboration.md#orders) |
| `Relay:DocCache:Enabled` | off | [Collaboration](collaboration.md#docs) |
| `Relay:Discovery:Enabled` | off | [Other PCs](discovery.md) |
| `Relay:Network:Enabled` | off | [Other PCs](discovery.md) |

- **Upgrade adapters together with the hub.** Older adapters use up attention offers the model never sees.
- **With attention on, messages can wait up to 10 minutes** before they wake a chat. Only urgent items wake at once.
- **Token counting only measures.** Replies change only for tools you list in `Relay:Tokens:EnforceTools`.
- **Analytics, ad hoc SQL, orders and the doc cache are off by default.** `analytics_query` and `standing_put` need an administrator token, as do `order_put`, `order_remove`, `control_request` and `doc_sync`.

## Hub settings

Use `appsettings.json`, environment variables (`Relay__Clients__0__Token`) or the command line.

| Setting | Default | What it does |
| --- | --- | --- |
| `ASPNETCORE_URLS` | `http://127.0.0.1:5271` | HTTP address. |
| `Relay:DatabasePath` | `data/relay.db` | The database, relative to the content root. |
| `Relay:SiloPort` | 21111 | Orleans silo port (loopback). |
| `Relay:GatewayPort` | 31111 | Orleans gateway port (loopback). |
| `Relay:ClusterId` | `PkRelay` | Change it, and the ports, for a second test hub. |
| `Relay:Clients:N:Token` | | A client's bearer token. |
| `Relay:Clients:N:TokenFile` | | Read the token from this file instead. |
| `Relay:Clients:N:PrincipalId` | | Who the client is. |
| `Relay:Clients:N:IsAdministrator` | `false` | Allows administrator tools. |
| `Relay:Clients:N:Repositories:M` | | Repositories the client may use. |

The hub refuses to start with no clients and no OAuth issuer.

## Addresses

| Route | What it is |
| --- | --- |
| `/mcp` | Remote MCP. Needs a token. |
| `/api/tools`, `/api/tools/{tool}` | List tools, call a tool. Needs a token. |
| `/hubs/coordination` | SignalR inbox hints. Needs a token. |
| `/dashboard/` | Live dashboard. See [Monitoring](monitoring.md). |
| `/scalar`, `/openapi/v1.json` | API reference. |
| `/health` | Health check. |

REST replies are `{ "ok": true, "code": "...", "dataJson": "{...}", "version": 1 }`. `dataJson` is a JSON string, so parse it once more. Errors use 400 (bad arguments), 401 (no valid token), 403 (denied), 404 (unknown tool or record), 409 (conflict) and 413 (too large). The stdio adapter returns the parsed `data` instead.

Tokens work only over HTTPS or real loopback. The hub ignores forwarded headers.

## OAuth (optional)

For remote clients with their own identity provider.

| Setting | What it does |
| --- | --- |
| `Relay:OAuth:Authority` | HTTPS issuer. |
| `Relay:OAuth:Audience` | Required audience. |
| `Relay:OAuth:RequiredScopes:N` | Required scopes. Default `relay`. |
| `Relay:OAuth:Resource` | Public HTTPS `/mcp` address. Publishes `/.well-known/oauth-protected-resource/mcp`. |
| `Relay:OAuth:Principals:N` | `Subject`, `PrincipalId`, `IsAdministrator`, `Repositories` for each allowed user. |

The hub does not issue tokens. A ChatGPT connection or tunnel is set up separately and has not been verified. See [Verification](verification.md).
