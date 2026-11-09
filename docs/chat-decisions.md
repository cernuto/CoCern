# Answers to Standing questions

`record_decision` records the human's current direct answer to a Standing question already offered to that chat. It accepts `proposalId`, `decision` (`approve` or `dismiss`), and the exact `humanWords`, plus the usual repository, board, session handle and stable request ID. The answer must be nonblank and at most 1,024 UTF-16 code units. Its punctuation and spacing are preserved.

The hub verifies the owned open session, authenticated presenter, pending proposal and complete displayed effect. Changed terms require another presentation and a fresh human answer. An expired grant cannot be approved, but its unchanged proposal can be dismissed. Workers gain no general Standing administration capability. `standing_put` remains administrator-only.

The existing board transaction records the answer, principal, chat, time, proposal, displayed effect and session-scoped audit reference. Approved lines and grants carry that evidence independently of bounded history. Equivalent replacement terms can record a new answer without changing the effective Standing revision. A dismissal changes no active Standing. A decision is refused atomically if its exact evidence cannot fit the readable view.

An uncertain call must be retried in the same session with the same request ID and arguments. The receipt is a record of that past decision, not proof that a grant is still active. Replaying it after removal never restores authority. A new call for an already decided proposal is refused.

The dashboard shows the exact answer as encoded plain text, the recording chat, scope and expiry, replacement effect, and audit reference. The existing authenticated, revision-checked removal controls remain the recovery route. A presented question is distinguished from a recorded answer. Legacy records have no invented quotation. Decision request text is excluded from the telemetry token-count queue.

The hub trusts the recording chat's attestation that the quoted answer is the current direct human reply. It cannot independently prove who spoke. Peer messages, queued messages, old quotations, silence and ambiguous replies must never produce a recording call. This is the accepted single-user trust boundary.

CoCern also has a separate `record_decision` form with `decision: "approveGoal"`, an existing `taskId` and exact `expectedVersion`, finite `expiresAt`, exact current `humanWords`, and `participants`. Membership is either the explicit literal `"repository"` or an array of at most 32 selectors, each containing exactly one whole `name` or current peer `ref`. Named membership must include the approving chat. Every selector must resolve to one current verified participant in the same repository; labels alone and duplicate or ambiguous members are refused.

Approval freezes the existing root task's goal, owner, membership and original Standing grant. Descendant workflows use that original grant reference. Expiry, revocation, root completion, altered purpose or missing private bindings refuse further goal execution; returning a prior claim receipt does not restore authorization. Only the verified owner can complete the root. Goal outcomes follow the owner's current verified session, while the dashboard remains the recovery route. Public task and Standing records omit private participant identifiers.

Session tool settings are a separate administrator operation and remain bounded by credential authority; goal approval does not grant administrator privileges. Goal approvals and session tool settings are on `main` and run on the hub (merged 9 November 2026). A session tool setting has since been used for a real chat; no real client has yet approved a bounded goal, so that journey remains unverified end to end.
