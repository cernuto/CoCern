# Native file claims: renewals must not use up the mutation fence

Status: design note, source only. Native activation stays off regardless of this work.

## The problem

Each native mutation reserves one file in the binding's `calls-<key>` folder (`NativeJoin.MutationFence`). The folder refuses at 4096 files and nothing is ever removed. `EditClaimLifecycle.RenewAsync` spends a new invocation, `<grant request>-renew-<sequence>`, about once a minute for each dirty file. Continuous renewal alone runs out in about 68 hours for one file, 64 minutes for 64 and 32 minutes for 128. Running out refuses safely, but it stops renewals and edits for the rest of the binding.

## What a fence is for

A fence holds a call to one session, tool and argument set, and keeps its request ID stable across a restart, so a lost reply is replayed and never sent as something new. It only matters while that invocation can still be sent again. The hub's own replay record is separate and does not depend on the fence file.

## The design

Keep one fence for each dirty file by retiring a renewal's fence once its sequence has moved on.

- **When a renewal is finished.** `RenewSequence` only rises and is durable. Once the store shows a sequence above N, nothing can send `-renew-N` again. A renewal is answered first, then the sequence moves, so a sequence above N also means N was answered.
- **When to retire.** Before sending a renewal, and when a claim settles, retire every sequence from the saved cursor `RetiredBelow` up to, but not including, the current one. Stop at the first unconfirmed retirement. The cursor saves what was confirmed.
- **A failed retirement stays owed.** The cursor only moves past a confirmed retirement, so the next pass starts from the same sequence. Renewals still go meanwhile, because losing the claim costs more than a slot for now. A settling claim keeps its receipts until its retirements are confirmed, so the sweep keeps coming back; the claim itself is already over and its row says so.
- **What stays.** The current sequence's fence is never retired, answered or not. That includes a pending renewal whose reply was lost, even after its claim settles. Acquire and release fences use the separate settled-intent retirement below.
- **Only exact matches go.** `NativeJoin` retires only `claim_renew`, and only after `MutationFence` with `reserve: false` confirms the file is private and matches this binding generation, session, tool and arguments. A fence that doesn't match is left in place and reported as done, because retrying can't change that. File errors, a missing handle or a reparse point report unconfirmed.

## Why this is safe

Removing an answered fence changes nothing at the hub: the request ID is still derived from the invocation, so a late duplicate of N is replayed or refused there. A late renewal can only extend a live grant of this same generation, or be refused as stale. Sweeps of one binding are already serialized by the sweep gate, so no second process can be sending N while it is retired.

## What can still be left behind

Each of these is a permanent slot, or a refusal that fails safe.

- One slot per settled generation whose final renewal reply was lost.
- One slot for a fence that doesn't match its renewal. That takes outside tampering or a hash collision.
- Fences of an older binding generation, because its ledger is no longer used.
- While retirement keeps failing, renewals still spend a slot each, so a lasting file error can still reach the 4096 refusal. Nothing is deleted to make room, and the owed fences go once the error clears, provided the sweep can still run.

## Alternatives rejected

- **No fence for renewals.** It would also drop the session and argument check on a real in-flight call, so a replay after a rebind would no longer be refused locally.
- **Reusing one invocation for every renewal.** The hub would replay the first stored reply, with its old lease, and never extend the claim.
- **A longer lease or fewer renewals.** That only delays exhaustion, and a longer lease keeps a crashed chat's file locked for longer.
- **Not advancing a renewal until the last fence is confirmed gone.** A file error on cleanup would then drop the claim on an edit in progress. Keeping the obligation on record gives the same bound without that.
- **Deleting the oldest fences at the limit.** The oldest may be an uncertain call, so this could break replay.

## What this does not cover

- **Unsettled calls still spend slots.** Acquires without a confirmed generation, unresolved compensations, messages and other native mutations keep their fences. This follow-up only reclaims the acquire and release fences of fully released intents. A mismatched or unavailable fence can also remain; the 4096 limit still refuses safely.
- **The retired-ID filter is also finite.** Its 8 KiB saturates and then refuses new edits, as `docs/file-claim-consumer.md` records.
- **The hub keeps a record per renewal** on the lock, trimmed by its existing retention. This work leaves that alone.

## Change

- `EditClaimTracking` gains the saved cursor `RetiredBelow`.
- `IEditClaimLifecycleTransport` gains `RetireRenewalAsync(path, generation, invocation)`, which answers whether anything is still owed.
- `NativeEditClaims.FileTransport` builds the same arguments as the renewal and calls a new `NativeJoin.RetireRenewalFenceAsync`.
- `EditClaimLifecycle` retires before each renewal and in one settle path, which holds the receipts until retirement is confirmed.
- `A_claim_renews_only_for_this_chats_own_change_still_dirty_in_the_file` covers the previous fence going, the current one staying, and a failed retirement being retried at both points. This is a replay fence, so a test is allowed. Nothing has been compiled or run.

## Settled acquire/release follow-up

After every target of an intent is confirmed released at a positive generation, settlement saves the released receipt without removing it. Released receipts are terminal: `Save` refuses an attempt to overwrite one, while existing active receipts remain independent of refusal-filter collisions.

The sweep takes the same decision gate PRE holds through its calls and saved attempt. Under it, `RetireReleased(intentId, keepReceipt: true)` durably sets the existing permanent refusal filter and keeps the exact target and acquire invocation identities. Only after that write succeeds does the transport retire the acquire fence and both deterministic release suffixes (`-release` and `-clean-release`). `NativeJoin` accepts only claim tools and applies the same private-file, binding generation, session and canonical argument check as renewal retirement. It neither reserves a fence nor contacts the hub.

The full receipt goes only after every retirement is confirmed. A crash or failed deletion keeps the terminal receipt, and a later sweep retries the same identities; an already missing fence is harmless. The next sweep stays scheduled at a sixty-second cadence even with no live claim, so failed cleanup is not forgotten or retried in a tight loop. A lost release response remains held/unknown until the existing generation/lease reconciliation proves settlement, so it cannot enter this cleanup early. Multi-target intents wait for all targets, and pre-owned duplicate receipts retire their own acquire invocation without touching the original grant's invocation.

The existing lifecycle and ledger-retirement tests are extended for this replay boundary: pending cleanup across restart, no repeated release, no fence retirement for an unresolved release, and no revival while the full released receipt is retained. Source-only verification uses Roslyn and Visual Studio; no build or test run is performed before the Manager's deployment window. This does not authorize native activation or prove provider hook behavior.
