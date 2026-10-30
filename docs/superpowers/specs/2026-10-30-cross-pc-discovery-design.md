# Cross-PC discovery and wiring

Oct 30, 2026 · design for Paul

## Intent

PkRelay already lets chats on one PC message each other, and a Codex chat can be woken automatically. Setting that up is manual: someone opens sessions, copies private session files, finds thread IDs and writes a wake config per route. None of it reaches other PCs, because the hub listens only on `127.0.0.1`.

The goal is that chats on any number of PCs on the same network find each other through the hub and can wake each other, with nothing typed or copied per PC beyond installing and starting PkRelay once. A **discovery grain** in the existing Orleans hub owns identity and wiring: which machines exist, which chats are connected on each, which relay session each chat uses, and how each chat is woken. Claude or Codex decide what to do next; the relay still never assigns work.

## Goals

- One PC hosts the hub. Any number of other PCs join and leave over time.
- A PC joins with no join code, no copied token and no hand-written config. The only thing typed on a new PC is the hub's address, once, at setup; after that it reconnects and announces itself on its own.
- PCs announce what they have; the discovery grain decides each machine's identity, each connected chat's relay session, and each chat's wake route, and sends those decisions back.
- Every PC's watcher takes its wake routes from the grain and picks up new chats without a restart.
- Agents can see which chats are reachable on which machine and message them.

## Non-goals

- Assigning, prioritizing or scheduling work. The grain decides identity and wiring only.
- Multiple hubs or an Orleans cluster spanning PCs. There is one hub silo with one SQLite file, as today.
- Waking chats on another PC directly. Each PC wakes only its own chats; `codex queue` and the Claude waiter are local.
- Making every chat on a PC reachable. Only chats that have connected to the relay take part.
- Remote access beyond the local network (internet, tunnels, external OAuth).

## Anti-goals

- A message reviving an old chat that never connected to the relay.
- Any automatically enrolled machine gaining administrator powers (pause, stop, register resources, revoke).
- Tokens or session secrets appearing in logs, tool output, the directory, or repository files.
- Breaking the existing single-PC setup, existing sessions, the start script, or the current wake and waiter modes.

## Decisions Paul made

1. The hub runs on one PC; the number of other PCs is dynamic.
2. The hub listens on all network interfaces.
3. Joining is frictionless: no join code and nothing typed per PC.
4. PCs are not on a shared Windows account, so Windows sign-in cannot be used for trust. Any machine that can reach the hub may announce itself. Paul was told this lets any device on the network join and send messages that wake agents; he chose frictionless joining anyway. The limits under "Trust" below are the mitigation.
5. A PC sends an announcement to the discovery grain, and the grain determines what follows: which session a chat uses, how it is woken, and so on.
6. The grain decides identity and wiring only. Claude or Codex decide what to do next.
7. Only chats that have connected to the relay (called `session_open`) are announced and can be woken.
8. A PC finds the hub only by its address, typed once at setup. There is no automatic hub discovery (no broadcast beacon, network scan or name lookup), because broadcast and scanning are unreliable across firewalls, client-isolated Wi-Fi and VPNs.

## What exists today

- `PkRelay.Host`: ASP.NET Core plus one Orleans silo using localhost clustering, SQLite grain storage, REST at `/api/tools/{tool}`, remote MCP at `/mcp`, and SignalR hints at `/hubs/coordination`. Authentication is a bearer token matched against configured `Relay:Clients`, or optional external OAuth. A client's token can come from a file (`TokenFile`). Plain HTTP is accepted only from loopback (`RelayAuthenticationHandler`).
- Four grains: control (session directory, resources, pause), session (private inbox), board (tasks), lock (resource claims). Rules live in `PkRelay.Domain`; grains only persist and deliver the outbox.
- `PkRelay.Adapter`: the stdio MCP server agents use; `--wake` mode, which queues a fixed inbox-check prompt into enrolled Codex chats with `codex queue`; and `--await-inbox` mode, which a Claude Code session runs as a background command so it wakes when a message arrives. Both modes read a static JSON config listing routes, each with a private session file, thread UUID and machine name. The adapter refuses plain HTTP to anything other than loopback.
- Mapping a relay session to its chat was done by hand on Oct 30: Codex stores each chat as `~/.codex/sessions/YYYY/MM/DD/rollout-<timestamp>-<threadId>.jsonl`, and Claude Code as `~/.claude/projects/<project>/<sessionId>.jsonl`. Searching those files for the relay session ID found the right chat, and the thread or session ID comes from the filename.

## Design

### Parts

