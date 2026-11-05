# Directory slice: named-peer resolver and current-session attention route

Design note, contract only; nothing is built. Source read on 2026-11-04.

## Where the interfaces are: not on main

The ask calls these "existing", but they exist only on feature branches.

- origin/main 19cf699 has neither: Roslyn on the primary checkout and `git grep` both find no `NativeParticipant`, `VerifiedNativeParticipant` or `CurrentNativeSession`.
- Both arrived in 718d127, with byte-identical files (CallGuard.cs 8c466cf, ControlState.cs a6c2d13, NativeParticipant.cs b9859fd) on native-participants ab806dd, native-join-coordinator a8539dc, native-hooks 86e5261 and auto-connect-features 2a79671.
- `RelayCall.NativeParticipant` is set only by `CallGuard.VerifyAsync`, so only for `RequiresSession` tools or a `get_status` naming a session; only a test reads it today. `ControlState.CurrentNativeSession` returns null for absent, expired, closing or duplicate rows; its only production caller is `ResumeNative`.
- The hook path (`NativeJoinHook.cs`) is only on native-hooks, which has diverged from auto-connect-features (8 and 12 unique commits) and moved from bf10d41 to 86e5261 during this work. Re-pin before starting. The chat-decisions plan is the same blob (5c88014) on main and auto-connect-features.

## 1. The problem

Decisions Task 3's goal form of `record_decision` takes `participants` by name, stores owner and members as verified participants, and sends goal items to the owner's current session. The native autojoin plan supplies identity but leaves "named-peer selection and ambiguity" to this slice. No code maps a name to a participant, and attention routes only by role.

## 2. Resolver contract

An internal typed request to the control aggregate, serialized with the directory. No public tool exposes it. Consumers are `peers` and `record_decision`; send-by-name (design part 3) can reuse it later.

**Input:** the guard-checked repository, the caller's `RelayCall.NativeParticipant`, and a `name`, or a `ref` from an earlier choice. A call with no verified caller is refused; raw clients keep today's tools.

**Matching:** whole-label equality, trimmed and ignoring case, never prefix or fuzzy. Candidates are this repository's directory rows across principals, since another PC has its own credential. The repository filter is mandatory because the control grain is hub-wide. Rows group by participant ID.

**Results:**

