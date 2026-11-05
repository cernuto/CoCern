# Native scope producer and wake activation

Status: proposed integration plan, 2026-11-05. No native hook activation or live wake success is claimed. Claude Manager owns assignments and sequencing.

## Ship the smallest wake correction first

The existing local path is durable inbox -> hint/reconciliation -> local session handle -> Codex thread locator -> WakeReconciler -> CodexWakeDispatcher -> `codex queue --thread ... --message ...`. An accepted queue receipt is not proof of a turn. Claude worker 089 reports that this PC has no Codex local-wake handle or watcher database for root or reviewer 3aa. The first gate is identifying why LocalChatWake creation is skipped, rather than replacing this path with another scheduler.

1. Integrate the reviewed native waiter fixes from `claude/native-waiter-fixes` after 089 supplies its final green head, including the native-hook stderr correction. Retain staged inactive hook settings until real-client gates pass.
2. Add bounded, credential-free initialization diagnostics in `src/CoCern.Adapter/Node/LocalWakeConnection.cs` and `src/CoCern.Adapter/Node/LocalChatWake.cs`. Record which guard or I/O stage prevented startup, without token values, token fingerprints, environment dumps, or private payloads. Add LocalWakeTests for each refusal and successful creation. Do not replay another process's environment.
3. Review restart recovery in LocalChatWake: existing handles must be authenticated and located before rearming; recency must exclude abandoned sessions; preserve the existing per-chat exclusive WakeState fence. Establish the actual failure from the deployed diagnostics before changing discovery behavior.
4. Independently review `src/CoCern.Domain/SessionAttention.cs` and AttentionSchedulerTests for the oldest-unread wake fence starving newer mail. A change must preserve deduplication and bounded retries, and prove two distinct unread messages can each cause eligible reconciliation without an acknowledgment loop. Task-addressed mail currently follows a different hold policy; do not silently change that policy.

## Retain authoritative native scope for protected consumers

Existing machine authentication already creates a private machine claim. `RelayAuthentication.Caller` currently drops it. NativeParticipant.Admit hashes the submitted working copy and machine label, then discards the working-copy value; the submitted machine name is not enrolled-machine proof. Do not reconstruct protected scope from principal prefixes, labels, mutable session bindings, or claim arguments.

Proposed producer files and responsibilities:

- `src/CoCern.Domain/CoordinationModel.cs`: extend RelayCaller with optional trusted machine identity populated only by authentication.
- `src/CoCern.Host/RelayAuthentication.cs`: map the existing authenticated machine claim into that private caller field. Configured local tokens and OAuth callers without that claim retain no protected machine scope.
- `src/CoCern.Core/DomainSurrogates.cs`: preserve the optional field across Orleans serialization, including a legacy absent-field case.
- `src/CoCern.Domain/CoordinationStateMachine.cs`: preserve and remove private caller provenance correctly if the native-opening effect path crosses this boundary; never expose it as public tool input.
- `src/CoCern.Domain/NativeParticipant.cs` and `src/CoCern.Domain/State/ControlState.cs`: retain the admitted native join's working-copy context alongside its generation and authenticated machine provenance. Expose a bounded private accessor using the existing native-join registry and current-session check, rather than a second registry. Confirm physical working-copy canonicalization with the adapter before granting file scope; lexical Path.GetFullPath or a shared Git common directory is insufficient.
- `src/CoCern.Core/CallGuard.cs`: resolve protected scope only after ordinary session-token verification and current native generation/closing checks. Avoid adding a scope lookup to every unrelated relay call. Keep all provenance private and strip it from public replies.
- Native/authentication/surrogate tests: exact own current generation passes; wrong token, repository, machine, working copy, replaced generation, closing session, legacy missing provenance, and forged input fail closed for protected file scope. Existing raw relay behavior remains available under its existing rules.

The frozen producer result is `CoCern.Domain.VerifiedNativeFileScope(ParticipantId, SessionId, RepositoryId, MachineScope, CanonicalWorkingCopyRoot, NativeGeneration)`, with all six fields strings and no secrets. PC2 confirmed this distinct producer record on 2026-11-05. Its consumer remains `CoCern.Domain.FileClaimScope.TryCreate(repository, machineScope, canonicalWorkingCopyRoot, relativePath, out scope)`. PC2 owns FileClaimScope and adapter file-claim enforcement; root must not duplicate those files. Protected file scope is a separate gate and must not delay the basic local wake correction.

The physical root comes from `ProtectedCheckoutRoot.Prove` (adapter). It fails closed for UNC paths, mapped network drives, subst onto a share, roots it cannot prove, and every non-Windows platform, where locality cannot yet be proven. General `NativeCheckout` and UNC binding behaviour is unchanged. It is a point-in-time eligibility check, not durable authority: renaming or retargeting a link between the proof and its use is not fenced. The `VerifiedNativeFileScope` consumer must revalidate identity at use or hold handles, and gets its own review.

## Real-client activation gates

1. Produce immutable reviewed adapter bits and deploy through the supported development deployment path. Record Git head, deployed DLL hash, adapter version, and generation. Check root's existing CoCern session can still authenticate after replacement.
2. On a disposable Codex profile first, observe a genuine SessionStart payload, native chat identity and working copy; verify denied/untrusted hooks cannot enroll and public arguments cannot impersonate an admitted participant. Previous permission to trust one fixture hook does not authorize trusting unrelated hooks.
3. Verify the initialized local connection, own restored session handle, exact native-to-chat mapping, watcher, and exclusive fence. Keep credentials out of logs and reports. A restart may be necessary to load the new hook or diagnose adapter startup; request it only after the deployable result exists and explain the exact remaining client action.
4. Let the target become truly idle. Send a fresh correlated nonce through CoCern under Paul's existing cross-session test authorization. Require a resulting native turn, a substantive correlated CoCern reply, explicit acknowledgment of the handled message ID, and drained queue. Capture queued, turn-start, reply, and acknowledgment times separately. Queue exit zero alone fails this gate.
5. Repeat after adapter replacement and with two chats; verify no cross-chat binding, no duplicate wake, no acknowledgment-only loop, and bounded recovery from a stale binding. Test real Claude independently using its Edge browser rule and its own hook path; Codex has no Stop hook today.
6. Only after these pass should inactive candidate hooks be promoted through an implemented and tested settings path. PlanNativePromotion is currently a no-op with no callers; do not report permanent automatic activation until that path and real restart/recovery are proven.

## Integration and UI follow-through

Batch A should merge the full origin feature head `6be2f238ebb0422ab20229922e39eb894cd87084`, not cherry-pick only its final commit, because the earlier ancestors contain its regression tests. Then integrate 089's reviewed reply-auto-ack, Waiting-on-you, and final native waiter heads. Compile and run affected tests on the exact frozen integration head before the single full gate; retain actual process-end and TRX evidence.

Waiting-on-you rendering and the slim dispatch strip follow Manager's feature order. The proposed unresponsive row should say "no hub call", not assert the model is stuck. Its last-activity query must exclude background calls; a live polling watcher cannot prove model activity. Keep the unresponsive contract provisional until the producer compiles and its query behavior is tested. Second-PC acceptance remains parked until Manager resumes it.