- **Hub** (one PC): the existing host, now listening on all interfaces over HTTPS, accepting machine announcements, and hosting the discovery grain.
- **Node** (every PC, including the hub's own PC): a new long-running adapter mode, `--node`, that replaces hand-written wake configs. It connects to the hub at its remembered address, announces the machine, maps connected chats, keeps private session handles, and wakes this PC's Codex chats from routes the grain returns.
- **Stdio adapter** (per chat, as today): unchanged as an MCP server, except that it finds the hub through the local node's state instead of `PKRELAY_URL`/`PKRELAY_TOKEN_FILE` when those are not set, and it hands each successful `session_open` handle to the node.
- **Claude waiter** (per Claude chat, as today): `--await-inbox` gets its route from the node instead of a hand-written config.

### Hub on the network

- Kestrel listens on all interfaces over HTTPS, plus the existing loopback HTTP endpoint for local clients.
- The hub generates its own certificate on first network start and keeps it under `data/`. No CA, no manual install.
- Opening the Windows firewall for the hub's port is a one-time step on the hub PC that Paul performs; the hub or scripts must not change firewall settings themselves. Document the exact rule.

### Joining: trust on first use

- At setup the node is given the hub's HTTPS address, connects, and pins the certificate the hub presents on first use. Later connections accept only that fingerprint. If the hub's certificate changes, the node stops and reports it rather than silently trusting the new one.
- The node sends an **announcement** to an unauthenticated enrollment endpoint. On first contact the discovery grain creates a machine identity, issues a machine credential, and returns it. The node stores the credential privately (current-user-only file permissions) and authenticates every later call with it.
- A machine is identified by a stable machine ID the node generates once and stores locally, not by its name or IP address, which can change or be duplicated. Re-announcing with the same ID and credential returns the same identity.
- Credentials are stored hashed at the hub. Revoking a machine is an administrator action at the hub and invalidates its credential; its chats leave the directory.

### Trust: limits on enrolled machines

- An enrolled machine's principal can use the coordination tools (sessions, tasks, claims, messages, context) within the repositories listed in `Relay:Discovery:Repositories`. It cannot use administrator tools: pause, resume, stop, cancel, `register_resource`, or revocation. Those stay with the hub PC's configured administrator client.
- Message text stays untrusted data, as today. Wake prompts stay fixed and never include message text.
- Existing rate limits on wakes stay in force, per chat route.
- The enrollment endpoint is rate-limited and capped in how many machines it will hold, so a misbehaving device cannot flood the directory.

### The discovery grain

A new aggregate in `PkRelay.Domain` with its grain in `PkRelay.Core`, persisted like the others. It is the single source of truth for:

- **Machines:** machine ID, display name, last addresses seen, client software present (Codex, Claude Code), credential hash, first and last seen, and liveness.
- **Connected chats:** provider (Codex or Claude Code), the provider's thread or session ID, the machine it lives on, the relay session it uses, its wake mode, a display label, and last seen.
- **Decisions returned to announcements:** for a machine, its identity and credential on first contact; for each connected chat, its relay session and wake route.

Rules:

- A chat enters the grain only after it has called `session_open`. Announcing a chat that has no relay session is rejected.
- One provider thread maps to one current relay session. If the same chat opens a new relay session later, the grain records the new one and the old route stops being woken.
- Liveness: a machine re-announces on a fixed interval. A machine that misses its deadline is marked away, and its chats are shown as not reachable until it returns. Records are kept, not deleted, so a returning PC resumes its identity.

### Connecting a chat

1. A chat calls `session_open` through its stdio adapter, as today.
2. The stdio adapter passes the returned private handle to the local node, which stores it privately. The handle never goes to the hub or into a file the agent is told to read.
3. The node maps the relay session to its chat by searching the provider's local chat records for the relay session ID and taking the thread or session ID from the matching filename. This is the method verified by hand on Oct 30.
4. The node announces the chat (provider, thread ID, relay session ID, label) to the discovery grain, which records it and returns its wake route.

### Waking

- **Codex chats:** the node runs the existing wake reconciliation for every Codex route the grain returns for this machine. It refreshes routes on every announcement cycle and on a change hint from the hub, so new chats need no restart. Existing deduplication, uncertainty handling, rate limits and stop file carry over.
- **Claude Code chats:** a Claude session still starts its own `--await-inbox` waiter, because Claude Code has no safe way to queue a turn from outside. The waiter gets its route from the node, and the node reports to the grain whether the waiter is currently running, so the directory can show whether the chat is reachable right now.

### Directory for agents

- `worker_list` (or a new read tool) shows, for each connected chat: label, provider, machine name, relay session ID to message, and whether it is reachable now. It never shows tokens, credentials or provider thread IDs.
- Agents choose recipients and decide what to do; the grain does not suggest or assign work.

### Compatibility

- The existing loopback client token, start script, Visual Studio launch profile, sessions, `--wake` configs and `--await-inbox` configs keep working unchanged.
- The node is opt-in per PC by starting it; a PC without a node behaves exactly as today.

## Calls made by Claude while writing this (check these)

Paul asked Claude to choose these two:

- **Repositories for enrolled machines.** A new hub setting, `Relay:Discovery:Repositories`, lists the repositories every automatically enrolled machine may use. It defaults to `["pkrelay"]`. It is deliberately not the union of the configured clients' repositories, so an unknown device never inherits the administrator's full scope. The directory shows only chats in the caller's permitted repositories.
- **What "set up once" means on a new PC.** Copy the published PkRelay folder to the PC and run its setup command once. Setup registers a per-user logon task (no administrator rights) so the node starts at every sign-in, and adds the PkRelay adapter to that PC's Codex and Claude Code MCP settings so chats there can reach the relay with nothing else typed. Before editing a client's settings file, setup keeps a timestamped backup, makes the change idempotent (re-running does not duplicate entries), leaves every other setting untouched, and can be skipped with an opt-out flag. Setup never changes firewall rules.

- **Which credential owns a chat's session.** Sessions can only be used by the principal that opened them. On every PC, including the hub PC, a stdio adapter with no `PKRELAY_URL`/`PKRELAY_TOKEN_FILE` set uses the local node's machine credential, so the node can check that chat's inbox. The hub PC's node enrolls as an ordinary machine and never uses the administrator token. A chat explicitly configured with the administrator token keeps working exactly as today but is not discovered or woken by the node. The session ownership rule in `SessionAggregate` stays unchanged.
- **Revocation.** Revoking a machine invalidates its credential and puts its machine ID on a deny list, so that ID cannot re-enroll; a revoked node stops and reports it rather than re-enrolling. Under open enrollment a device can still enroll again under a new machine ID. Keeping a hostile device out entirely is not possible without giving up frictionless joining, and is out of scope.
- **Mapping a chat to its thread.** Searching transcripts for a relay session ID becomes ambiguous once the directory shows session IDs to other chats. The mapping first tries to learn the chat ID directly (from the client's environment or the adapter's parent process); otherwise it matches only the record of that chat's own `session_open` result. It never searches for, or stores, a session token or credential. The builder verifies which works for each provider before building on it.
- **Static configs and the node.** The node keeps its own wake state. Running a hand-written `--wake` or `--await-inbox` config for the same chat as a node is not supported, because both would wake it; the docs describe migrating by stopping the old watcher. Existing static configs keep working when no node covers the same chat.

## Left to the builder

- The announcement interval and liveness timeout.
- Certificate generation details, key storage and rotation.
- The exact shapes of the enrollment endpoint, announcement payloads and grain operations, and whether the directory extends `worker_list` or is a new tool.
- How the stdio adapter hands a session handle to the local node (for example a private local file drop or a loopback-only endpoint).
- The node's local data layout, and whether `--node` is a new mode or grows out of `--wake`.
- Caps and rate limits for enrollment.

## Risks and things to verify first

- **Chat mapping depends on client storage.** Matching relay sessions through Codex's and Claude Code's local chat records relies on their internal file layouts, which can change. Isolate it behind one component per provider and fail visibly (chat shown as connected but not wakeable) rather than guess.
- **Open trust.** Any device on the network can enroll. The limits above reduce the damage, but a hostile device can still message and wake agents within the coordination tools.
- **Two agents waking each other** can loop. Existing rate limits apply; document how to stop it.
- **Firewall and network.** The hub PC needs one inbound TCP firewall rule. A PC on another subnet or a VPN works as long as it can reach the typed address.

## Acceptance

- Copying the published folder to a second PC and running setup once, typing only the hub's address, enrolls it, and it appears in the directory. After a reboot it rejoins on its own.
- Setup adds the adapter to that PC's Codex and Claude Code MCP settings, keeps a backup of each file, and re-running setup changes nothing further.
- A Codex chat on PC B that calls `session_open` appears in the directory within one announcement interval, and a message sent to it from a chat on PC A wakes it with no further setup.
- A Claude Code chat whose waiter is running is shown reachable, and a message to it wakes it.
- An enrolled machine is refused administrator tools.
- Stopping a node marks its machine away within the liveness timeout; restarting it resumes the same identity and chats.
- The existing test suite still passes, and the single-PC setup works unchanged.
