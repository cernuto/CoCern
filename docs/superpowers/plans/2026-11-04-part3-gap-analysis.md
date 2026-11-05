# Part 3: one message path, gap analysis and build order

Note only; nothing here is built. Basis: `docs/superpowers/specs/2026-11-03-auto-connect-design.md` part 3, read against main 19cf699, the feature trunk 2a79671, the integration at 49e215a and one-time delivery at 5265f1e on 2026-11-04. Paths are under `src/`. Re-confirm line references before building: main has moved since. The order was approved by the manager.

## What exists

| Element | State | Where |
|---|---|---|
| `ackMessageIds` on `send_message` | Exists | `CoCern.Core/ReplyAddOns.cs` |
| Seen handling | Exists: closes info items, never the inbox | `AttentionSeen`, `CoCern.Domain/SessionAttention.cs` |
| `check_inbox` hints | Exists, generic | `RelayToolCatalog.cs`, `AdapterInstructions.cs` |
| `replyTo` | Partial: stored only, acknowledges nothing | `CoCern.Domain/SessionAggregate.cs` |
| Recorded handling | Partial: `attention_respond` accept closes the item but keeps the inbox message | `SessionAttention.cs` |
| `UserPromptSubmit` hook | Partial: counts only, 30 s cache, opt-in, needs node enrollment | `CoCern.Adapter/Node/PromptHook.cs` (confirm its path) |
| Printed ledger | On the delivery branch | `CoCern.Adapter/WakeState.cs` |
| `PostToolUse` hook | Missing on every ref | |
| Auto-acknowledge on reply | Missing | |
| Send by name | Missing | |
| Sender name on messages | Missing: a wake prints a session ID | `InboxMessages`, `ClaudeWaiter` |

## Build order

1. Land one-time delivery.
2. Small hub change, approved: `replyTo` joins the acknowledge set in `ReplyAddOns.cs`. Tell root before touching it. `SessionAttention.cs` stays reserved for root's decisions tasks, so "accept also acknowledges" waits.
3. Check the client contract on a real client (below), then the adapter-only hook build: `WakeState` exposes the printed table without the ownership lock, `PromptHook` polls headers with `knownInboxEtag` and prints only unprinted messages, a `PostToolUse` hook is added, and `ClientSettings` and `SetupCommand` install by default.
4. Send by name follows the directory slice, which supplies the resolver and native labels. Sender name is a hub stamp in `SendDirect` and needs approval.

## Traps, to keep as tests

- A count-only notice must not mark messages seen. Marking seen removes the direct-message wake, so an ignored message would never wake the chat.
- A waiter holds its state lock for its whole wait, so an ordinary-turn hook cannot open `WakeState` to read the ledger as it stands.
- A waiter past its wake check can find nothing new once a hook has printed, and would wake the chat with "no new messages". Suppress empty wakes.
- The model's own `check_inbox` does not write the printed ledger, so a hook repeats those messages once. Decide whether that is acceptable or whether `PostToolUse`'s `tool_response` should record them.
- The native path has no printed ledger until `claude/native-waiter-fixes` lands.

## Spec corrections

- "No ack IDs to pass" fails for a reply covering several messages, because `replyTo` names one. `ackMessageIds` stays. Unhooked clients also keep today's fields.
- Part 2's "it will see mail on its next tool call" is untrue until `PostToolUse` exists.
- "2 new messages from Codex: ..." conflicts with today's `PromptHook`, which prints no text and marks nothing shown.
- "Codex equivalents" is outside this note's Claude-only evidence.

## Client contract

From the Claude Code hooks reference (https://code.claude.com/docs/en/hooks.md), read on 2026-11-04; none of it has been tried on a real client.

- `PostToolUse` adds context through `hookSpecificOutput.additionalContext`, and receives `tool_response` on stdin.
- `additionalContext` and plain stdout are capped at 10,000 characters. Past that, Claude gets a file path and a 2,000-character preview and is not asked to read the file. Hook delivery must stay under the cap; the printed wake's limit is 9,000 for that reason.
- The limit on `asyncRewake` stderr is not documented. Test it before relying on anything larger.
- `UserPromptSubmit` injects both plain stdout and `additionalContext`, under the same cap. Its command hooks time out after 30 seconds by default, and timed-out output is discarded.

The standing yes for the real-client checks covers `PostToolUse` size behaviour; do those before building on it.

### Real-client results, 2026-11-05

Tried on one interactive desktop chat, Claude Code 2.1.317, with `Bash` as the tool.

- **The cap is exactly as documented.** `additionalContext` of 1,000, 9,000 and 10,000 characters reached the model in full. 10,001, 30,000 and 100,000 were replaced by a `persisted-output` notice with the saved file's path and a preview of about 2 KB, so the end of the text is lost. The printed wake's 9,000 limit is safe.
- **`tool_response` by tool.** `Bash`: `stdout`, `stderr`, `interrupted`, `isImage`, `noOutputExpected`. `Write`: `type`, `filePath`, `content`, `structuredPatch`, `originalFile`, `userModified`. `Edit`: `filePath`, `oldString`, `newString`, `originalFile`, `structuredPatch`, `userModified`, `replaceAll`. Every `PostToolUse` had `tool_use_id`.
- **Still not tried:** the limit on `asyncRewake` stderr, and `UserPromptSubmit` output size.
