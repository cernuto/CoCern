# Native file claims: renewals must not use up the mutation fence

Status: design note, source only. Native activation stays off regardless of this work.

## The problem

Each native mutation reserves one file in the binding's `calls-<key>` folder (`NativeJoin.MutationFence`). The folder refuses at 4096 files and nothing is ever removed. `EditClaimLifecycle.RenewAsync` spends a new invocation, `<grant request>-renew-<sequence>`, about once a minute for each dirty file. Continuous renewal alone runs out in about 68 hours for one file, 64 minutes for 64 and 32 minutes for 128. Running out refuses safely, but it stops renewals and edits for the rest of the binding.

## What a fence is for

A fence holds a call to one session, tool and argument set, and keeps its request ID stable across a restart, so a lost reply is replayed and never sent as something new. It only matters while that invocation can still be sent again. The hub's own replay record is separate and does not depend on the fence file.

## The design

Keep one fence for each dirty file by retiring a renewal's fence once its sequence has moved on.

- **When a renewal is finished.** `RenewSequence` only rises and is durable. Once the store shows a sequence above N, nothing can send `-renew-N` again. A renewal is answered first, then the sequence moves, so a sequence above N also means N was answered.
- **When to retire.** Before sending a renewal, retire the fence for sequence minus one. When a claim settles, retire the same one. A retirement lost to a crash or IO failure is tried again before the next renewal, and it is a no-op once the file has gone.
- **What stays.** The current sequence's fence is never retired, answered or not. That includes a pending renewal whose reply was lost, even after its claim settles: it costs one slot per lost final reply, which is rare, and the rule stays simple. Acquire and release fences are untouched.
- **Only exact matches go.** `NativeJoin` retires only `claim_renew`, and only after `MutationFence` with `reserve: false` confirms the file is private and matches this binding generation, session, tool and arguments. Anything else leaves the file in place.

## Why this is safe

Removing an answered fence changes nothing at the hub: the request ID is still derived from the invocation, so a late duplicate of N is replayed or refused there. A late renewal can only extend a live grant of this same generation, or be refused as stale. Sweeps of one binding are already serialized by the sweep gate, so no second process can be sending N while it is retired.

## Alternatives rejected

- **No fence for renewals.** It would also drop the session and argument check on a real in-flight call, so a replay after a rebind would no longer be refused locally.
- **Reusing one invocation for every renewal.** The hub would replay the first stored reply, with its old lease, and never extend the claim.
- **A longer lease or fewer renewals.** That only delays exhaustion, and a longer lease keeps a crashed chat's file locked for longer.
- **Deleting the oldest fences at the limit.** The oldest may be an uncertain call, so this could break replay.

## What this does not cover

- **Edits still spend slots.** Each acquire and each release keeps a fence, so about 4096 claimed edits exhaust a binding however renewals behave. Retiring acquire and release fences when `ledger.RetireReleased` retires their intent looks possible, because the refusal filter already blocks a retry, but it needs its own review. It should be settled before native activation.
- **The retired-ID filter is also finite.** Its 8 KiB saturates and then refuses new edits, as `docs/file-claim-consumer.md` records.
- **The hub keeps a record per renewal** on the lock, trimmed by its existing retention. This work leaves that alone.

## Change

- `IEditClaimLifecycleTransport` gains `RetireRenewalAsync(path, generation, invocation)`, best effort.
- `NativeEditClaims.FileTransport` builds the same arguments as the renewal and calls a new `NativeJoin.RetireRenewalFenceAsync`.
- `EditClaimLifecycle` retires before each renewal and in one settle path.
- `A_claim_renews_only_for_this_chats_own_change_still_dirty_in_the_file` asserts that the previous fence goes and the current one stays. This is a replay fence, so a test is allowed. Nothing has been compiled or run.
