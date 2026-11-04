# Traffic baseline, 4 November 2026

This baseline measures relay replies, not model billing or actual model context. The analytics snapshot completed at 10:37:24 Pacific on 4 November 2026 and was read later that morning. Retained history begins at 11:28 Pacific on 2 November, so the two-day and seven-day queries covered the same retained records; this is not a complete week or a trend comparison. Previous-window values were null.

The snapshot contains 37,261 calls, 131 non-null session identities, and 31,387,312 reply bytes. The ten largest cohorts account for 98.72% of bytes. Unknown origin accounts for 55.96%, background for 38.09%, and model-tagged for 5.95%. The relay estimate totals 13,163,183 tokens, with 7.57% model-tagged. These estimates do not establish how many tokens reached or were billed by a model. Tokens per task is null with zero samples; no usage coverage was available for the 50 returned task rows.

| Cohort | Calls | Reply bytes | Bytes per call |
| --- | ---: | ---: | ---: |
| Background status | 22,040 | 10,916,527 | 495.31 |
| Unknown inbox | 6,361 | 9,690,383 | 1,523.41 |
| Unknown worker list | 533 | 4,038,039 | 7,576.06 |
| Unknown task read | 581 | 2,424,816 | 4,173.52 |
| Unknown status | 3,498 | 1,270,545 | 363.22 |
| Model-tagged inbox | 311 | 1,103,072 | 3,546.86 |
| Background inbox | 943 | 1,038,985 | 1,101.79 |
| Model-tagged task read | 82 | 312,906 | 3,815.93 |
| Model-tagged context read | 18 | 96,263 | 5,347.94 |
| Model-tagged send receipt | 1,089 | 93,219 | 85.60 |

One remote session accounts for 86.08% of unknown-origin inbox bytes. Unknown worker-list and task-read cohorts lack session identities and cannot be attributed to a process. Session attribution alone does not identify an executable. A conditional reread of the existing five-report batch reduced its response from 3,193 to 198 bytes, a 93.8% reduction; this demonstrates an existing capability, not a new improvement.

Suggested matched-workload targets are quiet status replies at most 250 bytes, scoped worker and workflow replies at most 2 KiB, unchanged replies at most 200 bytes, and at least 80% less replayed inbox content while retaining every new message, continuation and acknowledgment. For message requests, target at least 25% less text while preserving a receipt at most 128 bytes. These are proposed measurement targets, not implemented limits or demonstrated savings.

For a valid comparison, freeze versions and workload, use disjoint matched UTC windows, wait for completed snapshots, and retain retry costs. Record response bytes, relay token estimates and actual emitted client content separately. Exclude instrumentation consistently. Verify message identities, continuation, acknowledgments and native wake behavior as well as size. The requested 133-message replay and wake-latency comparison still require separate execution evidence; this baseline does not establish those savings.
