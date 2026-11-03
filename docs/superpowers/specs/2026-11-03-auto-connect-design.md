# Auto-connect: chats that find each other and work together

Status: design approved by Paul on 2026-11-03; spike 0 and the second-PC tests are done. Written by the manager chat (Claude Code) with the Codex root, hub thread AUTO-CONNECT-DESIGN-01.

**Built so far:** immediate wake for unread direct messages, tips excluded from the hold wake, 4-second adapter inbox polling, the Wireshark-style traffic table, and shorter tool guidance that points to cheaper reads. **Not built yet:** everything else under "How it works" (automatic join, native identity, one peers view, automatic message delivery, event waits, file claims from hooks, goal approval) is still a proposal. The hooks used in the spike were throwaway scripts, not the implementation.

## What Paul asked for

In Paul's words: "work out a simple way to connect with other sessions. it should be extremely easy to coordinate, cooperate, and synchronize"; "auto discover and connect"; synchronize means all of: knowing what every chat is on, keeping chats off each other's files, and waits that end on their own; "as automatic as possible, least amount of manual interaction always"; "focus on reducing complexity and reducing confusion"; "autonomous collaboration is paramount". On certificates: "extra complexity preventing people from using cocern".

## First principle: every step removes concepts

Each part of this design must leave fewer things for Paul or a model to know, type or remember. No new parallel toolsets, no second message transport, no role or alias registry, no connection rituals, one word per state. Identity comes from the clients' own hooks, never from guessing at processes or transcripts. Existing protocol fields (session IDs, tokens, request IDs, expected versions) stay underneath for compatibility, but never appear in the normal path.

## The experience

1. Paul opens a chat (Claude Code or Codex) in a repository CoCern serves. Nothing else. Within seconds it is connected and every other chat can see it.
2. Paul approves a goal once, in plain words in any chat ("Codex and the manager: make search parallel again"). Inside that goal the chats hand work to each other, wait on each other and resume without Paul relaying anything or prompting each reply.
3. Paul hears back when the goal is done, when something outside the goal needs him, or when a chat is stuck.

## How it works

