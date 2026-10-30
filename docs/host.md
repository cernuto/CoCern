# Relay host

The .NET 10 ASP.NET Core host runs one Orleans silo and one SQLite database. It coordinates durable tasks, claims, messages and reported evidence. It does not execute IDE tools, invoke native chats, launch models, or provide an OAuth issuer.

Build with `./scripts/Build-PkRelay.ps1`, then run `./scripts/Start-PkRelay.ps1 -RepositoryId pkrelay`. The bootstrap script creates a private local client token in the ignored `data/local-client-token` file and starts the loopback hub. Keep that file private. The host itself has no default credentials and refuses to start without enrolled clients or external OAuth configuration. The adapter configuration is described in [adapter.md](adapter.md).

## Configuration

ASP.NET Core configuration files, protected environment settings and command-line settings are supported. Nested environment keys use double underscores.

| Setting | Meaning |
| --- | --- |
| `ASPNETCORE_URLS` | Default `http://127.0.0.1:5271`; remote use requires a correctly configured HTTPS endpoint. |
| `Relay:DatabasePath` | Default `data/relay.db` under the host content root. Keep its directory private. |
| `Relay:SiloPort` | Loopback silo port, default 21111. |
| `Relay:GatewayPort` | Loopback Orleans gateway port, default 31111. |
| `Relay:ClusterId` | Default `PkRelay`; use independent identities and ports for separate test hubs. |
| `Relay:Clients:<index>:Token` | Enrolled bearer secret; no default. |
| `Relay:Clients:<index>:PrincipalId` | Trusted identity supplied to the application service. |
| `Relay:Clients:<index>:IsAdministrator` | Administrator permissions; default false. |
| `Relay:Clients:<index>:Repositories:<index>` | Exact authorized repository identities. |

Client token comparison uses fixed-time comparison of SHA-256 digests. All bearer credentials, including external JWTs, are accepted over HTTPS or an actual loopback TCP connection. An off-host plaintext request fails even if it supplies `X-Forwarded-For` or `X-Forwarded-Proto`. The host does not trust forwarded headers. Use HTTPS to the host for remote deployments; configure any tunnel or proxy separately and keep its upstream access protected. Browser CORS access is denied by default. SignalR clients should send the bearer token in authorization headers, as the supplied adapter does with long polling; query-string access tokens are not accepted.

Only one hub may own a SQLite database. Its exclusive lock, WAL mode and durable due-work index are supplied by the storage provider. A background dispatcher scans up to 64 due records per pass at startup and while running, with bounded delays and error backoff. It advances through ordered pages even when earlier owners repeatedly throw, then resets at the end of a scan. It invokes the owning grain's internal durable dispatch method. Pending work remains durable through delivery failures and restart; domain recipient deduplication makes redelivery safe. This service delivers coordination effects, and does not schedule model or IDE execution.

## HTTP and MCP

| Route | Behavior |
| --- | --- |
| `GET /health` | Public minimal process readiness response after successful host startup. |
| `GET /api/tools` | Authenticated fixed coordination catalog, descriptions and input schemas. Administrator tools are advertised only to administrators. |
| `POST /api/tools/{tool}` | Authenticated raw JSON argument object, routed to the same application service used by MCP. |
| `/mcp` | Official MCP C# SDK Streamable HTTP server in stateless mode; supports negotiation, tool listing and tool calls. |
| `/hubs/coordination` | Authenticated SignalR hints, with additional private session validation before joining a group. |
| `GET /openapi/v1.json` | Public OpenAPI metadata; shared coordination envelope and domain failure statuses. |
| `GET /scalar` | Public Scalar API reference, without embedded credentials. |

All tool arguments include `repositoryId`; established session operations also use the session's `sessionId` and secret `sessionToken`. Mutation calls require stable `requestId` identities and applicable expected versions. Open a fresh session for each chat. Request JSON cannot grant a principal, administrator role, or internal call privileges: actor identity comes exclusively from validated authentication.

REST replies are `{ "ok": true, "code": "...", "dataJson": "{...}", "version": 1 }`. The `dataJson` field contains JSON encoded as a string. Parse it once to obtain domain data. MCP returns the same envelope in a text content block, with `isError` for a failed domain operation. The stdio adapter reshapes that field to its documented parsed `data` result. An MCP transport session is not a relay chat session; private relay handles remain explicit on every applicable tool call.

REST uses 400 for malformed or invalid arguments, 401 for missing/invalid bearer credentials, 403 for repository/session/administration denial, 404 for unknown tools or absent records, 409 for recognized version/claim/state conflicts, and 413 for oversized payloads. Domain failures retain the stable `code` and version in the response. MCP domain failures retain MCP's tool-result error shape rather than imitating REST HTTP statuses.

JSON request bodies are limited to 32 KiB, including streamed bodies and MCP framing; REST parsing also bounds nesting depth. Domain reply envelopes are limited to 32 KiB before returning to clients. Narrow pagination or context requests when a result is too large. Private API, MCP and hub responses use `Cache-Control: no-store`. Serilog logs method, path, status and elapsed time without request headers, bodies or query strings. Client errors never include raw server exceptions.

## Session hints

Call `WatchSession(sessionId, sessionToken, repositoryId)` after connecting to SignalR. The hub invokes authenticated application `get_status` with that handle before joining the session group. Invalid or another session's token is denied, including for two sessions belonging to one principal. Hints use event name `coordination.changed` and contain only `{ sessionId, eventId }`; they contain no message text or session secret.

Hints are best effort. Reconnect, rewatch, then call `check_inbox` to reconcile. Reading does not acknowledge messages: call `ack` only after handling their IDs. A notification is not a durable read receipt, model wakeup or proof that an external action happened.

## External OAuth and ChatGPT

External access can validate JWT access tokens using `Relay:OAuth:Authority` and `Relay:OAuth:Audience`. The authority must be HTTPS; issuer, audience, signature, expiry and signed-token requirements are enforced. `Relay:OAuth:RequiredScopes:<index>` configures required resource scopes, defaulting to `relay`; every configured scope must occur in the standard space-delimited `scope` or `scp` claims before enrollment permissions apply. An empty required-scope configuration is not accepted. This first slice uses a single complete relay permission scope plus explicit enrollment; it does not provide separate read/write/admin scope tiers. Enrollment is explicit through `Relay:OAuth:Principals:<index>` entries with `Subject`, `PrincipalId`, `IsAdministrator` and `Repositories` fields. The validated `sub` claim must match an enrolled subject. Permissions never come from arbitrary tool JSON or untrusted role claims.

Set `Relay:OAuth:Resource` to the public HTTPS MCP resource URL, including `/mcp`, to publish protected-resource discovery at `/.well-known/oauth-protected-resource/mcp` and include its address in bearer challenges. The metadata names the configured external authority. Keep public resource identity consistent with the issuer's configured audience/resource policy. The host does not mint tokens, register OAuth clients, implement an authorization endpoint, or substitute a local client token for a ChatGPT account connection.

Real ChatGPT registration, the external issuer's authorization/client-registration policy, Secure MCP Tunnel deployment, TLS and off-host connectivity need separate environment configuration and verification. Local integration tests and the loopback smoke script do not verify those integrations or promise model wakeups.
