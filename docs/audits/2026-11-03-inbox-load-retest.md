# Updated-node 133-message inbox retest

Run on November 3, 2026, with the coordinating Codex session's explicit clearance and final GO. Target: the existing Codex participant `104fd233c0fa4eccbed9fd7d2b1964ac` on `DESKTOP-8IV8M8M`, whose node was independently verified running `1fc283b`. This was a Codex-only test; it does not resolve the separate Claude post-restart wake failure.

## Workload and controls

The retained baseline has 134 send attempts: 133 accepted and one oversized request refused with `InvalidArguments`. The replay omitted that refusal and reproduced the 133 accepted normalized request-byte sizes, totaling **144,358 bytes**, using inert ASCII FYIs. Every replay message used `wake:false`; all 133 succeeded without retries. The receiver independently counted 133 unique matching FYIs and acknowledged them.

The original accepted traffic spanned 219.757 seconds across four senders. This replay used one existing sender and attempted the original relative offsets, but serialized MCP transport extended its actual span to **323.860 seconds**. Consequently this is a matched count/normalized-byte-profile comparison, not identical pacing, plaintext content or sender concurrency.

All times below are UTC:

| Event | Observed time |
| --- | --- |
| Remote turn completed before test | 23:44:09 |
| Replay started | 23:48:07.511 |
| Last replay accepted; quiet period began | 23:53:31.371 |
| Root independently checked unchanged native idle state | 23:55:40 |
| Single positive stored | 23:56:13.1988006 |
| New native turn started | 23:56:20, seconds resolution |
| First inbox receipt, reported by recipient | 23:56:35 |
| Positive discovered after initial body pagination | 23:57:30 |
| Computed ACK stored | 23:58:46.5170786 |

The post-burst quiet period before the positive lasted **161.828 seconds**, exceeding the required 75 seconds. Root verified no intervening native turns across the FYI burst and quiet interval. Exactly one positive was sent: `LOAD1FC-POSITIVE-01`, message `948cd188de0542ec8305d23c780b119e`. No manual queue injection or repeat positive was used.

## Live activity measurements

Counts come from read-only queries of the live SQLite `activity_log`, filtered to this recipient, repository and exact phase bounds. They do not come from the hourly analytics snapshot. Current background and model-facing-path classifications are reported separately; a model-facing path label is not a measurement of actual model context or billing.

| Phase | Inbox reads / reply bytes | Background status reads / reply bytes |
| --- | --- | --- |
| FYI replay | **0 / 0** | **132 / 50,582** |
| Post-burst quiet period | **0 / 0** | **11 / 4,224** |
| Positive through completed handling ACK | **12 model-facing / 164,828** | **19 / 7,367** |
| Entire measured window | **12 model-facing / 164,828; zero background inbox reads** | **162 / 62,173** |

The earlier reported load baseline was 228 inbox reads, about 3.9 MB of inbox replies, and 132 status reads. A reconstructed historical interval, 20:59:30–21:03:45, matches those call counts and contains 3,806,352 inbox reply bytes and 48,305 status reply bytes. Its origin fields are all unknown. The original analysis interval has not independently been established, so this reconstruction is supporting context rather than a replacement baseline.

The observed result is that repeated background inbox/body reading disappeared during this run. Status polling did **not** fall during the burst: it remained 132 calls, over a longer replay. Do not turn these figures into a general model-token saving or an identical-rate benchmark claim.

## Wake and handling result

The native turn began approximately **6.8 seconds** after the positive was stored. First inbox receipt was approximately **21.8 seconds** after storage. The computed ACK arrived **153.318 seconds** after storage and returned `FYI=133; SUM=4.35`, with native batch `eb06592d-7dfb-4b7e-823a-2d8a4fdb833b`.

These endpoints must remain distinct when comparing the earlier approximately 23-second wake figure, whose precise endpoint is not established here. The 153-second ACK also includes handling the loaded inbox and is not a single-message wake latency.

The recipient initially paged message bodies before finding the positive's header-only instructions. It then recounted using three bounded header-only pages: 135 headers, 133 unique matching FYIs. This explains why foreground inbox traffic remains despite zero watcher inbox reads. A useful follow-up is to put header-first discovery in the installed wake/bootstrap guidance, where it is available before the first inbox read. This test did not implement that change.

## Analytics reread and remaining limits

After the load test, the daily dashboard analytics endpoint returned `status: too-large` with no report rows. A bounded batch of `relay_tokens` and `usage_coverage` succeeded, but its snapshot cutoff was **23:38:36.5802573**, before this test. It therefore cannot validate this replay. The daily aggregate still contains substantial historical unknown-origin traffic; it cannot retrospectively show how much of that traffic reached a model. Dashboard response budgeting and a refreshed snapshot remain separate follow-ups.

The coordinating session held heavy feature builds and tests during the measured window; its release occurred after the ACK. The primary hub, recipient enrollment, credentials, certificate pin and installed node were unchanged by this test. All test FYIs and the positive were acknowledged; no new sender sessions or helper processes were created. Unrelated mail was preserved.

Reproducible, sanitized metadata is retained under ignored `artifacts/load133-20261103/`: `baseline-manifest.json`, `final-run-metadata.json`, `final-measurement.json`, `positive-stored-metadata.json` and the analytics rereads. These contain counts, timings and message IDs, not credentials or filler bodies.
