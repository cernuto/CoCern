# Combined gate record, 4 November 2026

This record preserves failed and superseded evidence. It is not permission to deploy or activate native providers. The Manager owns integration and subsequent acceptance.

| Candidate | Result | Disposition |
| --- | --- | --- |
| 2a79671724e60aac8d3944af25508f63d555812d | 2,018 total: 2,013 passed, four failed, one existing Unix-only skip | Checkpoint only. Failures were control state shape, session state shape, concurrent private-identity start, and bare HTTP 502 recovery. |
| 7f6714cc41d33c526a640c5958cb44ccf365fc1b | Build exit 1, four CS1912 errors | Duplicate Clients and Locators initializers at two NodeTests locations. No tests, prerequisites or staging ran. |
| 49e215a7e4ec54747ec5e52f5a8dbf76118362fe | Build exit 0; 2,204 total: 2,202 passed, one failed, one existing Unix-only skip | Not green. Missing typed registration for __present_standing. All four checkpoint failures now pass. |

The corrected 49e215a tree is fa7f856da33e386873195db1400486839305a429. Its full unfiltered solution test ran from 19:46:05.5663564Z to 19:56:23.2065069Z on 4 November 2026, exited 1, and did not time out. StandingIdentity contributed 22 passed cases. The main assembly contributed 2,182 cases: 2,180 passed, one failed and one NotExecuted for the pre-existing Unix ancestor-alias test. No new skips, filters or retries were used. MSBuild was serial and xUnit parallelism was capped at four, with a strict 15-minute outer bound.

The sole failure is EffectMessageTests.Every_internal_operation_has_a_typed_message_or_is_on_an_untyped_list_that_only_shrinks at line 332. Operations includes __present_standing, but neither typed registry contains it. The untyped list is empty and pinned at zero. The fix needs a typed contract and registration, or demonstrated removal of an obsolete operation; adding an untyped exemption would weaken the invariant. The candidate stayed frozen and the owner was notified.

All three required prerequisites actually passed in the TRX: DevelopmentLauncherTests.The_real_launcher_reconnects_to_a_changed_adapter_without_restarting_its_client, AddPcTests.A_hub_run_from_this_checkout_finds_the_adapter_built_in_it, and DefaultsTests.The_bundled_DuckDB_extensions_match_their_manifest_and_carry_the_license. The four earlier checkpoint failures also actually passed, including bare HTTP 502 recovery.

The run used a fresh isolated checkout and fresh outputs, without building into a running application's output directory. Before building, all 387 canonical source entries matched. At completion, all 435 tracked source hashes and all 203 staged Host/Adapter output hashes remained unchanged, Git was clean, all four binary ProductVersions matched the candidate, and no owned runtime remained. The runner was explicitly released after that audit.

| Evidence | SHA-256 |
| --- | --- |
| StandingIdentity TRX | 4F9115ED4240978565AFD4F2E2598F6766E02E32C72C4ECF2CDD408F8673D28F |
| Main TRX | 6F7079AF27D43E7489FD22EC081EC5A6D3C118859D9CBCC9C0D1B185244BE110 |
| Parsed TRX summary | 4873F594EC90E2B54B16A71664040055179390CB3E069EC16E55E894A70AAC76 |
| Final source, staging and process audit | F8CE74C55D98BCF05F8C44DFC50C783885BE258E398847BD4D3ECBDBA5F56984 |

Raw runtime logs and local manifests remain local. They are not included here because they may contain machine-specific data. A subsequent corrected candidate needs its own frozen head, fresh outputs and full result. Neither these tests nor the source-only merge review establish live deployment, native activation, second-PC journeys or Task 6/provider acceptance.
