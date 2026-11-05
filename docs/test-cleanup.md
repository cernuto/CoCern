# Hub test cleanup

This test-only branch follows the Manager-assigned KEEP policy. Production code and the deployed hub are unchanged. Counts below come from Roslyn test discovery: methods and static case estimates (each Fact or InlineData row, with one unexpanded placeholder for a non-inline theory). Dynamic MemberData is not executed or expanded. These are not new runtime discovery results or a coverage claim.

Root's assigned files go from 1039 methods / 1509 statically countable cases to 218 / 303. The other owners' files are excluded from this reduction. Thirty-nine complete fixtures, twelve dead helper types and fifteen unused document fixture methods are removed. The retained analytics extension helper moves into Support. The four machine authentication/provenance HTTP checks share one host fixture while preserving their assertions. The retained token reassembly fixture keeps only its one test and required reply helper.

KEEP preserves authenticated REST/MCP and dashboard startup, role/repository/session-token isolation, credential/route/provenance handling, secret-free logging and snapshots, explicit human authorization, replay/current-authority checks, atomic storage/restart and cleanup fences, and message reassembly/acknowledgment safety. Presentation/copy/performance assertions and duplicated catalog/shape/server cases are removed. Exact kept methods remain in the source; the per-class table makes omissions reviewable. SessionStateShapeTests, LockStateShapeTests, BoardStateShapeTests and their shared exhaustive helper remain owned by the second PC.

No tests, builds, coverage runs or red proofs were run for this branch. Both test projects were checked through Roslyn compiler diagnostics. Verification waits for the single authorized pre-deploy check described in [testing.md](testing.md), using the changed-symbol mapping.