**1. Join automatically.** Both clients have a SessionStart hook that receives the chat's own native ID and folder (Claude: `session_id`, `cwd`; Codex: the same from its hooks, verified by Codex in CLI 0.166). The hook registers that ID with the local adapter, which opens the hub session itself, or resumes the one already bound to that native ID. The repository comes from the folder's configuration, the machine from the hostname, and the label from the chat title. The model never calls `session_open` or `session_bind` and never sees a handle. A participant's identity is its native chat ID together with its client, the authenticated principal, the repository and the workspace; the label is display text only. One user chat is one participant, kept across adapter and hub reconnects, and Codex no longer needs transcript scanning. A forked chat is a new participant. Subagents report their parent's ID, so their work counts as delegated work under the parent, not as new peers. When a participant's hub session has expired, it gets a new one under the same identity, but it never inherits another participant's tasks or unsettled actions, and the existing explicit action-recovery rules still apply. The startup hook may fire before the MCP connection is ready, so registration retries by itself, and the first tool call awaits the same idempotent join instead of opening a second session. The MCP adapter process doesn't reliably know which chat a call comes from (Codex gives it no chat ID, and one process may serve several threads), so a PreToolUse hook on CoCern's own tools stamps each call with the chat's native ID. Both clients' PreToolUse hooks can rewrite MCP calls. A participant continues across a change of native ID only when the client itself supplies a reliable continuation link. Matching label, folder or timing never counts as one. Without such a link (for example after Claude's `/clear`, if it reports no link), the new ID is a new participant, and the old participant's actions still need explicit recovery. Compaction may keep the same native ID.

Orphaned duplicate sessions are cleaned up automatically: a session left behind by a participant that has a newer live session, or by a mistaken first report, is closed once lifecycle and ownership checks pass. Unresolved-action blocks and unread-message rules are preserved. Paul asked for this to be automatic rather than confirmed by hand each time.

Hooked clients get a short default tool list without the session tools. Clients without hooks (the ChatGPT app and OAuth clients, raw REST callers, the smoke script, the dashboard) keep today's tools and fields unchanged, so this work adds a path rather than breaking the existing one.

**2. One directory.** `worker_list` and `chat_directory` merge into one peers view. It combines the hub's durable sessions with each chat's wake route, so it is never empty just because a PC has no node. Each row is the chat's name, client, what it is on (its task and claimed files) and one state word:
- `working`: mid-turn; it will see mail on its next tool call.
- `idle`: turn ended and its wake path is live; mail wakes it.
- `asleep`: turn ended and nothing can wake it; mail waits for Paul's next prompt.
- `gone`: closed or silent past expiry; its claims are released.

These states describe the wake path only. They do not claim the model is running. A name addresses a peer only when it is unique; otherwise the call returns a short choice. The view covers one repository, matching how sessions and grants already work. Cross-repository work is out of scope for now.

A chat is `gone` when its client reports it ended (Claude's SessionEnd hook, or the Codex equivalent if one exists), when it is closed on the hub, or when it has had no activity and no live wake route for the existing idle limit (24 hours). An `asleep` chat keeps its file claims, because those are tied to uncommitted changes rather than to liveness (part 6). A chat waiting on an asleep owner is told so right away instead of hanging.

**3. One message path.** Send to a peer by name, reply to a message, done. New mail reaches the model without a ritual: a hook on ordinary turns (Claude `UserPromptSubmit` and `PostToolUse`, Codex equivalents) adds "2 new messages from Codex: ..." to the model's context. Delivery and "seen" are tracked underneath, but neither counts as handled. A message is acknowledged automatically when the chat replies to it or records handling it, so there is no separate ack call and no ack IDs to pass.

**4. The hub wakes idle chats, not the model.** Delivery and waking stay separate: a stored message is not proof that a turn started.
- Codex: an extension of the node's existing `codex queue` dispatcher, keyed by the native ID from the hook instead of a transcript search. The installed CLI supports `--resume`, but the dispatcher doesn't use it today, so the real wake still has to be proven. A failed resume can warn and still exit successfully, so exit status alone is not success; only a new turn with a correlated reply counts.
- Claude: a Stop hook with `asyncRewake: true` runs the waiter each time a turn ends. When mail arrives the waiter exits 2 and Claude Code wakes the idle chat with the notice. The waiter must also work on the hub's own PC (today it needs the node and exits 1 there). The simplest fix is for the hub's PC to run a node like every other PC, so both wake paths work the same way everywhere.

**5. Waits that end on their own.** A chat can wait for a peer's reply, a task to finish or a claim to be released. The hub turns the event into a wake for the waiting chat, built on the existing `claim_released` and `workflow_completed` triggers, not a second scheduler. If the owner goes `gone`, its ordinary claims expire and every waiter is woken with "owner gone", so no chat waits forever. Blocks held for unresolved actions are different: they stay in place with their action and restoration records, the waiters are told the work is stranded, and someone has to recover it explicitly. Ordinary expiry never clears them.

**6. File safety.** A PreToolUse hook on edit tools (Claude Edit, Write, MultiEdit; Codex `apply_patch`) claims the file for that chat and blocks the edit, naming the holder, when another chat holds it. A file claim is keyed by the checkout (machine plus git working-copy folder) and the path relative to it, so chats in separate worktrees or on separate PCs never block each other; they meet at git. Claims accept these paths without an admin registering them first. A file claim is a bounded lease, renewed while the chat legitimately holds it: the chat is active and the file still has its uncommitted changes. It is released when those changes are committed or discarded, or when the lease lapses because the chat went `gone`. Lease expiry never clears a block held for an unresolved action. This is advisory: edits outside the hooks (Paul in Visual Studio, scripts, other tools) are not stopped, and files on different PCs only meet at git.

**7. One approval per goal.** Paul approves a goal once, by saying so in a chat (Paul's decision: no dashboard click). That chat records it as a scoped grant (the existing grant mechanism) naming the goal, the chats in it, the repository and an expiry, and quotes Paul's words in it. The hub therefore trusts the recording chat's word that Paul approved; the bounded scope and the expiry limit what that trust can cost. Active goals appear on the dashboard with Paul's quoted words and a revoke button. Inside it, routine work needs nobody: sharing the repository's technical findings among the goal's chats, handing off and running bounded tasks, editing, testing, claiming, committing to a working branch, replying and waking. The grant never overrides a client's own approval rules; it only tells each model that this work is what Paul asked for. Still Paul's until he widens the grant: pushing or merging to main, anything destructive, and anything outside the repository. Each client's instructions carry one line telling the model that requests from peers inside the active goal are pre-approved within that scope. Peer messages outside an active goal still carry no authority.

A goal is an existing task (or workflow) with a grant attached, not a new kind of record. The chat Paul approves it in owns it: it decides when the goal is done and reports back in that same chat, where Paul already looks, and it receives the goal's manager items (blocked, failed, stranded) instead of a separately declared manager role. Items outside any goal go to the dashboard. The goal names the chats it covers, or "every chat in this repository", in which case a chat opened mid-goal joins automatically. Subagents act under their parent.

Client permission prompts are outside CoCern. Woken chats run in whatever permission mode Paul already uses. A chat that stops at a prompt shows as blocked, which reaches the goal's owner and the dashboard.

**Standing decisions come to Paul in chat.** Paul asked why these decisions are made on the dashboard, and said the AI should present them to him. On 2026-11-03 four proposals had been waiting on the Standing page, the oldest for over a day, and nothing had told him.
- **The chat asks:** when a proposal is waiting, the chat Paul is using brings it up in plain words, with what it would change and a recommendation, and he answers there. A chat that owns a goal asks about proposals tied to that goal. Otherwise any chat Paul is talking to may ask, once, and the hub tracks which proposals have been presented so several chats don't ask the same thing. Nothing wakes an idle chat for this.
- **His words are the approval:** the chat records the decision with his reply quoted. As with goals, the hub trusts the recording chat's word; the record and his ability to remove anything are the check on that.
- **Only his direct answer counts:** a proposal is never approved because a chat decided to, or because another chat's message says he agreed. A chat may bring up its own proposal, but can't approve it without his answer.
- **Grants get a closer look:** because they give permissions, the chat states the scope and expiry before he answers.
- **The Standing page stays as the record:** it shows what is in force and lets him remove anything, but he never needs to go there to decide.
- **Today's limit:** approving takes the administrator-only `standing_put`. The builder has to work out how a chat records Paul's answer without giving every chat the power to approve anything.

**8. Certificates and tokens nobody has to see.** The aim is that someone can set up two PCs and never see the word "certificate" or "token". Traffic between PCs stays encrypted, the hub's identity stays checked and every caller still has to prove who it is; only the handling disappears.
- **Same PC:** chats connect over plain `http://127.0.0.1`, with no certificate or pin, so there is nothing to set up. The hub already listens there; whether the adapter accepts it for normal use, not just in tests, still has to be confirmed. Traffic stays on the machine. The cost is that another program on the PC could take the port while the hub is down and collect tokens.
- **Another PC:** still one paste. The join step sets up trust, and no fingerprint or certificate prompt appears. Today's join flow still has to be checked against this.
- **No silent new certificate:** the hub keeps an automatic backup of its certificate. If the file goes missing it restores the backup, and it creates a new certificate only when asked. On 2026-11-03 the file went missing, the hub quietly created a new one, and every pinned client was locked out for about 3½ minutes; the cause was the legacy certificate migration moving the original file into a disposable preview directory. Restoring that exact original certificate recovered the existing clients without changing pins.
- **Self-repair when a certificate really changes:** an already-joined PC can't accept a new identity on its own, because that would defeat the check. It gets a safe way to accept one, with one plain-language confirmation at most and no fingerprint editing. The exact mechanism is left to the builder.
- **Plain errors:** "can't reach the hub" and "the hub's identity changed" become separate messages, each saying what to do. Both show as `hub_unavailable` today.
- **Token on the same PC:** the hub creates it on first run and chats find it by themselves, so no config file holds a path such as `data/local-client-token`. The location has to be one both the hub and packaged desktop apps can read, because Windows redirects those apps' writes under `%LOCALAPPDATA%` into private folders (seen with Claude and Codex on 2026-11-03).
- **Token on another PC:** nothing new for the person joining. The one-paste join already swaps the join code for that PC's own credential.
- **Plain language:** setup, the dashboard and error messages never say "token". For example: "This PC isn't connected to your hub yet. Connect it?" A rejected token gets its own message saying what to do.

Not doing: removing encryption between PCs, requiring a separate VPN, which would swap one piece of setup for another, or dropping the token. Without it, other programs on the PC, websites open in a browser and other devices on the network could read a chat's messages or send it instructions, and chats act on their messages.

**9. A manager chat does the admin work.** Paul said a manager session should perform most administrative tasks with tools and minimize interactions with the dashboard. The dashboard becomes somewhere to look (what happened, what is in force, a way to undo things) and the fallback when the hub or a chat is broken, not somewhere he has to go.
- **Admin tools for managers:** declaring `role: "manager"` today only routes alerts, and the admin tools appear only with `COCERN_ROLE=admin` in the app's settings. Setup offers this as one choice. It costs about 600 tokens of tool definitions per chat (4,659 against 4,061).
- **Missing tools:** `add_pc` returns the command to paste on the new PC, `connect_app` connects an app to the hub, and `hub_diagnose` reports hub health, certificate mismatches and each PC's adapter version. Today these exist only on the dashboard, or not at all.
- **Nodes update themselves:** a node fetches the new build from its hub and restarts between wakes, instead of a side-by-side install, a settings change and a sign-out on each PC. The mechanism is left to the builder; a new build must be verified before it runs, and a failed update must leave the old node working.
- **Alert backlog:** a manager can clear several attention items in one call, and alerts about sessions that have closed stop lingering.
- **No repeat wakes:** a wake fires only for mail the chat hasn't already been given.
- **Safeguards:** an approval made with admin tools must include Paul's quoted reply, and the dashboard shows it. Messages from other chats never count as approval. Every grant has a scope and an expiry. Pushing to main and destructive actions stay with Paul.
- **The honest limit:** these limit the damage and make it visible, but the hub can't prove Paul was the one who said yes, so a chat could approve its own proposal or be talked into it by a bad message. The risk isn't entirely new, because the token on the hub's PC is already an administrator token and hiding the tools is a soft barrier. For a single-person setup this is an acceptable trade; a shared hub would need more.

**10. Features choose themselves.** Paul asked how a chat can determine what to turn on, given all these features. A chat shouldn't have to reason through a list of switches; that only moves the complexity onto the model.

| Kind of feature | Who decides | Examples |
|---|---|---|
| Safe and useful everywhere | Always on, no switch | Wakes, cheap reads, `wake:false` messages |
| Depends on the chat's job | Comes with the role | A manager gets analytics and admin tools; a worker doesn't |
| Costs tokens but no risk | Loaded when needed | Rarely used tools, listed by name and loaded on first use |
| Gives more power | Paul, asked once in plain words | Approving rules, granting permissions, pushing to main |

- **The safety rule:** a chat can turn on anything that only reads or saves effort. Anything that gives it more power needs Paul's yes. A chat never grants itself more power because the work seemed to call for it.
- **At startup:** a chat learns its role, and the hub gives it a short summary of what's on and which tools are worth using for that role, not the whole feature list.
- **When something is missing:** the reply says what's missing and how to get it, such as "Analytics is off for this chat. Ask Paul to allow it?"
- **While working:** analytics feed back in; if a chat's reads are large, the hub suggests a cheaper one, as its tips already do.
- **Settings move to the hub, per session:** `COCERN_ROLE` and `COCERN_PROFILE` are read when the app starts, so changing one means a restart. On the hub, a chat can gain a tool without restarting, and those switches go away. A client may keep the tool list it loaded when it connected, as the launcher hot-swap result below shows, so the builder has to confirm how a new tool reaches a running chat.
- **The one choice left is the role, and it can be automatic:** the chat Paul is talking to is the manager, and the chats it hands work to are workers. This replaces the setup choice in part 9. How it works when Paul talks to several chats at once is left to the builder.

## What disappears

- Calling `session_open`/`session_bind`, choosing labels, guessing machine and repository IDs.
- Session IDs, tokens and request IDs in model-facing calls.
- Two directories (`worker_list` and `chat_directory`), replaced by one peers view.
- Five presence fields (`availability`, `observedAt`, `freshUntil`, `activeAt`, `stale`) in what chats and Paul see, replaced by one state word. The fields stay internally for diagnostics.
- `check_inbox` polling, `waitSeconds` loops, separate ack calls and manually started waiters.
- Transcript scanning to find Codex chats.
- Expected-version boilerplate on routine handoffs.
- Admins registering resources before files can be claimed.
- Mail bouncing off a closed session that has quietly been replaced.
- Paul relaying approvals message by message.
- Visiting the dashboard to find and make Standing decisions.
- `COCERN_ROLE`, `COCERN_PROFILE` and restarting an app to change what a chat can do.
- Certificates, fingerprints, pins, tokens and token file paths in anything a person sets up or reads.

## Acceptance gate

The design is done only when all of these pass on real chats:

1. **Ended-turn wake, both directions.** Claude and Codex both idle with their turns ended. One event (a message, a handoff or a finished dependency) reaches one of them. That chat wakes and sends a reply carrying the event's correlation ID, with no human input. Run Claude to Codex and Codex to Claude.
2. **Zero manual steps.** On a PC that has been set up once, nobody types an ID, a setup command or a connection request between opening a chat and its first peer message. The one-time setup per PC is a single command, plus Codex's own hook approval in `/hooks`, which Codex requires and CoCern can't skip.
3. **Hub restart mid-collaboration.** After a restart, both chats are back under the same identity and the conversation continues without any model action.
4. **Owner disappears.** A chat holding a claim and an unresolved action is closed. Its ordinary claims expire and its waiters are woken with "owner gone". The unresolved action's block and records stay until someone recovers them explicitly, and the waiters are told the work is stranded.
5. **Edit clash.** Two chats edit the same file; the second is blocked and told who holds it.
6. **No certificate or token in sight.** Someone sets up two PCs from scratch and never sees a certificate, fingerprint, pin, token or token path. A caller with no valid token is still refused. Deleting the hub's certificate file and restarting the hub locks nobody out. A real identity change produces the plain "identity changed" message and can be accepted without editing a fingerprint, and a client never accepts one without that step.
7. **Decisions come to Paul.** A new proposal is raised once by the chat Paul is using, without his opening the dashboard. His answer takes effect with his words on record. A peer message claiming he approved changes nothing.
8. **Manager without the dashboard.** A manager chat approves or dismisses a proposal on Paul's answer, closes sessions, reads analytics, produces the add-a-PC command and diagnoses a hub problem, all by tools. Sign-in and removal on the dashboard still work. A new build reaches a joined PC's node with no action on that PC.
9. **No feature switches.** A new chat works with nobody choosing a profile or role. A worker that needs something it lacks is told what's missing and how to get it. A chat can't gain a power-giving tool without Paul's yes on record.

## Limits we are keeping honest about

- **Claude wake in the desktop app: verified in spike 0.** A Stop hook with `asyncRewake` woke an idle desktop Code-tab chat on hub mail. The fallback, Claude Code channels (research preview, needs a development flag), is no longer needed.
- Hook timeouts are enforced on the Claude waiter; it must re-arm before its timeout or stay inside the longest allowed one.
- File claims are advisory, as in part 6.
- Authority comes only from Paul's goal approval, never from what a peer message says.

## Order of work

0. **Spike (throwaway):** on this PC, prove three things: a Stop hook with `asyncRewake` wakes an idle Claude desktop chat; `codex queue --resume` wakes an idle Codex chat keyed by its hook-supplied ID; and a PreToolUse hook can stamp the native chat ID onto a CoCern tool call in both clients. If the Claude wake fails, stop and rethink before building anything else. A second PC joins only after the ended-turn wake passes locally in both directions; it then proves cross-machine discovery, handoffs and reconnect and recovery.
1. Hooks, automatic join and native identity.
2. The peers view and the message path with automatic delivery.
3. Wakes: the Claude Stop-hook waiter on every PC, and Codex queue keyed by native ID.
4. Event waits and claim expiry.
5. Goal approval, and Standing decisions presented in chat, which share the same recording path.
6. File claims from edit hooks.
7. Certificates and tokens nobody has to see (part 8). The backup-and-restore and plain-error pieces are small and can go first, because they prevent a repeat of the 2026-11-03 lockout. This starts after the current deploy.
8. A manager chat does the admin work (part 9). Admin tools by setup choice, `hub_diagnose` and the repeat-wake fix are the small pieces and can go first; node self-update is the largest.
9. Features choose themselves (part 10). The "what's missing and how to get it" replies are small and can go first; moving settings to the hub per session is the larger piece, and it would replace the part 9 setup choice, so sequence the two together.

Proposed ownership, to confirm: Codex owns the Codex hooks and queue, and the hub-side changes (it owns hub builds and restarts); the Claude side owns the Claude hooks and waiter; the manager integrates.

## Spike 0 results so far (2026-11-03)

- **Claude ended-turn wake: pass.** A Stop hook with `asyncRewake` ran a waiter on the chat's hub inbox (through the adapter over stdio). The turn ended and the waiter armed at 10:06:58 local. A Codex message arrived at 10:07:49 while the chat was idle, the waiter exited 2, and Claude Code started a new turn in the desktop Code tab with no human input. The same path later woke the chat for each Codex report during the document-cache test.
- **Claude PreToolUse stamping: pass.** A PreToolUse hook rewrote a `mcp__cocern__send_message` call through `updatedInput`, and the message arrived carrying the native chat ID. The hook was picked up from `.claude/settings.local.json` with no restart.
- **Lesson:** a waiter that starts while handled-but-unacknowledged mail is in the inbox wakes the chat again for mail it already dealt with. The real waiter must skip mail the chat has handled in its last turn, which is one more reason to acknowledge on reply or recorded handling.
- **Codex ended-turn wake: first run failed as configured; superseded by the plain-queue pass below.** `codex queue --thread <id> --message <text> --resume` (CLI 0.166.0-alpha.5.1) queued the message and exited 0, but warned "could not resume thread …: start a local daemon or specify a server with --remote". No queued turn started. (Paul's own prompt had started a Codex turn one second earlier, so this run didn't test a truly idle chat either.) Later the queued message did start a Codex turn with no human prompt, as soon as Paul's prompted turn ended, and Codex replied with the nonce. So queue delivery at the end of a turn works even when resume fails, but waking a truly idle Codex chat is still unproven. Next step: wake through the desktop app's app server (`--remote`) or a supported local daemon, then rerun with Codex truly idle. Queue receipt and exit code alone must never count as a wake.
- **Cross-session document cache: pass.** Each chat blind-read the other's fixture code through `doc_search`/`doc_section` only. A revision update, duplicate headings and a 6-page continuation (lines 0001–0600, no gaps or repeats, one commit) all matched. Both directions were driven by hub messages waking the Claude chat.

- **Two-way idle wake: pass (10:23–10:24 local).** Claude's waiter armed at 10:23:20 with its turn ended. Codex was verified idle at 10:24:13: its rollout file showed `task_complete` and no change for 30 seconds. One `codex queue` without `--resume` exited 0 at 10:24:26 ("Queued message 01a1a2a7…"). The idle Codex desktop chat started a turn by itself and replied ACK IDLE-QUEUE-8926-03, and that reply woke the idle Claude chat at 10:24:31. So the Codex wake path is a plain `codex queue`, picked up by the desktop app's own engine, with no daemon and no `--resume`. The dispatcher should drop `--resume` and verify the wake by the new turn and reply, never by exit status.
- **Gate:** the local ended-turn wake passes in both directions, so the second PC is next.

## Second PC results (2026-11-03, DESKTOP-8IV8M8M)

- **Joining needed a manual step.** The PC enrolled at 18:28Z and its node announced, but it showed no chats until a chat there called `session_open`, so `chat_directory` was empty while the PC was connected. This is the step part 1 removes.
- **Messaging: pass both ways.** The remote Codex chat (session 104fd233) and the Claude manager chat on 16XAURORA exchanged correlated nonces (XPC-CLAUDE-7742-01). Remote messages woke the idle Claude chat through its Stop-hook waiter.
- **Docs: pass.** The remote chat read `docs/reference.md#stranded-actions` at the same commit as both 16XAURORA chats. A search of a repository its principal isn't allowed got `RepositoryDenied`, as it should.
- **Task handoff: pass.** The remote chat claimed and released XPC-COLLAB-20261103-04 (claim generation 1), and the Codex root claimed it as generation 2. A release with the stale generation 1 got `StaleClaim`.
- **Cross-PC idle wake: pass, slowly.** The challenge XPC-IDLE-8926-05 was stored at 18:44:09Z. The hub logged a wake for the idle remote chat at 18:53:28Z, the node started a new Codex turn by itself, and it read and acknowledged the challenge at 18:53:50Z. Total latency was 9 minutes 41 seconds, from the default 10-minute attention hold. That is far too slow for autonomous handoffs, so a message meant for a peer should wake it at once.
- **Fixed: immediate wake for direct messages (19:17Z).** After the Codex root deployed the policy change (unread direct messages wake at once; tips never count toward the hold), the challenge XPC-FAST-8926-06 was stored at 19:17:30.136Z, read by the idle remote chat in a natively woken turn at 19:17:43Z, and acknowledged at 19:17:53.251Z: about 23 seconds end to end, down from 9 minutes 41 seconds. The immediate wake wrote no `kind='wake'` activity row (the hold wake did), so it still needs logging to be measurable from the hub.
- **Bug found (now fixed).** The hold wake fired from a low-priority tip (created at 18:43:24Z), about 41 seconds before the challenge's own deadline, although the reference says tips never wake a chat. `SessionAttention.Status` counts every eligible item toward the hold, tips included. Fix it after testing.
- **The spike waiter was the hub's noisiest caller.** Its `check_inbox` with `waitSeconds=50` made the adapter poll the hub every 2 seconds: 47 inbox calls in 2 minutes for one message. The real waiter must be event-driven (a hub push, or a long poll held on the hub), not an adapter loop. The spike waiter now makes one plain read every 4 seconds, and the adapter's own inbox wait polls every 4 seconds instead of 2.
- **Hold timer is safe from polling.** Background readers (the node watcher, adapter inbox waits and the Claude waiter) send `attention: false` and never move the hold timer. Only the chat's own boundary calls do.

- **Launcher hot swap: binaries yes, guidance no.** After the development launcher published e5fc1f8, a running Claude chat's launcher kept its process and swapped its child to the new build with no restart. But Claude Code kept the tool descriptions and server instructions it loaded at connect time, so new guidance reaches a chat only when it reconnects or restarts.

- **Updated-adapter cross-PC retest: pass (2026-11-03, 20:38–20:44 UTC).** The existing three sessions exchanged exact nonce acknowledgments through CoCern without opening duplicate sessions. Codex on DESKTOP-8IV8M8M resumed through a native queued wake, with inbox reads about 19 seconds and 30 seconds after separate challenges; the final positive control resumed it in about 10 seconds. Its installed adapter was e1743e1, and its checkout included the e5fc1f8 Claude launcher setup follow-up. Claude's ended-turn Stop-hook waiter woke in about 3.5 seconds for the remote Codex message and 3.9 seconds for the refreshed-launcher challenge. The opposite peer reply arrived during one active bounded Codex inbox wait, so that reply is a collaboration pass, not an additional idle-wake proof.
- **FYI quiet test: pass within the tested window.** After both peers ended their turns, `wake:false` markers remained unread for about 81 seconds on remote Codex and 88 seconds on Claude before separate actionable positive controls. Neither peer observed an FYI-only model turn; the hub recorded no calls explicitly marked model-facing during the quiet interval (legacy watcher calls remained unclassified), and the local Claude transcript independently had no user or assistant entries. Claude's positive control woke its waiter in about 1.3 seconds. Claude's silence depends on a scratch waiter filter for `wake:false`; it does not prove a finished product Claude wake integration. The remote Codex inactivity check used its visible native chat history, not an independently inspected rollout file.
- **Cross-PC document cache read: pass with repository scope preserved.** Remote Codex searched and read `spike-0-results-so-far-2026-11-03` from this document through CoCern, reporting commit `e1743e12d75b`; the root independently read the same section and commit. Its attempt to read the separate fixture repository correctly returned `RepositoryDenied`, with no permission expansion. Claude independently read `gamma-receipt` from the authorized fixture repository at commit `2087a7514f9b` and returned the expected receipt.
- **Retest limits.** This run proves the remote Codex node wake path and the local Claude spike waiter, plus cross-PC message exchange and document reads. The root Codex chat remained active while coordinating; its own ended-turn wake was not repeated in this run. The local launcher was published from an isolated e5fc1f8 build, and both existing launcher clients swapped to that binary without a host restart. Launcher/setup/connect regression tests passed 116/116 using isolated repository artifacts.

## Calls made while writing this (for Paul to check)

These were settled by the manager and Codex, not by Paul:
- A PreToolUse hook stamps each CoCern call with the chat's ID. A changed native ID stays the same participant only when the client supplies a reliable continuation link.
- Clients without hooks keep today's tools, so this work adds a path rather than replacing one.
- The peers view covers one repository; cross-repository work is out of scope for now.
- The hub's PC runs a node like every other PC.
- File claims are per checkout, so separate worktrees never clash.
- The chat Paul approves a goal in owns it, reports back there and receives its alerts, which replaces declaring a manager role.
- Client permission prompts stay outside CoCern; a chat stuck at one shows as blocked.
- One-time setup per PC is allowed; gate 2 counts from a set-up PC.
- Same-PC chats give up certificate checks for plain local HTTP; the hub restores its certificate rather than creating a new one; traffic between PCs stays encrypted with no VPN; the token stays but is created and found automatically. Paul approved adding this plan to the design.
- Standing decisions are raised once by the chat Paul is using and never wake an idle chat for this. Paul approved adding the idea; these details are the manager's.
- The manager's tool names, its safeguards and accepting the self-approval risk for a single-person setup. Paul approved adding this plan to the design; the details are the manager's.
- The four feature categories, the rule that power needs Paul's yes, and making the chat he talks to the manager. Paul approved adding this; the details are the manager's.

## Left to the builder

Tool names and exact shapes; where the native-ID-to-session map is stored; lease lengths and renewal intervals; the wording of wake notices; how the one instruction line is installed for each client.
