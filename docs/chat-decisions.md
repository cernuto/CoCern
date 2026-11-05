# Answers to Standing questions

`record_decision` records the human's current direct answer to a Standing question already offered to that chat. It accepts `proposalId`, `decision` (`approve` or `dismiss`), and the exact `humanWords`, plus the usual repository, board, session handle and stable request ID. The answer must be nonblank and at most 1,024 UTF-16 code units. Its punctuation and spacing are preserved.

The hub verifies the owned open session, authenticated presenter, pending proposal and complete displayed effect. Changed terms require another presentation and a fresh human answer. An expired grant cannot be approved, but its unchanged proposal can be dismissed. Workers gain no general Standing administration capability. `standing_put` remains administrator-only.

The existing board transaction records the answer, principal, chat, time, proposal, displayed effect and session-scoped audit reference. Approved lines and grants carry that evidence independently of bounded history. Equivalent replacement terms can record a new answer without changing the effective Standing revision. A dismissal changes no active Standing. A decision is refused atomically if its exact evidence cannot fit the readable view.

An uncertain call must be retried in the same session with the same request ID and arguments. The receipt is a record of that past decision, not proof that a grant is still active. Replaying it after removal never restores authority. A new call for an already decided proposal is refused.

The dashboard shows the exact answer as encoded plain text, the recording chat, scope and expiry, replacement effect, and audit reference. The existing authenticated, revision-checked removal controls remain the recovery route. A presented question is distinguished from a recorded answer. Legacy records have no invented quotation. Decision request text is excluded from the telemetry token-count queue.

The hub trusts the recording chat's attestation that the quoted answer is the current direct human reply. It cannot independently prove who spoke. Peer messages, queued messages, old quotations, silence and ambiguous replies must never produce a recording call. This is the accepted single-user trust boundary.

This slice implements proposal answers and their evidence only. Bounded goals, capability upgrades and actual native-client acceptance remain separate work. No production approval or goal was created to develop this slice. New Razor generation and the related runtime smoke checks remain for the authorized pre-deploy milestone; source diagnostics alone do not prove the rendered journey.