| Class | Before methods / static cases | After methods / static cases |
| --- | ---: | ---: |
| AdminToolsHostTests | 32 / 46 | 8 / 11 |
| AdminToolsTests | 10 / 10 | 3 / 3 |
| AnalyticsBatchRouteTests | 2 / 2 | 0 / 0 |
| AnalyticsBatchSliceTests | 7 / 7 | 0 / 0 |
| AnalyticsCorrectnessTests | 11 / 15 | 0 / 0 |
| AnalyticsSnapshotScrubTests | 2 / 4 | 2 / 4 |
| AnalyticsTests | 14 / 14 | 0 / 0 |
| AttentionSchedulerTests | 25 / 28 | 0 / 0 |
| BoardMessageTests | 6 / 10 | 2 / 6 |
| BoardReadModelsTests | 5 / 5 | 0 / 0 |
| BugHuntTests | 12 / 20 | 2 / 2 |
| ChatDecisionTests | 17 / 26 | 7 / 13 |
| CollaborationHostTests | 8 / 8 | 1 / 1 |
| CollaborationTests | 23 / 27 | 5 / 5 |
| ContractsTests | 12 / 17 | 1 / 1 |
| ControlStateShapeTests | 2 / 2 | 0 / 0 |
| CoordinationTests | 67 / 71 | 17 / 19 |
| CoreSiloTests | 14 / 14 | 2 / 2 |
| DashboardActivityRetentionTests | 2 / 3 | 0 / 0 |
| DashboardAnalyticsConditionalTests | 2 / 2 | 0 / 0 |
| DashboardAnalyticsPresentationTests | 3 / 10 | 0 / 0 |
| DashboardAnalyticsTests | 7 / 18 | 1 / 1 |
| DashboardAuthenticationTests | 8 / 15 | 2 / 5 |
| DashboardBindingReportTests | 1 / 6 | 0 / 0 |
| DashboardClientStateTests | 3 / 5 | 0 / 0 |
| DashboardDocCacheRenderTests | 2 / 2 | 0 / 0 |
| DashboardDocumentInventoryTests | 2 / 2 | 0 / 0 |
| DashboardDocumentUsageProjectionTests | 4 / 4 | 0 / 0 |
| DashboardExpiredSessionTests | 5 / 5 | 0 / 0 |
| DashboardFreshnessReviewTests | 6 / 7 | 0 / 0 |
| DashboardHostReviewTests | 7 / 9 | 2 / 2 |
| DashboardInteractionTests | 4 / 4 | 0 / 0 |
| DashboardLoggingTests | 4 / 7 | 0 / 0 |
| DashboardManagerManagementTests | 18 / 39 | 6 / 8 |
| DashboardSessionActivityTests | 6 / 9 | 0 / 0 |
| DashboardSessionCleanupTests | 6 / 16 | 4 / 4 |
| DashboardSignInLinkTests | 5 / 8 | 4 / 4 |
| DashboardStorageReviewTests | 8 / 9 | 0 / 0 |
| DashboardTests | 22 / 28 | 8 / 14 |
| DefaultsTests | 3 / 3 | 0 / 0 |
| DiscoveryDomainTests | 14 / 21 | 5 / 5 |
| DiscoveryGrainTests | 15 / 17 | 5 / 5 |
| DiscoveryHostTests | 30 / 51 | 6 / 6 |
| DiscoveryStateShapeTests | 1 / 1 | 0 / 0 |
| DocCacheLazyTests | 5 / 5 | 0 / 0 |
| DocCacheMemoryTests | 2 / 2 | 0 / 0 |
| DocCacheTests | 55 / 71 | 3 / 3 |
| DocDiscoveryTests | 13 / 16 | 3 / 3 |
| DocSearchIndexTests | 10 / 10 | 0 / 0 |
| EffectDeliveryTests | 3 / 18 | 1 / 1 |
| EnrollmentHardeningTests | 17 / 33 | 6 / 17 |
| FeedbackRulesTests | 6 / 6 | 0 / 0 |
| HostTests | 35 / 40 | 12 / 16 |
| HubNetworkTests | 29 / 46 | 8 / 11 |
| HubResponseCompactionTests | 18 / 38 | 5 / 13 |
| InboxEfficiencyTests | 2 / 2 | 0 / 0 |
| InboxWaitAttentionTests | 5 / 7 | 0 / 0 |
| ManagerManagementClientTests | 21 / 34 | 7 / 9 |
| ManagerSelectionContractTests | 2 / 4 | 0 / 0 |
| ManagerSelectionHostTests | 1 / 1 | 0 / 0 |
| ManagerSelectionTests | 19 / 24 | 6 / 6 |
| ManagerStopTests | 9 / 16 | 4 / 4 |
| MinorFixTests | 9 / 12 | 2 / 2 |
| PrincipalReviewTests | 8 / 8 | 2 / 2 |
| PrivateMessageProbeTests | 1 / 1 | 0 / 0 |
| ReadModelsTests | 4 / 25 | 0 / 0 |
| ReplyAcknowledgmentTests | 35 / 35 | 6 / 6 |
| ReplyAddOnsTests | 1 / 1 | 0 / 0 |
| ReplyCompactorTests | 9 / 9 | 3 / 3 |
| ReviewFixTests | 16 / 28 | 3 / 3 |
| SearchExtractionTests | 11 / 11 | 1 / 1 |
| SelfDeliveryTests | 1 / 1 | 1 / 1 |
| SessionCleanupTests | 17 / 23 | 5 / 11 |
| SessionGuidanceTests | 3 / 3 | 0 / 0 |
| SessionLifecycleTests | 27 / 27 | 7 / 7 |
| SessionRenameTests | 3 / 3 | 2 / 2 |
| SharedNamesTests | 8 / 30 | 3 / 8 |
| StandingIdentityTests | 5 / 22 | 5 / 22 |
| StandingManagementTests | 9 / 14 | 6 / 7 |
| StateShapeTests | 3 / 3 | 0 / 0 |
| StorageTests | 33 / 38 | 9 / 9 |
| StoredKeyPathTests | 2 / 2 | 0 / 0 |
| TokenEfficiencyTests | 29 / 35 | 1 / 1 |
| ToolDiscoveryTests | 7 / 50 | 3 / 3 |
| TrafficOriginTests | 9 / 18 | 0 / 0 |
| WaitingOnYouCostTests | 1 / 1 | 0 / 0 |
| WaitingOnYouTests | 63 / 76 | 10 / 10 |
| WaitingUiTests | 6 / 7 | 1 / 1 |
| WorkerListFitTests | 4 / 7 | 0 / 0 |
| DashboardUsefulnessTests | 6 / 19 | 0 / 0 |