- `Resolved`: exactly one candidate, a native row that `CurrentNativeSession` returns. Carries the `VerifiedNativeParticipant`, label and client.
- `Ambiguous`: two or more candidates, counting legacy rows and the caller. The caller may be told each one's label, client, machine, task, state word, `ref`, and `verified:false` for a legacy row. Recency, client and machine never break the tie.
- `None`, with a reason: `unknown` (no row; identical for another repository's chat), `replacing` (close or duplicate in flight, retry), `unverified` (the one match is a legacy row), or `gone`.

**Verified means:** the participant ID was set by the hub through `NativeParticipant.Admit` from the private binding proof (client copies are stripped by `RelayRequestGuard`), and the row passes `CurrentNativeSession`: one row, current generation, not closing, same repository. Label, binding, role and machine never count. A label only nominates a candidate, so a collision can cause a refusal but never a grant. Any chat can force ambiguity on a name, which costs availability and gives no authority.

**Lifecycle:**

- Replaced session: same participant, resolves once to the new session.
- Closed or expired: never `Resolved` by name; `gone` if a snapshot is kept (8c), else `unknown`. A stored ID stays valid, so a goal keeps its member and a rejoin under the same identity is that member.
- Fork, changed native ID with no continuation link, or another checkout, machine or principal: a new participant. A same-titled fork makes the name ambiguous; once the parent closes, the name resolves to the fork, which holds nothing of the parent's goals.
- Subagent: reports its parent's ID and has no row.
- `ref`: the row's current session ID, valid only while current, never stored. After a replacement it answers `unknown`.

**Task 3 hand-off:** pass resolved tuples on a clone of the arguments under a hub-only field, because `trace.Args` shares the object with `call.Args` and feeds telemetry. Fingerprint the names the client sent, as `RequestedResources` does for claims.

## 3. Current-session attention route

- An additive participant form of `AttentionRoute`, which today targets a role. Control calls `CurrentNativeSession` in the same operation and sends the raise or close to that one session.
- **New items after a replacement** reach the new session through that lookup.
- **Items raised before a replacement** do not survive alone: they live in the old session's state, and a raise racing a close is dropped, since delivery is Advisory. So control keeps a bounded set of board IDs on the participant's private join entry, and when `TrackNative` replaces the session it sends each board one resync. The board re-raises its open goal items for that owner; existing item-key and revision handling makes this idempotent. Claims and unsettled actions never move.
- **No current session:** `routed: 0`. Nothing goes to managers, another chat or a same-named chat. The truth stays in the board's task state, nothing counts as handled, and a rejoin triggers the resync. Dashboard visibility is not confirmed (8i).

## 4. Peers view (design part 2)

A new session-required read tool, `peers`, replacing `worker_list` and `chat_directory` on the native surface; raw clients keep both unchanged. It must set `RequiresSession`, which those two do not, or `RelayCall.NativeParticipant` is null. It builds on the `MachineDirectory.DirectoryAsync` merge, so a PC with no node still appears.

It returns `items` and `truncated` for one repository. Each row is an allow-list: `name`, `client`, `on` (task and claimed files), `state` (`working`, `idle`, `asleep`, `gone`) and `self`. `ref` and machine appear only in an `Ambiguous` choice. Names stay marked as untrusted display text.

## 5. Hiding rule

For every tool in `RelayToolCatalog`, on success and refusal, the serialized reply contains neither the keys `nativeParticipantId`, `nativeGeneration` or `nativeRequestId`, nor any participant ID value on the hub, ignoring case. The same holds for worker rows, grant and Standing delivery, task packets, search, attention items, activity rows and dashboard models. Two `peers` reads across a replacement share no opaque field, so no stable public handle exists. A client-sent participant field changes nothing.

Mechanism: IDs stay on the in-process record and under private state keys, `peers` projects by allow-list, and Task 3's grant holds owner and members under keys its projections drop.

## 6. Files

**Hub-side, needs approval and a hub redeploy:**

- New: `src/CoCern.Domain/Messages/PeerMessages.cs`, `src/CoCern.Core/PeerTools.cs`.
- Edited in `src/CoCern.Domain`: `ControlAggregate.cs`, `State/ControlState.cs` (including `Shape`), `CoordinationModel.cs`, `NativeParticipant.cs`, and in `Messages/`: `AttentionMessages.cs`, `EffectMessages.cs`, `InternalRequest.cs`, `SessionRecord.cs`.
- Edited: `src/CoCern.Core/ToolRouter.cs`, `src/CoCern.Core/RelayRequestGuard.cs`, `src/CoCern.Contracts/RelayToolCatalog.cs`, `src/CoCern.Contracts/ToolSchemas.cs`.

**Client and docs, edited:** `src/CoCern.Adapter/CoordinationTools.cs`, `ConditionalRead.cs`, `AdapterInstructions.cs`, `Node/SessionHandoff.cs`; `docs/reference.md`.

**Tests in `tests/CoCern.Tests`:** new `PeerDirectoryTests.cs`; edited `EffectMessageTests.cs`, `SessionStateShapeTests.cs`, `ContractsTests.cs`, `ToolDiscoveryTests.cs`.

## 7. Tests to write first

- `Ambiguous_name_refuses_and_lists_choices`: two rows share a label; no participant is returned, whichever is newer.
- `Label_collision_grants_nothing`: a raw session relabelled as a peer or "CoCern Manager" gains no participant, role or goal membership.
- `Other_repository_is_invisible`: the reply is identical to an unknown name, and the chat is absent from `peers`.
- `Replaced_session_routes_to_current`: same participant; the item reaches the new session only.
- `No_public_reply_contains_a_participant_id`: a sentinel that walks the catalog, so new tools are covered automatically.
- `Closing_then_expired_resolves_replacing_then_gone`.
- `Fork_is_a_new_participant`: ambiguous first; after the parent closes, resolves to the fork, with goal membership still the parent's.
- `Legacy_row_is_unverified_and_counts_toward_ambiguity`, `Stale_ref_fails_after_replacement`.
- `No_current_session_routes_to_nobody`, `Rejoin_resync_reraises_once`.
- `Peers_rows_are_allow_listed`, `Peers_lists_chats_without_a_node`, `Raw_tools_are_unchanged`.

## 8. Open questions and risks

- **a. Base and release.** "Released" needs 718d127 merged and a hub deployed with it. Choose the base: native-hooks has the callers, auto-connect-features the integration, and neither contains the other.
- **b. Blocker, conflicts with design parts 1 and 2: the hub has no name for a native chat.** `Admit` accepts exactly repositoryId, requestId, machineId and nativeJoin, the native surface hides `session_bind`, and the title stays in the adapter's local handle. Only node announcements carry a label, and the client exists on the hub only inside the hash. From reading the code, a native row on a PC with no node has an empty name. Proposal: the adapter sends the title through a private bind after join, and `Admit` stores the client.
- **c. `gone` rows.** A closed session leaves the directory, and `nativeJoins` keeps no label or repository. Proposal: an additive bounded snapshot on the join; without it, `peers` shows only silent chats, not closed ones.
- **d. Untitled chats.** Default labels are "Codex" and "Claude", and Claude's title is optional, so ambiguity will be common rather than an edge case.
- **e. `ref` shows a session ID,** which the design wants off the normal path. I recommend allowing it on this exception path because it rotates and adds no concept; an opaque per-caller token is the alternative. Needs a decision.
- **f. Row data has gaps.** The hub has no mid-turn signal for `working` (activity reports arrive at most hourly, and `waiterRunning` covers only Claude). `on` needs board reads, and file claims (part 6) are unbuilt. Task 3 needs neither, so these could follow the resolver.
- **g. Replay order.** A `record_decision` retry after a name turned ambiguous must return its stored receipt, so the replay check must precede resolving.
- **h. Plan scope.** Task 3's file list omits the control files, so this slice lands first. Task 3 owns the board side of resync and the private grant keys, so resync has no consumer until then. Resolve and grant commit in different grains; that is acceptable because identity is stable.
- **i. Dashboard fallback.** The design and plan rely on the dashboard when an owner is gone. In the dashboard's `.razor` markup on main, "blocked" and "failed" appear only in analytics and "stranded" not at all, and stranded alerts go to manager sessions. Task 3 or 4 must confirm or add this view, or a gone owner's item is visible nowhere.

## 9. As built on claude/directory-resolver (base 5f61704, not compiled)

The section "not on main" above is stale: `NativeParticipant`, `VerifiedNativeParticipant` and `CurrentNativeSession` are on main. Sections 2, 4 and 5 are in. Section 3 is not: the route alone is small, but resync needs state on the join and a board side, and Decisions Task 3 is its only consumer.

- **Internal requests:** `PeerResolve` and `PeerList` go to the control aggregate, answered in `PeerDirectory`. `PeerTools` is the only reader and projects by allow-list. `PeerTools.ResolveAsync` is the typed entry Task 3 calls.
- **8b, the name:** an optional `label` beside the private join's frozen proof, sent by the adapter on the wire only. It is outside the identity hash and the frozen arguments, flows to the existing `SessionRecord` label, is bounded by the guard, and grants nothing. `Admit` also records the proof's provider as `nativeClient`, kept on the private join entry, not the row, so worker listings do not change. With no title, the name reads as the client's default. Native opens are not replayed by request ID, so a changed title cannot conflict with a retry; it applies when a session is next opened. Hub before adapter: an older hub refuses the extra field.
- **8c, `gone`:** no snapshot. A closed chat answers `unknown` and is not listed.
- **8d, untitled chats:** they share "Claude" or "Codex" and are refused as ambiguous. Nothing breaks a tie.
- **8e, `ref`:** the row's current session ID, on the choice only.
- **8f, row data:** `state` is `reachable` (a live announced wake route) or `unconfirmed` (no proof of one), since the hub has no mid-turn signal. `on` is left out until a board read is justified.
- **8g, replay:** `peers` is a read and stores nothing. A mutation must check its stored receipt before calling the resolver; the contract is on `PeerTools.ResolveAsync`.
- **Differs from section 4:** the native surface adds `peers` and keeps `chat_directory`, because `send_message` still takes a recipient's session ID and `peers` shows one only in a choice. Drop it when a name can address a message.
- **Catalog:** `RelayToolCatalog.ForRole` leaves `peers` out, as raw clients never get it, so the capped worker and administrator catalogs do not change. The native surface pays about 431 characters.
