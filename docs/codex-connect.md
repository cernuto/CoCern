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

If the hub is unavailable, restore the connection. A checkout that cannot be mapped to an authorized repository needs its hub configuration corrected; its folder name alone is insufficient.
