# Connect Codex

Open **Connect** on the hub's PC, choose Codex, then open or resume a chat. A new connection uses the standard MCP connector. Native startup hooks and their review are not required for this path.

The packaged [CoCern workflow skill](../skills/cocern-workflow/SKILL.md) guides the agent through its session, tasks, claims and bounded inbox checks. Setup installs the owned skill at `~/.agents/skills/cocern-workflow/SKILL.md`; a customized skill is retained. Credentials and session handles are not pasted into setup or shared between chats.

**Connected · standard connection** means the app configuration is installed. The chat must load the connector before it can use tools. A stale address, changed certificate or failed configuration remains visible and offers a connection update.

Repair keeps a working standard connection in standard mode. It does not re-enable disabled native hooks, replace unrelated settings or treat the absence of optional native hooks as damage.

## Existing native connections

An existing native-mode choice is preserved. Its calls require genuine trusted hooks for the active Codex host/profile. SessionStart records a private pending intent without creating a directory row. The first genuine CoCern PreToolUse joins automatically, including when hooks were installed during the chat. A chat that never calls CoCern creates no native session. Hook trust stays under Codex's control; CoCern never writes approvals or fabricates a native event or call signature.

An explicitly configured native adapter can publish its tool catalog while waiting for a genuine hook. This removes the startup dependency that could hide the very tools needed to trigger PreToolUse after a reload. Catalog discovery is metadata only: each backend still requires its own private registration exchange and every invocation still requires its valid signed chat context. Background native recovery and wake work also wait for registration.

**Native startup verified** describes the startup, session and wake checks shown in its details. It does not establish that a particular MCP tool call was allowed, executed or read by the model. Native activation needs installed-client verification of both call signing and permission behavior; ordinary installation only stages its inactive preparation.

`native_stamp_required` means the native adapter received a tool call without the signature from PreToolUse. It is separate from the earlier native registration handshake. Repeated manual bridge probes do not establish that signature. Inspect the actual active-profile hook trust and genuine event evidence; do not copy another chat's handle, synthesize a stamp or turn an individual refused native call into a standard call.

Connect refresh runs a read-only repository preflight before requesting any chat activity. `RepositoryDenied` needs a hub-side check of the Git origin, configured repository ID and the connection's allowed repositories. Roots-discovered clones participate in origin matching. An authorized configured repository ID, or an enrolled connection's unique repository scope, also works when the hub has no clone; a conflicting known clone still refuses. Remote callers' filesystem paths never establish hub identity. After correcting the mapping or authorization, refresh Connect and make the same chat's first CoCern call. Reapproving already-trusted hooks does not fix this refusal.

`native_private_connection_required` identifies private connection verification failure; `native_handle_conflict` identifies incompatible saved chat identity. Connect reports those codes and the corresponding action instead of treating them as hook review problems. A recent native registration or backend startup failure also remains visible even when an older startup receipt exists.

A classic chat can retain its existing session when its own private handle matches the exact provider, native thread ID and repository. The hub verifies that private proof before adopting it, retaining the label, task targets, IDE bindings and inbox. Forged, stale or conflicting proof creates no replacement session. No handle or credential is included in hook output.

## One session per native chat

A native chat keeps one hub session for its whole life, keyed by its provider, its own chat ID, this PC and the repository, not by the folder it runs in. Moving the chat to another worktree or clone of the same repository keeps that session, its inbox, tasks and label: its next call reopens the same session with the new checkout's evidence, which the hub checks again, and the hub records the new working copy and protected root. Claims the session took under up to 8 earlier checkouts on the same PC can still be released and renewed after the move.

The adapter keeps these per-chat bindings privately under `sessions/native-chats` in its connection folder. Older per-checkout bindings (`sessions/native`) are adopted on the chat's first call: the one the chat used last becomes its binding, and the others stay as they are but are no longer used. If another, older adapter still holds that file, the call is refused as retryable rather than giving the chat a second session.

When the hub no longer knows a chat's saved session (for example after its data was reset, or the session closed), the chat's next CoCern call verifies that exact handle, gets `SessionUnknown`, and opens a new session for the same chat. Wake watchers stop watching the session that is gone. The adapter never resumes a nearby session by name or repository.

## Repository hint and native chats

`COCERN_REPOSITORY` sets a default for standard calls. A native chat goes by its checkout instead: a chat that already has a binding keeps that binding's repository, and a new chat whose preflight the hub refuses under the hint (`RepositoryDenied`) asks once more without it. Either way the hub resolves the checkout's evidence again and checks the credential's access, so the hint never grants anything and never strands a chat working in another repository.

If the hub is unavailable, restore the connection. A checkout that cannot be mapped to an authorized repository needs its hub configuration corrected; its folder name alone is insufficient.
