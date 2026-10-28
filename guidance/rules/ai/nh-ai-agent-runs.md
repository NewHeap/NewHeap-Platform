---
id: nh-ai-agent-runs
title: "Run assistant agents on their own from events, with subject threads, deferred proposals, consultations, proposal feedback, revalidation and rule runs"
area: backend
reference: ai-agent-runs
summary: "Start agents from committed application events or from people, run them headless as background operations, keep one thread per agent and subject whose access follows the application's own roles, let people decide the agent's proposals later under a named approval policy, also through the application's own channels, let other assistants consult an agent with the asking person's rights, learn from why people reject its proposals, close proposals that people overtook by hand, run agents by fixed rules without a model, never pile up the same proposal, pause, stop, switch off and resume agents from code and the administration, deploy agents and skills that start off, brief agents about their subject within a token ceiling per run, let one agent act as a role with skills, let agents hand work to each other, evaluate shadow variants against the live agent, put an agent or a skill in shadow and change its technical account at runtime, add skills and triggers to an agent as data without a deploy, let agents created in the administration run with the rights of the account they answer to, say what every agent works on, run agents for the whole application, app-wide or per division, place a run's proposals at the objects they concern, keep every run's result with a history that follows the access of what it is about, group agents on boards that show their latest results and what is new, keep the application's own typed data about agents as metadata, let the application execute approved proposals of its own tools and report how they went, take the instructions of data skills and agents created in the administration from the application's own text assets, let administrators connect agents with hand-offs and try shadow variants as data within ceilings in code and promote a variant onto its live agent, change agent definitions from code and read agents as they are in effect, move agents between hosts as versioned agent packages whose locked parts every save keeps, and group the approval inbox by the parent of each proposal's subject with what is new since the last visit."
sample-cases: ["SPM-268", "SPM-269", "SPM-270", "SPM-271", "SPM-272", "SPM-273", "SPM-274", "SPM-275", "SPM-276", "SPM-277", "SPM-278", "SPM-279", "SPM-280", "SPM-282", "SPM-283", "SPM-284", "SPM-285", "SPM-286", "SPM-287", "SPM-288", "SPM-290", "SPM-291", "SPM-292", "SPM-293", "SPM-294", "SPM-295", "SPM-296", "SPM-297", "SPM-298", "SPM-301", "SPM-302", "SPM-303", "SPM-304", "SPM-306", "SPM-307", "SPM-308", "SPM-310", "SPM-311", "SPM-313", "SPM-314", "SPM-316", "SPM-317", "SPM-318", "SPM-319", "SPM-320", "SPM-321", "SPM-324", "SPM-326", "SPM-327", "SPM-328", "SPM-329", "SPM-330", "SPM-331", "SPM-333", "SPM-339", "SPM-343", "SPM-344", "SPM-348", "SPM-349", "SPM-351", "SPM-352", "SPM-353", "SPM-366"]
public-symbols: ["ConfigureAgent", "NhAssistantAgentRunBuilder", "NhAssistantAgentRunOptions", "NhAssistantAgentBudget", "AddApprovalPolicy", "NhAiApprovalPolicyBuilder", "NhAiApprovalPolicy", "NhAiApprovalMode", "NhAiApprovalExpiryAction", "NhAiApprovalBinding", "NhAiApprovers", "NhAiApproverRule", "AddSubjectType", "INhAiSubjectAccessResolver", "INhAiSubjectAccess", "NhAiSubjectRef", "NhAiSubjectInfo", "NhAiSubjectAccess", "NhAiSubjectMember", "NhAiSubjectRole", "AddTrigger", "NhAssistantTriggerBuilder", "NhAssistantTriggerConflict", "NhAssistantTriggerIds", "INhAssistantAgentTriggers", "NhAssistantTriggerReceipt", "NhAssistantTriggerOutcomes", "NhAssistantTriggerReasons", "AddAssistantAgentRuns", "NhAssistantAgentRunOperationsBuilder", "INhAssistantAgentRunLookup", "NhAssistantTriggerEventRun", "NhAssistantAgentRunFacts", "FindRunAsync", "NhAiAgentAccountability", "NhAiGrantSource", "NhAiAgentGrant", "NhAiElevatedReads", "NhAiAgentInvocationScope", "NhAiAgentInvocationScopes", "NhAiAgentFailureCodes", "INhAiAgentToolAuthorizer", "INhAiProposalPrecondition", "NhAiProposalPreconditionContext", "NhAiPreconditionResult", "INhAiPrincipalFactory", "NhAiSubjectAuthorizationResource", "NhAssistantRunStatuses", "NhAssistantApprovalModes", "NhAssistantApprovalStatuses", "AddScheduledTrigger", "NhAssistantScheduleBuilder", "NhAssistantScheduledTick", "AddWebhookTrigger", "NhAssistantWebhookBuilder", "NhAssistantWebhookDelivery", "NhAssistantKillSwitchDto", "NhAssistantKillSwitchRequest", "INhAssistantAgentNotificationPolicy", "INhAssistantAgentNotificationChannel", "NhAssistantAgentNotificationContext", "NhAssistantAgentNotificationKind", "NhAssistantAgentNotificationObject", "NhAssistantDefaultAgentNotificationPolicy", "UseAgentNotificationPolicy", "AddAgentNotificationChannel", "INhAssistantAgentMemoryMigrator", "UseAgentMemoryMigrator", "MaxRunSummaries", "MaxArtifactBytes", "INhAiArtifacts", "NhAiArtifact", "NhAiArtifactInfo", "INhAiSubjectRetention", "ForgetSubjectAsync", "NhAssistantThreadMemoryDto", "NhAssistantArtifactDto", "NhAssistantProposalDto", "INhAssistantToolPresenter", "NhAssistantConversationThreadDto", "NhAssistantThreadDecisionDto", "NhAssistantConversationKinds", "NhAssistantMessageAuthorKinds", "AllowConsultation", "NhAssistantConsultationBuilder", "NhAssistantConsultationOptions", "NhAssistantConsultationTools", "NhAssistantConsultationAnswer", "NhAssistantAgentRequestReceipt", "NhAssistantConsultationStatuses", "NhAssistantConsultationDto", "NhAssistantConsultationListDto", "NhAssistantConsultationProposalBuilder", "NhAssistantConsultationRights", "NhAssistantConsultationRoutes", "NhAssistantConsultationRefusalReasons", "NhAssistantConsultationDecisions", "NhAssistantConsultationRouteOverrides", "SetConsultationRoutesAsync", "NhAssistantConsultationRoutesState", "NhAssistantConsultationRouteState", "NhAssistantConsultationRoutesRequest", "NhAssistantRouteSwitches", "NhAssistantApprovalConsultationDto", "MaxOpenConsultationProposalsPerAsker", "MaxConsultationDepth", "MaxConsultationsPerActorPerDay", "RequireApprovalForEffects", "NhAssistantProposalFeedbackCategories", "NhAssistantProposalFeedbackGroups", "NhAssistantFeedbackTargetKinds", "INhAssistantProposalFeedbackSink", "NhAssistantProposalFeedback", "AddProposalFeedbackSink", "MaxRejectionsInContext", "RejectionContextWindow", "MaxFeedbackCharacters", "NhAssistantProposalFeedbackRequest", "NhAssistantBulkProposalFeedbackRequest", "NhAssistantProposalFeedbackDto", "NhAssistantBulkProposalFeedbackDto", "NhAssistantFeedbackOutcomeDto", "NhAssistantFeedbackSummaryDto", "NhAssistantFeedbackSummaryRowDto", "NhAssistantFeedbackCategoryCountDto", "NhAssistantFeedbackListDto", "NhAssistantFeedbackItemDto", "NhAssistantFeedbackExportRecord", "NhAssistantLiveAgentWorkDto", "NhAssistantToolCallStatuses", "INhAssistantSubjectWork", "NhAssistantSubjectWorkExtensions", "NhAssistantSubjectWorkDto", "NhAssistantSubjectAgentDto", "NhAssistantSubjectProposalDto", "NhAssistantSubjectSummaryRequest", "NhAssistantSubjectSummaryDto", "NhAssistantSubjectSummaryListDto", "NhAssistantOpenThreadRequest", "INhAiProposalGuard", "NhAiProposalGuardContext", "NhAiProposalKey", "NhAiProposalGuardStages", "NhAiStaleKind", "AddProposalGuard", "AddProposalRevalidation", "NhAssistantRevalidationBuilder", "INhAssistantProposalRevalidation", "NhAssistantRevalidationResult", "NhAssistantInvalidatedProposal", "NhAssistantStaleKinds", "NhAssistantInvalidationStages", "NhAssistantRevalidateProposalsRequest", "NhAssistantInvalidationCountDto", "INhAssistantAgentRuleHandler", "NhAssistantRuleContext", "NhAssistantRuleResult", "NhAssistantRuleInput", "NhAssistantRuleProposal", "NhAssistantRuleDecision", "NhAssistantProposeOutcome", "UseRuleHandler", "UseModel", "NhAssistantRunKinds", "NhAssistantRunDto", "NhAiDuplicateProposals", "RejectionCooldown", "MaxRejectionCooldown", "MaxInputPayloadCharacters", "NhAssistantProposalObjectDto", "NhAssistantProposalLookupRequest", "NhAssistantProposalLookupDto", "INhAssistantProposalDecisionService", "NhAssistantDecidingPerson", "NhAssistantProposalDecision", "NhAssistantProposalDecisionFacts", "NhAssistantProposalDecisionResult", "NhAssistantDecisionChannels", "INhAssistantAgentControl", "NhAssistantAgentState", "NhAssistantAgentStateRequest", "NhAssistantAgentRunStates", "MaxStateReasonCharacters", "INhAssistantSubjectBriefingProvider", "NhAssistantBriefingContext", "NhAssistantBriefingPurposes", "NhAssistantSubjectBriefing", "NhAssistantBriefingSection", "AddSubjectBriefing", "MaxBriefingCharacters", "BriefingTimeout", "MaxTokensPerRun", "Skill", "NhAssistantSkillBuilder", "NhAssistantSkillOptions", "Triage", "NhAssistantTriageBuilder", "NhAssistantSkillChoosers", "UseSkill", "PauseSkillAsync", "StopSkillAsync", "ResumeSkillAsync", "SwitchOffAsync", "SwitchOffSkillAsync", "SetKillSwitchAsync", "StartsOff", "NhAssistantDataClassificationNames", "NhAssistantConfigurationWarnings", "NhAssistantSkillState", "AcceptHandOffs", "NhAssistantHandOffBuilder", "NhAssistantHandOffOptions", "NhAssistantHandOffTools", "HandOffAsync", "NhAssistantHandOffReceipt", "NhAssistantHandOffOutcomes", "NhAssistantHandOffResult", "MaxHandOffDepth", "ShadowOf", "NhAssistantEvaluationDto", "NhAssistantEvaluationRunsDto", "NhAssistantEvaluationToolDto", "NhAssistantEvaluationDecisionsDto", "NhAssistantEvaluationCostDto", "NhAssistantEvaluationRoutingDto", "NhAssistantEvaluationDifferenceDto", "NhAssistantEvaluationDifferenceKinds", "NhAssistantEvaluationExportRecord", "NhAssistantReplayRequest", "NhAssistantReplayDto", "NhAssistantReplayStatuses", "NhAssistantEvaluationLimits", "INhAiAgentInvocationContextFactory", "AllowGatewayReads", "SetShadowAsync", "SetSkillShadowAsync", "SetServiceAccountAsync", "NhAssistantShadowState", "NhAssistantServiceAccountState", "NhAssistantShadowRequest", "NhAssistantServiceAccountRequest", "NhAssistantShadowChoices", "AddTriggerEvent", "NhAssistantTriggerEventBuilder", "NhAssistantTriggerFilterContext", "AddSubjectSet", "AddRuleHandler", "INhAssistantAgentConfiguration", "NhAssistantRunCatalogDto", "NhAssistantRunBlockDto", "NhAssistantAgentConfigurationDto", "NhAssistantCodeSkillDto", "NhAssistantCodeTriggerDto", "NhAssistantCodeRunSettingsDto", "NhAssistantDataSkillDto", "NhAssistantDataSkillVersionDto", "NhAssistantDataTriggerDto", "NhAssistantDataSkillRequest", "NhAssistantDataTriggerRequest", "NhAssistantRunConfigurationProblem", "NhAssistantRunConfigurationProblems", "NhAssistantTriggerConflictNames", "NhAssistantAgentConfigurationLimits", "NhAssistantSkillDefinitionStatuses", "NhAssistantDataTriggerSources", "ActWithAccountableRights", "NhAssistantAgentRunSettingsDto", "NhAssistantAgentRunSettingsRequest", "NhAssistantElevatedReadNames", "NhAssistantSkillApplyModes", "NhAssistantSkillCandidateRequest", "NhAssistantSkillComparisonDto", "NhAssistantSkillVersionStatsDto", "NhAssistantSkillComparisonToolDto", "PromoteSkillCandidateAsync", "DiscardSkillCandidateAsync", "CompareSkillCandidateAsync", "GetTriggerVersionsAsync", "PromoteTriggerCandidateAsync", "DiscardTriggerCandidateAsync", "CompareTriggerCandidateAsync", "NhAssistantDataTriggerVersionDto", "NhAssistantTriggerComparisonDto", "HideFromChat", "CreateAgentAsync", "NhAssistantAgentRequest", "NhAssistantApprovalPolicySummaryDto", "NhAssistantApproverSummaryDto", "NhAssistantAccountDto", "NhAssistantSubjectTypeBuilder", "INhAiSubjectSearch", "NhAiSubjectSearchRequest", "NhAssistantSubjectTypeDto", "NhAssistantSubjectSearchDto", "NhAssistantSubjectSearchResultDto", "NhAssistantAgentWorksOn", "WorksOn", "WorksOnAllObjects", "WorksOnApplication", "PerDivision", "RunPolicy", "ViewPolicy", "AddAgentPolicy", "NhAiSubjectRef.SystemType", "NhAssistantAdminAgentInputDto", "NhAssistantAdminAgentDto", "NhAiSystemSubjects", "UseSystemSubject", "NhAssistantSystemSubjectBuilder", "NhAssistantObjectAgentsDto", "NhAssistantObjectAgentDto", "NhAssistantLatestRunDto", "NhAiSubjectAttribute", "NhAiToolSubject", "MaxPlacedProposalsPerRun", "NhAssistantPlacementReasons", "NhAssistantProposalOriginDto", "MaxRunResultCharacters", "NhAssistantRunHistoryDto", "NhAssistantRunHistoryListDto", "NhAssistantRunResultDto", "AddAgentGroup", "NhAssistantAgentGroupBuilder", "InGroup", "INhAssistantAgentGroups", "NhAssistantAgentGroupDto", "NhAssistantAgentGroupMembershipDto", "NhAssistantAgentGroupsRequest", "NhAssistantBoardListDto", "NhAssistantBoardSummaryDto", "NhAssistantBoardDto", "NhAssistantBoardAgentDto", "NhAssistantBoardWorksOnDto", "NhAssistantBoardSubjectTypeDto", "NhAssistantBoardVisitDto", "NhAssistantLiveWatchDto", "NhAiSubjectParent", "NhAssistantProposalParentDto", "NhAssistantInboxDto", "NhAssistantInboxGroupDto", "NhAssistantInboxSubjectGroupDto", "NhAssistantInboxVisitDto", "UseConsultationsAsData", "ConsultableAsData", "NhAssistantConsultationCeiling", "NhAssistantConsultationCeilingBuilder", "NhAssistantConsultationSources", "NhAssistantSubjectRoleNames", "SaveConsultationAsync", "RemoveConsultationAsync", "NhAssistantDataConsultationRequest", "NhAssistantDataConsultationDto", "NhAssistantCodeConsultationDto", "NhAssistantConsultationCeilingDto", "SetConsultationStateAsync", "NhAssistantConsultationStateRequest", "NhAssistantConsultationStates", "StartsInShadow", "NhAssistantTriggerKinds", "UseSingleTenant", "AddAgentMetadata", "NhAssistantAgentMetadataBuilder", "NhAssistantAgentMetadataErrors", "NhAssistantAgentMetadataLimits", "NhAssistantAgentMetadataSources", "INhAssistantAgentMetadata", "NhAssistantAgentWithMetadata", "NhAssistantAgentMetadataValue", "NhAssistantAgentMetadataEntryDto", "NhAssistantAgentMetadataDto", "NhAssistantAgentMetadataRequest", "GetMetadataAsync", "SaveMetadataAsync", "RemoveMetadataAsync", "ExecuteInApplication", "UseProposalExecutor", "INhAssistantProposalExecutor", "NhAssistantProposalHandOff", "INhAssistantProposalExecutionReports", "NhAssistantExecutionReport", "NhAssistantExecutionOutcome", "NhAssistantExecutionReportReceipt", "DecisionCulture", "NhAssistantApplicationExecutionCodes", "UseTextAssetSource", "INhAiTextAssetSource", "NhAssistantInstructionAssetReference", "NhAssistantInstructionAssetListDto", "NhAssistantInstructionAssetDto", "NhAssistantInstructionAssetTextDto", "UseHandOffsAsData", "NhAssistantHandOffCeiling", "NhAssistantHandOffCeilingRule", "NhAssistantHandOffCeilingBuilder", "NhAssistantHandOffCeilingRuleBuilder", "NhAssistantHandOffSources", "NhAssistantDataHandOffLimits", "INhAssistantDataHandOffs", "NhAssistantAgentHandOffsDto", "NhAssistantCodeHandOffDto", "NhAssistantDataHandOffDto", "NhAssistantHandOffCeilingDto", "NhAssistantHandOffAllowanceDto", "NhAssistantDataHandOffRequest", "UseShadowVariantsAsData", "NhAssistantShadowVariantCeiling", "NhAssistantShadowVariantCeilingBuilder", "NhAssistantShadowVariantSources", "NhAssistantShadowVariantsDto", "NhAssistantShadowVariantDto", "INhAssistantAgentPackages", "NhAssistantAgentPackage", "NhAssistantAgentPackageInfo", "NhAssistantPackageAgent", "NhAssistantPackageAgentDefinition", "NhAssistantPackageRunSettings", "NhAssistantPackageSkill", "NhAssistantPackageTrigger", "NhAssistantPackageConsultation", "NhAssistantPackageHandOff", "NhAssistantAgentPackageFormat", "NhAssistantAgentPackageFormatJsonSerializerContext", "NhAssistantAgentPackageJsonSerializerContext", "NhAssistantAgentPackageExportOptions", "NhAssistantAgentPackageExportDto", "NhAssistantAgentPackageWarningDto", "NhAssistantAgentPackageWarnings", "NhAssistantAgentPackageImportOptions", "NhAssistantPackagePartReference", "NhAssistantAgentPackagePartDto", "NhAssistantPackagePartOutcomes", "NhAssistantAgentPackagePreviewDto", "NhAssistantAgentPackageImportDto", "NhAssistantAgentPackageUnlinkDto", "NhAssistantPackagePartLinkDto", "NhAssistantAgentPackageLinkDto", "NhAssistantAgentPackageExportRequest", "NhAssistantAgentPackageImportRequest", "NhAssistantAgentPackageUnlinkRequest", "NhAssistantAgentPackageRoutes", "NhAssistantPackagePartKinds", "NhAssistantPackageLockFields", "INhAssistantInstructionAssetUsage", "NhAssistantInstructionAssetUsageDto", "NhAssistantInstructionAssetSkillUseDto", "NhAssistantInstructionAssetAgentUseDto", "GetPackageAsync", "NhAssistantInstalledAgentPackageDto", "NhAssistantPackageAgentLinksDto", "NhAssistantApplicationExecutionBuilder", "AlsoInline", "INhAssistantSystemMembers", "Members", "NhAssistantInboxStatuses", "INhAssistantProposalCounts", "MaxConsultationsPerTurn", "INhAssistantAgentDefinitions", "NhAssistantAgentDefinitionRequest", "PromoteShadowVariantAsync", "NhAssistantShadowVariantPromotionRequest", "NhAssistantShadowVariantPromotionDto", "NhAssistantShadowVariantPromotionInputDto", "NhAssistantShadowVariantPromotedDto", "INhAssistantAgentDirectory", "NhAssistantAgentDirectoryFilter", "NhAssistantAgentInEffectDto", "IndexByEach", "NhAssistantResolveExecutionRequest", "NhAssistantApprovalPartDto", "SkillsInChat", "NhAssistantMessageDto", "ChatOnly", "NhAssistantShadowVariantPromotions", "ProposeCandidatesAsync", "NhAssistantInstructionAssetCandidatesDto", "NhAssistantInstructionAssetCandidateDto", "NhAssistantInstructionAssetCandidateOutcomes", "NhAssistantUncarriedParts", "UncarriedParts", "KeptLinked", "NhAssistantAgentPackageReadRequest", "NhAssistantAgentPackageReadDto"]
skills: ["newheap-backend-development", "newheap-background-processing"]
providers: ["sql-server", "postgresql"]
risk: high
---
## Preferred approach

Register the agent with `AddAgent` as for chat, then describe how it runs on its
own with `ConfigureAgent`. The agent answers for its runs itself by default
(`NhAiAgentAccountability.Agent`); choose `SubjectResponsible` or
`ServiceAccount(actorId)` when a person or technical account must answer
instead. Grant tools beyond the rights of the person the agent works for only
with two keys: the tool opts in with `NhAiToolAttribute.AllowAgentGrant` (and
carries an authorization policy), and the registration grants it with `Grant`.
Use `TriggerOnly()` for grants that must not apply while a person chats in the
thread, and `DelegateFromInitiator()` only when the agent may also use what its
initiator may use. A turn that answers another person's message, such as one typed while the run
worked that the run claimed, does not delegate: only the agent's own grants and accountable rights
serve it, so a person never directs calls with someone else's rights. Keep `NhAiElevatedReads.Restricted` unless the results of
granted reads may be shown to everyone who sees the thread.

Give every agent action that changes state a named approval policy with
`AddApprovalPolicy` and `UseApprovalPolicy`. `NhAiApprovalMode.Deferred`
records the proposal, gives the model a synthetic "recorded" result and lets the
run end; people decide in the thread or in `GET approvals/inbox` later.
`NhAiApprovalMode.Inline` suspends the headless run until the decision. Approvers
are subject roles (`NhAiApprovers.SubjectRole`), the initiator or a policy;
`FourEyes()` keeps the initiator out, `RequireUnchangedSubject()` closes a
proposal as stale when the subject changed before execution, and refuses an approval of
a changed subject with `assistant-proposal-stale` at the decision, closing it the same
way (SPM-271), and every approver must also satisfy the tool's approval policy. Approved proposals execute as their
own background operation; a non-idempotent tool runs at most once, and an
execution whose outcome is unknown waits for a person in `resolve`. A proposal waits
for its policy's `Lifetime` on the application's clock (a registered `TimeProvider`,
otherwise the system clock): its expiry, reminders and escalation, every decision's
`DecidedAt`, the rejection cooldown and every check whether it expired (the inbox,
the thread, the decision, the maintenance job and the invoker's validation) read
that clock, and an inline run's wait expires on it too. `CreatedAt`, which proposal
guards receive as `ProposedAt`, executions and audit events keep the system clock.
A test lets a proposal expire by moving its clock past the lifetime instead of
shortening it or waiting.

When the application itself executes what a tool proposes, for example an order that a purchasing
system's supplier confirms hours later, declare the tool with `ExecuteInApplication(toolId, reportWithin)`
and register the application's `INhAssistantProposalExecutor` with `UseProposalExecutor<TExecutor>()`;
startup fails when a declared tool has no executor. The declaration is opt-in per tool, so every other
tool keeps executing in its function. A run or a consultation always leaves a proposal of a declared tool
deferred, at its subject, because the application's result never reaches a waiting turn; only a person's
own turn, a chat or a request in an agent thread, still approves it inline and runs the function. Once a
person approves it, through the decision endpoints, `INhAssistantProposalDecisionService` or a
consultation's proposal at its subject, the checks of every execution run (the subject version under
`RequireUnchangedSubject` and the proposal guards), the proposal becomes `awaiting-application` with
`ExecutionReportDueAt`, and `ExecuteAsync` receives an `NhAssistantProposalHandOff`: the approval id,
the approved `NhAiProposal` with its arguments and hash, the approver, the agent, the subject, the
thread's tenant, the attempt and the report deadline. Accept the work and return; key it by the approval
id, because an approver may hand the same proposal over again. The hand-off carries the approved
arguments, not the division or scope the call was authorized in, so bind an object the arguments name
again before acting on it: read the object's division and refuse unless `DecidedByActorId` may act
there (SPM-314 refuses an order for another division's project with `order-project-out-of-scope`). The
hand-off and the receipt of its report carry `DecisionCulture`, the UI culture the person approved in, so
the application can address them in their language, as the SPM-348 order desk confirms an order to its
approver: the request culture of the decision endpoints, `NhAssistantDecidingPerson.Culture` of a
decision through `INhAssistantProposalDecisionService` (the language of the message the channel asked
them in), or the culture of the person's turn for an inline approval. Only a supported UI culture of
`RequestLocalizationOptions.SupportedUICultures` is recorded, by its exact name or as the supported
culture of its language (`nl-BE` as `nl-NL`); another language records `null` and the decision stands,
as does a proposal approved before the culture was recorded. A resolution that hands the proposal over
again keeps it, and the assistant's own posts into an agent thread keep the default culture. A
failed result refuses the proposal
(`execution-failed` with the result's code), and an exception leaves it `execution-unknown`
(`hand-off-failed`). Report with `INhAssistantProposalExecutionReports.ReportExecutionAsync`, built with
`NhAssistantExecutionReport.For(handOff, outcome, code, summary)`: `NhAssistantExecutionOutcome.Completed`
closes the proposal as `executed`, `Failed` as `execution-failed` and `NeedsAttention` as
`execution-unknown`, each with the application's dash-case code of at most 64 characters as its
execution result. The content-free audit records `AgentProposalHandedToApplication` and then
`AgentProposalExecuted` with `ExecutionOutcome` and `ExecutionCode`, never the summary; the approvers are
notified (`NhAssistantAgentNotificationKind.ExecutionReported`, or `ExecutionUnknown` for a report that
needs attention); the summary of at most 2,000 characters is posted into the agent's thread as an
application message without the trusted flag, which the follow-up run reads as information, never as
instructions; and the follow-up run starts with the outcome and the code when the policy asks for one,
once per hand-off. A report reaches only a proposal of its `TenantId`, or of the whole application for a
reporter without a tenant or of the single-tenant mode's tenant; any other reads as
`assistant-approval-not-found`. The same report again returns the proposal with `Duplicate`, and a report
on a proposal that does not wait for one is `assistant-execution-report-not-awaited`. Without a report
before the deadline, on the application's clock, the maintenance job makes the proposal
`execution-unknown` (`report-overdue`) and tells the approvers, who resolve it with
`hand-to-application` or `mark-resolved`; `re-execute` is refused for it, as `hand-to-application` is for
a proposal the application does not execute.

When a person's own inline approval should reach the application too, declare the tool with
`ExecuteInApplication(toolId, reportWithin, execution => execution.AlsoInline())`
(`NhAssistantApplicationExecutionBuilder`, SPM-328). Without `AlsoInline` a person who approves such a
proposal in their own turn, a chat or a request in an agent thread, runs the tool's function there (AE3),
which stays the default. With it the decision hands the approved proposal to the executor as a deferred
approval is handed: `awaiting-application` with `ExecutionReportDueAt`, `AgentProposalHandedToApplication`
in the audit (without a subject for a chat), and `NhAssistantProposalHandOff.Inline` set; a chat belongs
to no object, so its hand-off names the proposal's first target, or `nh-system/global` when the tool
names none, and the conversation's tenant. The tool's function never runs. The turn goes on with a tool
result that says the application has it (`assistant-proposal-handed-to-application`), that it refused it
(the refusal's code, tools disabled) or that whether it took it is unknown (`hand-off-failed`), and the
tool call keeps waiting as a deferred call does until the report ends it. In an agent thread everything
above applies. In a chat, which has no approvers, nobody is notified and no follow-up run starts: every
close is posted into that conversation as an application message without the trusted flag, the outcome
with its code and the report's summary, so the report, a refused hand-off and a passed deadline reach
the person who approved. The approval records the UI culture of the person's turn that approved it
(`AssistantApproval.DecisionCulture`, the `ApprovalCulture` migrations, which deferred decisions record
as well), and these posts, their title
included, use that culture when the application still supports it as a UI culture
(`RequestLocalizationOptions.SupportedUICultures`), otherwise the default request culture, as for an
approval handed off before the culture was recorded; an agent thread keeps the default culture of reports
and deadlines (DN8). The person who approved resolves an uncertain chat proposal with the rules of
an agent thread's `resolve` through `POST conversations/{id}/approvals/{approvalId}/resolve`
(`NhAssistantResolveExecutionRequest`): `hand-to-application` runs the execution guards and hands it to
the application again with a new attempt and a new deadline, `mark-resolved` confirms it, `re-execute`
and an agent thread's proposal are `400 assistant-approval-decision-invalid`, another participant gets
`403 assistant-approval-forbidden`, another status `409 assistant-execution-not-uncertain`, and the audit
records `AgentProposalResolved`; the answer is the conversation, whose approval part says `CanResolve`
only to that person (`NhAssistantApprovalPartDto.ExecutionResultCode`, `ExecutionReportDueAt`,
`CanResolve`). The maintenance job also expires these deadlines in a host without agent runs.

A proposal that nobody decided in time expires, and the policy's `OnExpiry` says what
happens to its run. `NhAiApprovalExpiryAction.FailRun` fails the run with
`assistant-approval-expired` and the failure class `policy`, and notifies like any failed
run; `Withdraw` lets it complete without the proposal, with the same failure code.
`Escalate` gives a deferred proposal the escalation policy's lifetime once; an inline
proposal never escalates and expires as with `Withdraw`. A run that waits inline is always
ended by its own background operation when its wait expires, also when the maintenance job
expired the proposal first, so the outcome does not depend on which of the two came first
and the run ends, and is audited, once. The job applies `FailRun` itself only to the run of a
deferred proposal, which no longer waits for it.

A run whose background operation ended without it is ended by the maintenance job. That
happens when an operator cancels the operation in the background-operation administration or a
`Supersede` trigger cancels it, when reconciliation fails it as `dispatch-stalled`, or when a
worker fails it for good before the run's handler ended the run (a payload schema mismatch, a
redacted payload, a timeout or retries that ran out), for example while the run waits inline
for a decision. Once the operation has been terminal for the background operations'
`StaleAttemptTimeout` on the system clock, the job ends the run once, through the same end as
the operation: a cancelled operation cancels it with `assistant-run-operation-cancelled` (class
`cancelled`), `dispatch-stalled` fails it with `assistant-run-operation-stalled` and every other
end fails it with `assistant-run-operation-ended` (class `internal`), with its summary,
`AgentRunEnded`, work update, hand-off follow-ups and, when it failed, `RunFailed`. Its pending
inline proposals close first, `expired` when their lifetime passed on the application's clock
and `withdrawn` otherwise; an inline proposal a person approved after the operation ended, which
so never executed, is `withdrawn` with the run's failure code and executes nothing. The thread's
turn that waited for them is released, so the agent's next run starts; deferred proposals keep
their own lifecycle. A running or waiting run whose operation is gone, because the cleanup of
ended operations removed it, fails with `assistant-run-operation-ended` once the run started (or
was created) at least that timeout ago. The job checks a bounded page of open runs per round, in the order
of their creation time and id, continuing after the last one, and leaves a run whose operation
still waits, is due or runs alone. A run that a person or the kill switch cancels while it waits
inline ends at once and releases that turn as well. Such a cancel, and subject retention, leave
an inline proposal that was approved but not executed yet `approved`, because the run's operation
may still be starting and about to execute it. Once that operation has been terminal for
`StaleAttemptTimeout` on the system clock (or is gone and the run ended that long ago), the job
withdraws it, once and without executing it, with the run's failure code as its result, or the
run's status (`cancelled`) when the run ended without one; the decider is kept. A proposal that is
executing or whose execution is unknown stays with the execution recovery. Cancel a run with
`POST runs/{id}/cancel` rather than its operation. Resuming a run that ended this way (`retry` with `mode: "resume"`)
retries its operation, which drops the wait the run gave up and starts a new turn in the thread;
`mode: "restart"` starts a new run instead.

`GET conversations/{id}` of a thread says who decides, so a chat client does not apply
the chat rule that only a turn's author decides: it returns `kind: "thread"` and
`thread` (`NhAssistantConversationThreadDto`) with the subject, the caller's live role
and, for each open proposal (`NhAssistantThreadDecisionDto`), its run, `canDecide` and a
`reasonCode` such as `assistant-four-eyes-required`, from the same live check as the
decision. Decide a proposal with a run through `POST approvals/{id}/decisions` with its
proposal hash; a proposal of a person's own turn in the thread resumes that turn through
the conversation's `decide` stream. Messages a trigger wrote into the thread carry
`authorKind: "trigger"` (`NhAssistantMessageAuthorKinds`), so clients show them as events
rather than as a person, and the message of a rule run carries `authorKind: "rule"`; agent
messages never carry an `authorActorId`.

Let people decide through the application's own channels, such as a reply to a message that
asked an approver, with `INhAssistantProposalDecisionService`. It is a server API whose caller
vouches for the authentication of the person: pass the person as `NhAssistantDecidingPerson` with
the principal and actor id the application authenticated through its channel (for example a reply
token it issued to that person, and claims rebuilt from its user store as their sign-in would give
them), never an id taken from the message itself, and with the tenant the person signs in with. The
tenant is required; pass `null` only in a host without tenants or for its operators. Before the application asks someone, read
`GetDecisionFactsAsync`: the current proposal hash, the expiry, whether four eyes apply, the
approvals still needed and whether this person may decide, with the refusal code otherwise, and no
proposal content; nothing changes, so the proposal guards run only when the person decides.
`SubjectChanged` says whether the object changed since the proposal, by the precondition the
execution runs (`INhAiProposalPrecondition`, by default the subject versions compared): `true`,
`false`, or `null` for a proposal that is no longer pending or a subject without versions. Tell
the person: with `RequireUnchangedSubject` an approval of a changed object is refused with
`assistant-proposal-stale`, while a rejection is still recorded.
`DecideAsync(person, approvalId, expectedHash, decision, channel, idempotencyKey)` is the decision of
`POST approvals/{id}/decisions` through the same implementation: the approvers and the tool's
approve policy, four eyes, the hash, the proposal guards and `RequireUnchangedSubject`, the expiry on
the application's clock, the execution, waiting run, follow-up run and rejection cooldown that follow,
and the same refusal codes. Proposals that a person decides in a conversation (their own chat turn,
or a consultation the asker decides) are refused there as here. The channel is a dash-case code
(`NhAssistantDecisionChannels`, at most 50 characters) that the content-free audit records as
`Channel` on `ApprovalApproved` and `ApprovalRejected`, where the endpoints record `web`; the
idempotency key, such as the delivery id of the reply, is recorded only as the audit's
`IdempotencyKey`. Decisions are idempotent by state: a repeat by the same person with the same
decision and hash returns the earlier decision with `Duplicate` and is not audited again, and
anything else, such as another answer of the same person, gets the ordinary refusal. The endpoints
keep answering a repeat with `409 assistant-approval-not-pending`.

Register an `INhAssistantToolPresenter` (`AddToolPresenter`) for every tool an agent
may propose. Approvers often decide days later and outside the conversation; the
proposal then carries the application's localized presentation, built from the real
arguments and current data (for example the project's name with its current and new
status), and the tool input stays under technical details. The assistant presents each
proposal in every supported UI culture of `RequestLocalizationOptions` when it is made
(at most eight), and every reader gets the presentation of their request culture, also
when a headless run in the default culture made the proposal; keep the presenter's
output complete in each supported language. The tool call of a proposal that closed
without running shows `rejected` or `not-executed` instead of waiting.

Agent-run clients refresh through the live hub: `AgentWorkChanged` tells the thread's
participants and everyone in the subject's member snapshot that a run, a proposal or
its feedback changed, without content, so an approval inbox updates before its owner
opened the thread. Each of them hears it only while the subject's resolver gives them a role,
or they were invited to the thread, at the moment it is sent; the thread's messages and turn
events follow the same rule. An update that names a run, about the run or one of its proposals, also
reaches the person who started that run while `ResolveAsync` still gives them a role on the
subject, also when no member list names them, and the first update of a thread whose member
snapshot was never filled fills it. A person who starts an agent by hand therefore sees its
run start and end live, also when the run proposes nothing; reload on those updates instead
of polling the run. A shadow thread's updates reach only its own audience.
`decidedByDisplayName` and the report's `authorDisplayName` come from the member snapshot;
show the id when they are missing.

Read the proposal an update names by its id, not through its thread. `GET approvals/{approvalId}`
returns one proposal of an agent thread in any status as `NhAssistantSubjectProposalDto`: the
proposal with its objects, stale explanation, successor, rationale and the caller's own feedback,
plus `canDecide` and `reasonCode` from the same live check as a decision, for pending proposals
only. `POST approvals/lookup` reads at most `NhAssistantProposalLookupRequest.MaxApprovalIds` (100)
ids in one request and returns the visible ones in request order (`NhAssistantProposalLookupDto`).
Both follow the thread's visibility: a live role on the subject or an invitation to the thread,
shadow agents for administrators only and the caller's tenant; a proposal the caller may not see
answers `404 assistant-approval-not-found` like an unknown one, and a lookup leaves it out. Reading
never joins the thread, and a lookup resolves each subject once. A person's own chat proposals stay
in their conversation. Reload a list for proposals it does not show yet; in the client,
`NhAssistantAgentRunApiService.getProposal` and `lookupProposals` read by id, and the inbox and the
subject suggestions keep closed cards through one lookup instead of reading threads.

Show agent work on the application's own pages, not only in the inbox. A detail page reads
everything on its subject with `GET subjects/{type}/{id}/work` (every agent's thread and the
open proposals of all of them with the caller's live decision right); a list reads badges for
up to 200 subjects with one `POST subjects/{type}/summary`; `POST threads/open` creates the
agent's thread about a subject without a run, so the page can open the conversation and the
first message runs the first turn. The application's own back-end uses
`INhAssistantSubjectWork` (`GetForRequestAsync`, `SummarizeForRequestAsync`) to include the same
data in its endpoints with the same access checks, in the caller's tenant. Outside a request pass
the person with their tenant: `GetAsync(subject, new NhAssistantDecidingPerson(user, actorId,
tenantId), agentId)` and `SummarizeAsync(subjectType, subjectIds, person)`; the overloads that take
an actor id and a principal compare no tenant and are obsolete. Refresh such pages on
`AgentWorkChanged`, which names the subject.

Describe each kind of subject with one `INhAiSubjectAccessResolver` registered
through `AddSubjectType`. `DescribeAsync` returns the scope key, division,
version and responsible person, and optionally `Parent`, the subject it belongs to
(`NhAiSubjectParent`: a `NhAiSubjectRef` and a display name), such as the project of a
task; `ResolveAsync` returns the live role of one
person and is called on every request; `ListMembersAsync` feeds the member
snapshot that listings, notifications and live updates use. Both also run inside
background operations, for notifications and live updates, so keep them free of
request-only state. Call `INhAiSubjectAccess.RefreshAsync` after the people on a
subject change: it replaces the member snapshot and removes the participant rows that a role
created for people it no longer lists and the resolver, asked again, gives no role on the agent's
thread; invitations stay. Live updates and notifications ask the resolver when they are sent
either way, so a stale row reaches nobody. A parent is a label for grouping: the platform never resolves it or grants access
through it, its type needs no resolver (a registered type adds its name and link), and everyone who sees
the subject's proposals reads its name, so give only a name the subject's readers may read. A subject is
never its own parent; the platform refuses such a description.

Describe the subject type for people in the same registration:
`AddSubjectType<TResolver>(type => type.Named(new() { ["en"] = "Project", ["nl"] = "Project" }, fallbackLanguage: "en").NamedPlural(...).ForEntity<Project>().OpenAt("/projects/{id}"))`.
Names are texts per culture name of at most 100 characters with one in the fallback language;
every reader gets the name of their request culture, another culture of the same language or the
fallback, and a type without names shows its id. `ForEntity<T>()` records the entity's short and
full type name as the NewHeap log's `ObjectType` and `ObjectTypeFull` do. `OpenAt` takes a path
within the application or an absolute `https` URL with exactly one `{id}` in its path or query; the
subject id fills it URL-encoded. Let the resolver also implement `INhAiSubjectSearch`, so people
choose a subject by name: `SearchAsync(NhAiSubjectSearchRequest)` searches the person's own
subjects server-side, bounded by `Limit`, with provider-neutral LINQ. The platform keeps only results
whose `ResolveAsync` gives the searching person at least `Reader` in the person's tenant, reads at
most four results per result it shows, and answers `GET subjects/{type}/search?query=&limit=` (2 to 100
characters, at most 50 results) with `NhAssistantSubjectSearchDto`; a type without search answers
`404 assistant-subject-search-unavailable`, and people then give the id. The run catalog lists the
types as `SubjectTypes` (`NhAssistantSubjectTypeDto`: name and plural in the reader's language,
entity, link template, `Searchable`, `IsSystem`), and the thread, proposal, inbox, subject-work and
conversation-thread DTOs carry `SubjectTypeName` (only for a named type) and `SubjectLink`. Startup
fails for a type starting with `nh-` (`NhAiSubjectRef.ReservedTypePrefix`), which the platform
reserves for its own subjects, for two resolvers of one type and for a configuration whose resolver
is not registered; the parameterless `AddSubjectType<TResolver>()` stays valid.

Say what each agent works on: `ConfigureAgent(id, run => run.WorksOn("project", "quote"))` names
registered object types (at most 20), `WorksOnAllObjects()` chooses every type on purpose, and
`WorksOnApplication()` adds the whole application (`NhAiSubjectRef.SystemType`, `nh-system`) for work
that concerns no single object; an agent that says nothing works on all object types and not on the
application (`NhAssistantAgentWorksOn.Undeclared`), as before. `PerDivision()` makes its work for the
whole application exist per division, only for an application that lists its divisions;
`RunPolicy(name)` decides who may run it for the whole application (by default the administration
policy) and `ViewPolicy(name)` who sees its results there (by default its required policy); starts on
an object keep their own rule. An agent created in the administration stores the same statement:
`worksOnSubjectTypes`, `worksOnAllSubjects`, `worksOnApplication`, `perDivision`, `runPolicy` and
`viewPolicy` on `NhAssistantAdminAgentInputDto` and `NhAssistantAgentRequest`, sent together (all
omitted keeps it, a new agent works on all object types), with run and view policies chosen from the
policies the application lists with `AddAgentPolicy(name, description)` (the run catalog's
`AgentPolicies`; `Divisions` says whether per division is offered). Saving answers with field errors
(`worksOn`, `worksOnSubjectTypes`, `perDivision`, `runPolicy`, `viewPolicy`); the migration
`AgentWorkOnObjects` lets existing agents work on all object types. One rule enforces it: every
trigger fire (events, schedules, webhooks, follow-ups, requests and hand-offs), `POST threads/start`,
`POST threads/open` for a new thread, a hand-off and a consultation on a type the agent does not work
on is refused with `assistant-agent-does-not-work-on` and the refused start is audited; `canStart`
reads `false`. A trigger or trigger event that knows its type up front says so with
`Subject("project", e => e.ProjectId.ToString())`: startup then fails when the agent does not work
on it, the run catalog shows the type on the event (`NhAssistantRunBlockDto.SubjectType`), saving a
data trigger on it for another agent answers `event: invalid`, and a data trigger that no longer
fits after an agent's statement changed is out of effect with problem `not-worked-on`. Startup also
fails for `AcceptHandOffs` and `AllowConsultation` on a type the agent does not work on, for an
unknown type in `WorksOn` and for an unregistered run, view or listed policy. The administration
DTO (`NhAssistantAdminAgentDto`) returns the statement, `WorksOnDeclared` for an agent in code that
said it, and `KnownSubjectTypes`, the types it accepts hand-offs and consultations about, its triggers
name and its threads concern, as a hint next to "all objects".

Run an agent for the whole application, such as a weekly count of open projects, on the platform's
own subject type `nh-system`, which the assistant registers itself (the run catalog lists it with
`IsSystem`, named "The whole application" in the reader's language). `NhAiSystemSubjects.Global`
(`nh-system/global`) is the whole application for an agent that works app-wide, the default;
`NhAiSystemSubjects.ForDivision(id)` (`nh-system/division-{id:N}`) is the whole application in one
division for an agent with `PerDivision()`. Its thread, memory, artifacts, run timeline, retention and
budgets work as for any subject, per scope key (`global` or `division:{id}`). Divisions are optional:
per division needs an `INhDivisionDirectory`, which NewHeap Identity registers over its divisions and an
application registers with `AddNewHeapDivisionDirectory<T>()`; without it the run catalog reports
`Divisions: false`, data agents cannot work per division and startup refuses `PerDivision()` in code.
Leave `Subjects` out of a schedule, or the subject set out of a data schedule, to run it for the whole
application: each tick starts one run on `nh-system/global`, or one per listed division, ordered by
name and at most `MaxSubjectsPerTick`, for an agent that works per division. Only an agent that works
on the application may have such a schedule: startup refuses it in code, saving it as data answers
`subjectSet: required`, and one whose agent no longer works on the application is out of effect with
`not-worked-on`. The whole application app-wide is never started for a per-division agent, nor a
division for an app-wide one (`assistant-agent-does-not-work-on`). A run for the whole application
concerns no object: its tool calls have no subject execution scope, and in a division the division is
its execution scope, as an active division is for a person. People who pass the read policy (by
default the assistant's access policy) read it and people who pass the approve policy (by default the
administration policy) also take part and decide its proposals, evaluated for the person as the
principal factory rebuilds them and against the subject with its division, with access to the division
for a division subject; configure both with `UseSystemSubject(system => system.ReadPolicy("app.project.view").ApprovePolicy("app.project.manage"))`.
Name an app-wide read policy when the access policy needs an active division, which the whole
application app-wide has not. The whole application lists no members, so the approval inbox asks the
caller's live role on its subjects that have pending proposals (at most 100, the roles kept a few
seconds) and lists their proposals for its approvers, and `POST subjects/nh-system/summary` counts with
the caller's live role; a caller bound to a tenant decides none of them. Notifications about its
proposals reach the approvers among the people of its thread: those who take part in its conversation
and those who decided its proposals, at most 50, each with the role the resolver gives them now and
only when they pass the agent's view policy. A thread nobody opened therefore notifies nobody, so let
the application list its people with `UseSystemSubject(system => system.Members<TMembers>())`
(`INhAssistantSystemMembers.ListAsync(divisionId)`, scoped, SPM-327): the actor ids of the whole
application app-wide (`null`) or of one division. Notifications then consider the first
`INhAssistantSystemMembers.MaxMembers` (200) of them next to the thread's people, each with the role the
read and approve policies and the agent's view policy give them now, and in a division only for people
who may work there; the list names candidates and grants nothing, so a listed reader never hears what is
meant for approvers. List the people who decide, such as the application's approvers, not every
account; the member snapshot stays empty. With
listed divisions `GET subjects/nh-system/search` finds the
divisions a person works in by name (`Searchable` in the run catalog), as the subjects of a
per-division agent.

Agent work on the application's own subjects belongs to the tenant the resolver describes
(`NhAiSubjectInfo.TenantId`), which the thread stores, and one tenant rule applies on every path. A
caller reaches a thread, its runs, proposals, memory and files only when the thread is stored under
the caller's tenant: the routes by id (`GET threads/{id}/runs`, `consultations`, `proposals`,
`memory`, `artifacts` and an artifact's content, `POST runs/{runId}/cancel` and `retry`), deciding,
deciding several, resolving, feedback, the facts and decisions of
`INhAssistantProposalDecisionService` and the conversation route answer another tenant's thread as
unknown (`404`) before a participant row is written. The routes about a subject (`GET threads`,
`POST threads/open` and `threads/start`, `GET subjects/{type}/{id}/work` and `agents`, `POST
subjects/{type}/summary`) compare the described subject's tenant with the caller's: a subject of
another tenant answers `404 assistant-subject-not-found` before a thread, participant row or run is
made, and counts zero. The approval inbox, flat and grouped, and `INhAssistantProposalCounts` list
only threads of the caller's tenant, whatever an old member snapshot row or a run the person once
started says. The consult and request tools carry the asker's tenant (a person's own turn: the
tenant of their request; a call outside a turn, such as over MCP: its context's; an agent run: its
thread's, and a run of the whole application none, as the proposals it places), so another tenant's
subject, and the whole application for an asker bound to a tenant that takes no part in it, are
`assistant-consult-subject-unavailable`. `ResolveAsync` gets no tenant, so a resolver that gives one
actor id roles in several tenants never widens this boundary. A caller without a tenant who addresses
one thread, run, proposal or subject stored under a tenant reads it as unknown too, and the platform
logs a warning (`NhAssistantTenancy`, at most once an hour per path, with the path and the subject
type only), because that is nearly always a missing `NhAssistantDecidingPerson.TenantId` or a context
resolver that gives no tenant. Listings and callers of another tenant log nothing.

The whole application belongs to no tenant: its threads, runs and proposals are stored without one, and
a caller bound to a tenant takes part in none of its work, neither in listings nor by id: deciding,
resolving, giving feedback, the decision facts of `INhAssistantProposalDecisionService`, `POST
threads/start` and `threads/open`, the thread with its conversation, runs, proposals and files, and the
subject's work and agents answer as unknown (`404`). Only callers without a tenant, such as the host's
operators, decide and run it. A host whose people all belong to one tenant,
such as a deployment per customer that projects them with `UseAuthenticatedClaimsForSingleTenant`,
declares that explicitly with `UseSingleTenant(tenantId)` on the assistant builder; the mode is never
derived from the projection. People of that tenant then take part as callers without a tenant do: the
approval inbox, deciding (also through `INhAssistantProposalDecisionService` with the person's
`TenantId`), boards and division visits, `WatchAgentWork`, the thread and its conversation, the run
history and `GET runs/{id}`, the subject's work and summary, `GET subjects/nh-system/search`, and
running an agent by hand; schedules without a subject set need nothing more, their runs land in the
whole application's threads. A caller of any other tenant still sees none of it and finds its
proposals, threads and subjects unknown (`404`). The read
and approve policies of `UseSystemSubject` and the agent's run and view policies still decide. Nothing
is migrated: the data stays tenantless, so switching the mode off leaves it valid and hides it again
from people with a tenant. Startup fails when the AI ASP.NET integration projects another fixed tenant
or takes an issuer's tenant from a claim, which can carry more than one; an issuer without a tenant
passes. A resolver of the application's own (`UseAuthenticatedInvocationContextResolver`) cannot be
checked and must return the configured tenant or none. With an issuer projection the actor id belongs
to the issuer, so register an `INhAiPrincipalFactory` that rebuilds those people for the roles of the
whole application; when their rights come from a claims transformation, register an `INhAiAccessSource`
with `UsePrincipalsFromAccessSource` instead (see `nh-ai-aspnet-context`). The administration (`UseAdminPolicy`: the runs view, agent control, configuration
and evaluation) administers the host as a whole and keeps no tenant rule, so in a host with several
tenants let its policy admit only operators without a tenant.

Run an agent now, by hand, with `POST threads/start` (`agentId`, `subjectType`, `subjectId`, optional
`message`; the caller is the initiator and the trigger `manual`). One rule decides who may, also for a
new thread of `POST threads/open` and every `canStart`: the agent works on the subject and, for the
whole application, on its scope (`409 assistant-agent-does-not-work-on`), the person passes the
agent's required policy, and on an object the person is a participant; for the whole application
(`nh-system/global`, or `nh-system/division-{id:N}` for a per-division agent) the agent's `RunPolicy`
decides, by default the administration policy, evaluated like the roles of the whole application
(for the rebuilt principal, against the subject with its division, with access to the division), and
the person reads it. The approve policy of the whole application gives no start. An agent in shadow is
started by administrators only. `202` names the outcome: `started`, `queued`, `coalesced`,
`superseded`, `ignored`, `deduplicated`, `waiting` (a paused agent or skill; `reasonCode`
`assistant-agent-paused` or `assistant-skill-paused`, the run starts on the resume) or `deferred`
(inside the caller's transaction). A refused start answers its code with the one status the assistant
gives that code, as every assistant endpoint does: `403 assistant-subject-access-denied` or
`assistant-agent-forbidden`, `409` for `assistant-agent-does-not-work-on`, `assistant-agent-stopped`,
`assistant-agent-configured-off`, `assistant-no-accountable-owner` and
`assistant-trigger-rate-limited` (the agent's runs per subject and day, `NhAssistantTriggerReasons.RateLimited`),
`404` for an unknown agent or subject, `503 assistant-trigger-rolled-back` when the run could not be
queued, and `400 assistant-subject-invalid` for a malformed subject reference. `POST threads/open`
answers the same refusal when it would create the thread. The same rule decides every other run a
person asks for, with the agent's required policy as it is in effect now: a restart
(`POST runs/{runId}/retry` with `mode` `restart`, which also needs the approver role) answers its
refusal like `POST threads/start`; a message to a busy agent thread waits for a run only when its
sender may start the agent, and anyone else gets `409 assistant-conversation-busy` with nothing
waiting for them; and the generated request tool starts a run only for an asker who may start the
agent, with the asker's role for the agent's thread (`assistant-consult-subject-unavailable` without
it, `assistant-agent-forbidden` when the rule refuses). A message to a busy thread therefore never lets
an approver of the whole application who fails the run policy, or a participant of an agent in shadow,
start a run. `GET subjects/{type}/{id}/agents` (`NhAssistantObjectAgentsDto`) lists
the agents that work on a subject, an object or the whole application, and that the caller may use:
per agent `CanStart`, the thread people open, the newest run (`NhAssistantLatestRunDto`: status, time
and the first 300 characters of its result, for a run without a stored answer the summary it ended
with) and the open proposals; the caller needs a role on the subject. A run started by hand never uses
another person's personal MCP connection: only the initiator's own, with their consent.

Place a run's proposal at the object it concerns. Declare which input property of a tool names which
object type with `[NhAiSubject("project")]` (`NhAiSubjectAttribute`) on a property of the input record,
or on the parameter of a positional record, of type `Guid`, `string` or an integer, at most one per
tool. The source generator writes `NhAiToolDescriptor.Subject` (`NhAiToolSubject`: the type and the
JSON argument name) and adds it to the contract hash only when present, so tools without one keep their
hashes; a tool that starts declaring one changes its hash, and its pending approvals no longer execute.
The build fails for an invalid type, including the platform's `nh-` types (`NHAI015`), a property of
another type or one the input contract does not read (`NHAI016`) and more than one declaration
(`NHAI017`). Hand-built and attested catalogs set `Subject` themselves; MCP tools never declare one,
so they always propose in the run's own thread. When an agent run (not a person's turn in the thread
and not a consultation) calls such a tool with an effect and the argument names another object than
the run's own, for example a weekly run for the whole application that proposes to complete a
project, the platform reads the object as the tool binds its input and checks it before the call
runs: the agent works on its type, a per-division agent only in its own division and a run bound to a
tenant only in that tenant; the object exists (`DescribeAsync`); the account the run answers to (its
`AccountableOwnerId`: the agent's own, the responsible person or the technical account) is at least
`Reader`; and the run placed fewer than `NhAssistantLimits.MaxPlacedProposalsPerRun` proposals
(default 20, 0 to 200). The call then runs in the object's scope, authorized against the object and its
division, and always becomes a deferred proposal: it waits in the agent's thread at that object,
created when needed (a run in shadow places it in the shadow thread there), with the object's version,
execution scope, guards and duplicate check, the run that made it (`RunId`) and the thread it came from.
The object's approvers decide it, in its thread, on its page and in their inbox; the run's initiator
does not through the initiator rule, and four-eyes still applies. An approved one executes in the
object's scope with the agent's account, from its own copy of the call, through the governed tool
(never through the MCP re-checks, which concern only `mcp.` tools); the run's own thread keeps the call
as `not-executed` with `assistant-proposal-placed`. A refused placement does not fail the run: the call
ends `not-executed` with `assistant-proposal-not-placed` and a `reason` the model reads
(`NhAssistantPlacementReasons`: `does-not-work-on`, `not-found`, `no-access`, `limit`, and
`no-accountable-owner` when nobody answers for the agent at the object), a rule handler
reads `NhAssistantRuleProposal.PlacementReason`, and the audit records `AgentProposalNotPlaced`; a
placed one answers with the object, `NhAssistantRuleProposal.PlacedAt` names it, and the audit records
`ApprovalRequested` and `AgentProposalPlaced` about the object. Proposal DTOs name the run with
`PlacedBy` (`NhAssistantProposalOriginDto`: its thread, the run and its subject type; the subject's id
and name only for the whole application, which every reader may know). When the run ends, also after a
failure, each approver is notified once for the run about the objects that wait for them (SPM-302: the
colleague reads "4 proposals at 4 projects wait for your decision", an approver of one project only
about that project, and a day later one reminder), and the run's summary counts its
placed proposals per tool and status. The migration `AgentPlacedProposals` adds the column that marks a
placed proposal.

Read what runs did from their results and history (K13, K14). A run keeps its whole answer as Markdown
in `AssistantRun.Result`, at most `NhAssistantLimits.MaxRunResultCharacters` (20,000) characters with
`ResultTruncated` when it was cut: the final answer of each of its turns that completed, separated by a
thematic break, the rules' summary of a rule run included, while `NhAssistantRunSummaries` keeps writing
the small summary the agent's next runs start from. The run's end stores that summary in the same write
that ends the run, so a run that reads as ended always has it: wait for a run's end, never for its
summary after it. Files the run saved stay artifacts. The result is
model output, MCP tool output included: render it only with a safe Markdown renderer, never as HTML.
It lives with the run, so it follows the thread's retention. A run works in the application's default
culture, so a result the rules write once reads in that language for everyone; let rule handlers write
it per culture instead with `NhAssistantRuleResult.Done(culture => ...)`, which the platform asks once
per supported UI culture with that culture current, rationales included. The run then keeps its result
per culture as well (`AssistantRun.ResultsJson`, migration `AgentRunLocalizedResults`), and `GET runs`,
`GET runs/{id}`, `GET admin/agents/{id}/runs`, the agents of an object and the boards return each reader
the result in the request's culture, by the exact culture, its parents or another culture of the same
language, and otherwise in the run's own culture; the thread and the agent's memory keep the run's own
culture. A model's answer stays as the model wrote it. `GET runs` lists for people the runs they
may see (`NhAssistantRunHistoryListDto` of `NhAssistantRunHistoryDto`: agent and its name, subject,
trigger, status, kind, skill, usage, times and the first 300 characters of the result), newest first,
with the filters `agentId`, `subjectType` (`nh-system` for the whole application), `subjectId`,
`status`, `from` and `to` (creation, from inclusive, to exclusive) and `page` and `itemsPerPage` (at most
50); an invalid filter answers `400 assistant-run-filter-invalid` with a field error per invalid
parameter in `errors` (`page`, `itemsPerPage`, `agentId`, `subjectType`, `subjectId`, `status`,
`to`), checked before the agent of the runs view. A run on an object is visible to
whoever may read the object, or was invited to the agent's thread there, as the thread; a run for the
whole application to whoever passes the agent's `ViewPolicy`, by default its required policy, evaluated
like the roles of the whole application (for the rebuilt principal, against the subject with its
division, with division access); shadow work to administrators only, and a thread of another tenant to
nobody. Visibility is the application's resolver's decision, so the history decides per thread after
the query: it considers the 100 agent threads with the most recent matching runs and pages the runs of
the visible ones in the database (`Limited` says older threads were left out; filter by an object or a
period to reach them). `GET runs/{id}` (`NhAssistantRunResultDto`) returns one run the caller may see
with its whole `Result`, its `Summary`, its proposals in its own thread and those it placed at objects
the caller reads (`HiddenProposals` counts the others), its files (`GET
runs/{id}/artifacts/{artifactId}/content`, shielded as in the thread) and whether the caller may open
its conversation; administrators open every run. `GET admin/agents/{id}/runs` is an agent's runs view
for administrators: every run, live and shadow (marked), with the same filters and an exact total. The
migration `AgentRunResults` adds `Result`, `ResultTruncated` and an index of threads by agent.

Count what runs did from the business audit sink alone (run facts). `AgentRunEnded` carries, next to
the run, agent, version, trigger, subject, initiator and skill, the content-free facts an application's
usage report or billing needs: `TriggerKind`, what started the run as one of `NhAssistantTriggerKinds`
(`event`, `schedule`, `webhook`, `manual`, `message` for a person's message that waited for the run,
`request`, `hand-off`, `follow-up` for a decided or executed proposal or an ended hand-off, and `replay`),
recorded on the run when it is created (`AssistantRun.TriggerKind`, migration `RunTriggerKinds`) so
removing or changing its trigger later does not change it; `Shadow`; `SkillVersion`, the version of a
skill defined as data; `TriggerVersion`, the version of a trigger defined as data that started the run
(a trigger candidate's runs are shadow runs of its version); `ModelCallCount`, every governed model call of the run across its turns and
attempts, triage included, which the run counts when the call's budget is reserved, so a failed,
cancelled or retried turn keeps its calls and a run by rules alone stays 0; and `RunStatus` and
`RunFailureCode` apart, while `ResultCode` keeps `status:code`. Count runs, runs without a model call
(`ModelCallCount` 0) and shadow runs per `AgentId` and `SkillId` from these events in an
`INhAssistantBusinessAuditSink` instead of querying the assistant's tables, and match a run's model
calls to usage records by the run id in `NhAiUsageRecord.CorrelationId`, each call named by its
`ModelCallId`. A consultation is no run: its usage stays on `AgentConsulted`.

A tool that needs the agent run it works for reads `NhAiInvocationContext.AgentRunId` (a `Guid`),
which the run engine sets on the agent's invocation scope (`NhAiAgentInvocationScope.AgentRunId`) and
the agent invocation gate copies to every tool call of the run, also when an approved proposal of the
run executes. It is `null` outside a run: in a person's chat, a person's turn in the agent's thread and
a consultation. `RunId` stays the turn's id, which proposal hashes and usage records use, and
`CorrelationId` the run id in `N` format; never parse either to find the run.

An agent's thread of the whole application is never readable more widely than its results: reading it,
its conversation, its proposals and its files needs the read role of the whole application (the read
policy of `UseSystemSubject`) and the agent's view policy, by default its required policy, evaluated as
for its results; an invitation does not open it to anyone else. A start by hand on the whole application
needs the view policy as well, so the thread it leads to can be opened. Its proposals are listed in the
inbox, counted in the subject summary and decided only by people who pass the view policy as well (and
the approve policy and the approval policy's rules); to anyone else such a proposal reads as unknown
(`404 assistant-approval-not-found`). The status of the background operations of its runs and proposal
executions, its notifications and the live updates of its thread follow the same rule. The whole
application lists no members, so its approvers and readers receive its agents' work while a screen of
theirs watches it: the hub method `WatchAgentWork()` joins the connection to the work, per agent and
scope, of every agent whose thread of the whole application the person reads (the read role and the
view policy), also before its first run, at most 200, and answers `NhAssistantLiveWatchDto` with
`RefreshAfterSeconds` (300); renewing the watch leaves what the person may no longer read, and
`UnwatchAgentWork()` stops it. A person bound to a tenant watches nothing, unless `UseSingleTenant`
names that tenant. Watchers receive the same
content-free `AgentWorkChanged` as the people of the thread.

Group agents on boards (K15, K16). Register a group in code with `AddAgentGroup("project-management",
group => group.Named(new() { ["en"] = "Project management", ["nl"] = "Projectbeheer" }, fallbackLanguage:
"en").Describe(...))`: a dash-case id of at most 64 characters, a name of at most 100 characters per
language with one in the fallback language, an optional description of at most 300 characters per
language; the same group registered again, for example from every composition whose agents join it, is
kept. An agent in code joins with `ConfigureAgent(id, run => run.InGroup("project-management"))`, and
startup fails for a group that is not registered or a shadow variant in a group. Administrators put any
agent, in code or created in the administration, in groups or take it out with `PUT
admin/agents/{id}/groups` or `INhAssistantAgentGroups.SetMembershipAsync`; the choice is stored in
`AssistantAgentGroupMember` where it differs from code and wins over code, and a choice that equals code
lets the code decide again. Agents created in the administration join groups this way only. The run
catalog lists the groups as `AgentGroups` and the admin DTO carries `Groups` and `CodeGroups`. `GET
boards` lists the groups whose board shows the caller at least one agent; `GET boards/{group}`
(`NhAssistantBoardDto`) returns per agent what it works on, its whole application in the board's scope
(app-wide `nh-system/global`; with `divisionId` a per-division agent's division), `CanStart` by the rule
of starts by hand, the thread when the caller may read it, the latest run with the beginning of its
result, the open proposals of its thread and those its runs placed at objects, and `IsNew`: the latest
run ended or a proposal was made after the caller's last visit of that board in that scope, and before
the first visit everything shown is new. An agent that works on the whole application shows by its view
policy, another by its required policy without a result or a start; shadow variants and agents without
run settings never show, and a caller bound to a tenant sees no work of the whole application. `POST
boards/{group}/visit` records the visit in `AssistantBoardVisit` (person, group, scope key and time,
nothing else); a board the caller may not open answers `404 assistant-board-not-found`, a malformed
division id `400 assistant-board-scope-invalid`. One board reads the group's choices, the agents, the
threads of at most two subjects, their latest runs, their open proposals and the visit, each in one
query. The run maintenance removes visits older than `NhAssistantLimits.BoardVisitRetention` (default
180 days) and visits of groups the host no longer registers, once an hour; a host that registers no
group removes none. The migration `AgentBoards` adds both tables.

Give approvers an overview of their inbox (SPM-310). Every proposal records the parent its subject's
resolver names when the proposal is made (`ParentSubjectType`, `ParentSubjectId` and
`ParentDisplayName` on `AssistantApproval`, the migration `ProposalParents`), so listing the inbox
never asks a resolver per proposal; `NhAssistantProposalDto.Parent` (`NhAssistantProposalParentDto`)
carries it with the type's name and link. `GET approvals/inbox` filters by `subjectType`, `agentId`,
`divisionId` and `runId`; `grouped=true` groups the proposals by their parent, or by their own subject
without one, so a parent's own proposals and those of its children form one group. Groups come by their
first expiry, a group's own subject first and then its other subjects by their first expiry, the
proposals of one run together and then by expiry, every tie broken on both providers in the same way;
each group, subject and run is contiguous across pages. The page is still paged by proposals, and
`Groups` (`NhAssistantInboxGroupDto` with `NhAssistantInboxSubjectGroupDto`) gives per group and
subject on the page its `Count` there, its `Total` in the inbox and whether it continues from the
page before or on the page after. Without `grouped` the inbox keeps its expiry order and `Groups` is
`null`. The inbox also marks what is new like a board: `NewApprovalIds` names the proposals on the
page made after the caller's `LastVisitedAt` in the inbox's `ScopeKey` (`global`, or the division of
`divisionId`), every one before the first visit, and `POST approvals/inbox/visit` (optional
`divisionId` of a division the caller works in, otherwise `404 assistant-subject-not-found`) records
`NhAssistantInboxVisitDto`. `status=` lists proposals on their way with the same visibility (SPM-327):
`pending` before its expiry (the default), `awaiting-application` until its report falls due, by report
deadline, and `execution-unknown` until an approver resolves it, by decision
(`NhAssistantInboxStatuses`); any other value is `400 assistant-message-invalid` and the page says its
`Status`. For digests, `INhAssistantProposalCounts.CountAsync(person, statuses)` counts per status with
the inbox's own query and visibility for an `NhAssistantDecidingPerson` the application authenticated,
ids and counts only; an unknown status throws `ArgumentException`. The visits live with the board visits under the platform's reserved id
`nh-inbox`; `AddAgentGroup` therefore refuses every group id that starts with `nh-`, and the run
maintenance removes inbox visits by age only.

Start agents from application events with `AddTrigger<TEvent>` and
`INhAssistantAgentTriggers.PublishAsync`, and set `Dedupe` to the event id or to the
changed row and its version, so an at-least-once delivery starts one run: an event is known in its
agent's thread by its input once it waited, joined a pending run or was claimed by one, so a
redelivery after that run took it is `deduplicated` with that run, even though the run records the
key of the event that started it only. Publish
either inside the domain transaction or after the commit. Inside the transaction of
the context that holds the background operations, `PublishAsync` and `StartAsync`
write nothing to the assistant's tables: each trigger becomes a background operation
in that transaction (`NhAssistantAgentRunOperationTypes.FireTrigger`) that fires once
the transaction committed, so a rollback leaves no thread, run or input behind, a
committed change always reaches its agent, and the receipt reads `deferred`
(`NhAssistantTriggerOutcomes.Deferred`) without a run or thread; look the thread up
after the commit when you need it. After the commit, for example from an outbox
consumer, a trigger fires at once and the receipt names the run. Deferred triggers
of one agent and subject fire in publishing order, and a retried operation fires
once. Every receipt of a fire that wrote an input, or writes one after the commit, names it
(`NhAssistantTriggerReceipt.InputId`): started, queued, superseded, coalesced and waiting fires and a
deferred fire before the commit; a rejected, ignored or deduplicated fire names none. To find what
became of an event, call `INhAssistantAgentRunLookup.FindByEventAsync(agentId, triggerId, eventKey)`
with the trigger's dedupe key, or `FindByInputAsync(inputId)`: the `NhAssistantTriggerEventRun` names
the input, its thread and subject, whether it still waits, and the run that consumed it, or while it
waits the run the event started. The lookup goes through the queued inputs, because a run that
coalesced several events records only the first event's key, and a redelivered event finds its first
delivery. It is a server API for the application: it checks no person's access, so show its result
only to people the application lets see the agent's work, and pass the caller's tenant to leave other
tenants' work out (the whole application's work only for a tenant that takes part, `UseSingleTenant`).
To read what a run did, call `INhAssistantAgentRunLookup.FindRunAsync(runId, tenantId)` with the run a
receipt or the event lookup named: the `NhAssistantAgentRunFacts` give its agent, subject and thread,
whether it is shadow work, its trigger with the trigger's kind, version and candidate flag, how it worked
(`Kind`), its status, failure class and code, attempt, proposal count, summary, and its result with
`ResultTruncated` in the caller's UI culture when the rules wrote it per culture, and when it was
created, started and ended; `null` for an unknown run and another tenant's. It applies the same tenant
rule and no person check, as `GET runs/{id}` would for a person, so the application need not read the
assistant's tables. The result is model output: render it only with a safe Markdown renderer, never as
HTML.
The assistant's own endpoints, schedules, webhooks and follow-ups always fire
at once. Maintenance queues a run again whose operation never committed. Choose
`NhAssistantTriggerConflict` for events that arrive while a run is active:
coalesce, queue, supersede or ignore. Bound agents with `Budget`, `Retry`,
`Enabled`, and `Shadow()` on the agent or a skill to start it in shadow for evaluation on real
events: its work goes to the agent's shadow thread of the subject, which administrators alone see,
where nobody is notified and nothing proposed executes. Shadow is part of the effect decision: every
effect a run calls in a shadow thread needs an approval and becomes a shadow proposal before the tool
runs, also a tool the effect policy runs without approval, an agent request (`assistant.request.*`)
and an imported MCP tool, and a tool that issues or authorizes its own approval is refused there.
Reads still run; the consult tool, which reads, refuses a shadow run itself (below). `Retry` (0 to 10, 3 by default, in code
or in the run settings) alone decides how often a run is retried after a transient failure: the
run's background operation allows the most an agent can configure, and the run fails once its
failed attempts exceed the agent's value, so `Retry(0)` never retries and `Retry(5)` gives six
attempts. `Retry` counts attempts that failed only:
a run whose thread a person's turn holds, or whose agent already has `maxConcurrentRuns` runs
going, gives way without spending a retry and tries again 15 or 30 seconds later on the
application's clock, twice as long after every attempt that gave way, at most five minutes. Call
`AddAssistantAgentRuns()` inside `WithBackgroundOperations`.

Give agent runs capacity of their own when other background work can fill the global limit.
`AddAssistantAgentRuns(runs => runs.Queue("agent-runs").MaxConcurrency(4))` places every operation of
the assistant (runs, proposal executions, revalidations, deferred trigger fires and replays) on that
queue, which the process's Hangfire server serves on its own, and gives the queue that concurrency
limit; `MaxConcurrency` needs a queue of their own and refuses to change a limit the queue already has.
Reserve slots of the global limit for the queue with `WithReservedQueueConcurrency("agent-runs", 2)`:
other work then never takes them, and the queue limit keeps agent work from taking every shared slot
in turn. Operations queued before the queue changed keep their old queue, so keep serving it until
they ended. The agents' own `maxConcurrentRuns` still applies per agent.

Other sources start agents too. `AddScheduledTrigger` runs a Hangfire recurring job on
a cron schedule and fires the trigger for each subject its `Subjects` query returns,
at most `MaxSubjectsPerTick`; a repeated job in the same minute starts nothing twice.
Hangfire fires the cron on real time, but the minute a tick belongs to comes from the
application's clock (a registered `TimeProvider`, otherwise the system clock): a test moves
its clock to the next minute to tick again instead of waiting, and with a clock that stands
still every later tick counts as the same minute and starts nothing.
A host that runs the schedules registers the recurring jobs of the schedules in code when it starts and
keeps those of data schedules in line with its snapshot, which removes the jobs of schedules its snapshot
does not know. Set `NewHeap:AI:Assistant:RunSchedules` (`NhAssistantOptions.RunSchedules`, true by
default) to `false` on every other host that shares the database, such as an API host next to the worker
that runs Hangfire, or a host without a Hangfire server: it registers, changes and removes no recurring
job and needs no `IRecurringJobManager`, while its triggers, runs, administration and the following of
data changes work as usual and a tick that reaches it still fires. Keep it `true` on the hosts whose
Hangfire servers run the schedules, and give them the agents and schedules the other hosts have.
`AddWebhookTrigger` exposes `POST {prefix}/triggers/{id}` to an external system without
a user: the caller signs `{timestamp}.{deliveryId}.{body}` with HMAC-SHA256 and a shared
secret of at least 32 characters read for every call, calls more than five minutes off
are refused, and a delivery id starts at most one run. Treat the body as untrusted and
take only identifiers and known values from it. A follow-up run after a decided or
executed proposal is opt-in per approval policy (`StartFollowUpRun`) and waits 30 seconds,
so decisions made together reach one run. Debounces, this one included, run on the
application's clock (a registered `TimeProvider`, otherwise the system clock): a test moves
its clock past them instead of waiting. The follow-up continues in the skill of
the run that made the proposal (`skillChosenBy` `follow-up`) while the agent declares that skill:
a paused skill lets it wait and a stopped one refuses it. It also keeps that run's initiator, or
none for trigger work and for a proposal the run placed at another object: the person who decided
stays in the approval and its audit, never becomes the follow-up's initiator and lends it no rights,
so four eyes keeps the requester out of what the follow-up proposes.

Filter events that do not concern the agent with `When` on `AddTrigger` and
`AddWebhookTrigger`: it runs before every other lambda of the trigger, and a refused event
starts nothing, is not audited and reads `ignored` with reason `filtered`. Keep it cheap and
free of I/O.

Control an agent with `INhAssistantAgentControl` from the application's own code, with
`POST admin/agents/{id}/state` or from the administration, each with an optional reason of
at most 500 characters. **Pause** an agent around an import or a migration: no new runs
start, events, webhooks, manual starts and requests wait per thread (outcome `waiting`,
deduplicated by their dedupe key), schedules skip their ticks, running runs finish, approved
proposals execute and people keep chatting. **Resume** starts one run per thread with
waiting events, which sees them all. **Stop** an agent that works wrongly: its active runs
are cancelled and their pending proposals withdrawn, waiting events end, triggers and chat
are refused with `assistant-agent-stopped`, and approved proposals wait until it leaves the
stopped state. **Switch off** an agent (`SwitchOffAsync`, state `off`) that should not work on
its own for a while, for weeks if needed: its events (data triggers included), schedule ticks
(data schedules included), webhooks, hand-offs and follow-up runs are ignored before any of their
lambdas runs, read `ignored` with reason `agent-off` like a `When` filter, are not audited and
nothing waits; a hand-off to it is refused with `assistant-agent-off`. Running runs finish, open
proposals stay decidable and executable, and people keep chatting with it and starting it by hand
(`POST threads/start`, a message in its thread, a request through another assistant) as their
policies allow. Inputs that waited from an earlier pause end, so switching it on again with
`ResumeAsync` starts no run for anything that happened meanwhile. A skill switches off on its own
(`SwitchOffSkillAsync`): triggers that name it are ignored with `skill-off`, rules that route to it
end their run with `assistant-skill-off`, and triage and `use_skill` do not offer it. The kill switch
(`SetKillSwitchAsync`, `POST admin/agents/{id}/kill-switch` `{ engaged }`) stops an agent without a
reason when engaged; releasing it never switches on an agent that was off: one that was off when it
stopped goes back off, one that is off stays off, and every other agent resumes, a paused one included.
While an agent is stopped, `NhAssistantAgentState.OffBeforeStop` (`offBeforeStop` on the state and on
`NhAssistantAdminAgentDto`) says whether releasing switches it back off, and the agents
administration shows it on a stopped agent; an explicit `ResumeAsync` switches it on either way.
Skills keep their own states through it. A state change never
changes the agent's definition or version; editing and resetting never change the state, and
`isEnabled` in the definition is ignored. An agent disabled in code with `Enabled(false)` reads
`configured-off` and cannot be switched on at runtime; with `NewHeap:AI:Assistant:Enabled` off,
triggers, schedules, runs and execution queuing stop as well. A stopped, switched-off or
code-disabled agent's triggers evaluate no lambda.

Deploy an agent or a skill that should wait for an administrator with `StartsOff()` on
`ConfigureAgent` or on `Skill(...)`, not with `Enabled(false)`: it is off the first time the
application sees it, an administrator switches it on at runtime, and from then on its stored state
wins, so a new host start or a later deploy never switches it off again. The skills in code are
recorded at the first start on this version; a skill that was deployed before and gets `StartsOff()`
in that same deploy therefore starts off once. An agent created in the administration
(`startsOff` on `POST admin/agents` and `NhAssistantAgentRequest`) and a new data skill (`startsOff`
on `NhAssistantDataSkillRequest`) start off only when asked to; the administration's create forms
offer it as "Switch on now", on by default. An agent created at runtime starts in shadow only when
asked to (`StartsInShadow` on `NhAssistantAgentRequest`, `startsInShadow` on `POST admin/agents`): it
is stored as the agent's shadow override from its first row and audited as `AdminAgentShadowChanged`
(`shadow`), so it takes effect as soon as the agent has run settings, also when they and its triggers
are saved right after in the same request, and no event reaches it live first. Put it live with
`SetShadowAsync`; the agent editor offers "Start in shadow" for a new agent that runs, off by default. The run configuration reports `startsOff` for an agent
and its skills in code.

The shadow mode and the technical account are runtime overrides of the code, changed the same way
(`INhAssistantAgentControl`, `POST admin/agents/{id}/shadow`, `POST
admin/agents/{id}/skills/{skillId}/shadow`, `POST admin/agents/{id}/service-account` or the
administration) and audited without their reason. `SetShadowAsync(agentId, true | false | null)`
puts an agent in shadow, live or back to its mode in code (mode `shadow`, `live` or `code`);
`SetSkillShadowAsync` does so for one skill, which works in shadow whenever its agent does. Shadow
work goes to the agent's shadow thread of each subject: an agent has at most one live and one
shadow thread per subject, with separate memory. A trigger that names a skill in shadow fires
there, a live run that rules or triage route to one ends with `assistant-moved-to-shadow` and a run
in the shadow thread takes its events, and `use_skill` in a live thread does not offer it. Runs
that started finish in the mode they started in; a run still queued when the mode changes starts in
the new one, and waiting events move along when they resume. The live thread, its open proposals
and people's chat in it stay as they are, but only administrators start an agent in shadow by
hand, and consultations and requests are refused. Shadow work never reaches a live agent, as a
hand-off to a live agent is refused: a run in a shadow thread that calls a consult tool is refused with
`assistant-consult-agent-unavailable`, and its call of a request tool, an effect, becomes a shadow
proposal that never executes before the tool runs (the request tool refuses a shadow run itself as
well). The other agent records no consultation, gets no thread, input or run, leaves no proposal for
the subject's approvers and notifies nobody. A shadow variant always works in shadow
(`assistant-shadow-variant`). `SetServiceAccountAsync(agentId, actorId | null)` lets another
technical account answer for an agent that answers to one in code (`ServiceAccount(actorId)`
stays the default; `assistant-service-account-not-used` otherwise). The account must exist and be
allowed to sign in according to `INhAiPrincipalFactory` (`assistant-service-account-not-found`); with
`UsePrincipalsFromAccessSource` the access source must return a fresh context for it.
New runs and the agent's threads answer to it; runs that started and the proposals they made keep
the account they were made with. The account lends its rights to gateway reads: choose one with
no more rights than the agent needs. Only a person who may act for the account binds it, here, in
an agent's run settings and in a package import: the account is that person, the agent's account in
code, the person may act as any user (the platform permission `nh.platform.auth.impersonate.allowed`),
or the person holds the account's rights to the agent's tools as well, that is every authorization
policy of the agent's tools that the account passes, evaluated against the agent without a division,
so a right the account holds only in a division is not compared. Otherwise the change is refused
with `403 assistant-service-account-not-permitted`
and nothing changes; saving an account that is already bound again checks only that it still exists.
The person is the request's user when they sent the request, otherwise the principal
`INhAiPrincipalFactory` rebuilds; application code that binds with a technical actor id, which the
factory knows as no person, decides on its own.

Add skills and triggers to an agent configured in code as data, without a deploy, when an
application manages them in screens or imports them from packages. Code registers the building
blocks under dash-case names joined by dots: `AddTriggerEvent<TEvent>(name, ...)` for an event a
data trigger can start a skill on (its `Subject`, `Message`, `Initiator`, `Dedupe`, `Payload` and
`When` are what every trigger on it receives; one published event serves code and data triggers
alike), `AddSubjectSet(name, description, subjects)` for the subjects a data schedule starts a
skill for, and `AddRuleHandler<THandler>(name, description)` for rules a data skill works by.
Several skills may name the same rule handler: it reads the skill it runs for from
`NhAssistantRuleContext.SkillId`, `SkillVersion` (the data version, `null` for a skill in code) and
`SkillCandidate` (`true` in the run of a candidate version); all three are `null` for the agent's own
rules outside a skill. Approval policies, model profiles and subject types are named by their ids as in code.
`INhAssistantAgentConfiguration`, or `PUT admin/agents/{id}/skills/{skillId}` and `PUT
admin/agents/{id}/triggers/{triggerId}`, stores a data skill (description, instructions with their
hash, tools within the agent's own selectors, profile, rule handler, ceiling, budget and approval
policy) and a data trigger (an event or a schedule with cron, time zone, subject set and message,
the skill, conflict and debounce). Saving checks the data as startup checks code and answers with
field errors; a skill or trigger in code cannot change (`assistant-skill-in-code`,
`assistant-trigger-in-code`), and grants never become data. A new skill and a new trigger start in
shadow: put the skill live with `SetSkillShadowAsync` and the trigger with `SetTriggerShadowAsync`
or `POST admin/agents/{id}/triggers/{triggerId}/shadow`. A changed skill is the next version
(`expectedVersion`, otherwise `assistant-version-conflict`) and the change chooses how it takes
effect: `apply: live` puts it live at once; `apply: shadow` makes it the skill's candidate, which
takes every event of the triggers that name the skill as a second run in the agent's shadow thread,
next to the live version's run, until an administrator promotes it
(`PromoteSkillCandidateAsync`, `POST .../candidate/promote`) or discards it
(`DiscardSkillCandidateAsync`, `POST .../candidate/discard`). Routing by rules or triage reaches
the live version only; a candidate lets nothing wait while the agent or skill is paused, and a
queued run of a candidate that was discarded ends with `assistant-skill-version-gone`. Runs record
`skillVersion` and `skillCandidate`; `CompareSkillCandidateAsync` (`GET .../candidate/comparison`)
counts the runs, outcomes, usage and proposals per tool of both versions, what people decided about
the live version's proposals and the events both handled. Versions stay as history
(`GET admin/agents/{id}/skills/{skillId}/versions`). Remove a trigger before the
skill it names (`assistant-skill-in-use`). Every change is audited (`AdminAgentSkillDefinitionChanged`,
`AdminAgentTriggerDefinitionChanged`) and reaches every host: the snapshot of code and data that
every reader uses reloads when a stamp changes, checked before every fire and run start at most
once per second and every half minute in the background, and data schedules get recurring jobs of
their own. Data that no longer fits the code after a deploy, such as a removed event, is left out
and shown as a problem in `GET admin/agents/{id}/configuration` instead of failing startup. The same
response says what an administration's overview of the agent needs without another request: each
trigger in code names the subject type it runs for up front (`NhAssistantCodeTriggerDto.SubjectType`,
`nh-system` for a schedule for the whole application, `null` when only the fire knows it), and an agent
in code reports whom it answers to, whether it delegates from the initiator, its approval policy and
whether it works by rules (`CodeRunSettings`, `NhAssistantCodeRunSettingsDto`), and, read only for the
administration's guardrails, what else its code sets: `SkillsInChat`, `AllowQuestions`, `UsesMemory`,
`TriageProfile`, `RetryCount`, `MaxConcurrentRuns`, `MaxRunsPerSubjectPerDay`, `DailyToolCalls`,
`DailyTokens`, `MaxTokensPerRun`, `MaxConsultationsPerTurn`, `ElevatedReads` and `AllowGatewayReads`,
with the platform's defaults for what it leaves out (SPM-298). `GET admin/tools`
names the object type a tool declares with `[NhAiSubject]` (`SubjectType`), so a screen can say that
a run's proposal of it waits at that object; MCP tools never declare one.

Try a change of a data trigger in shadow before it replaces the live one, as for a skill. Triggers
defined as data have append-only versions: a change is the next version (`expectedVersion` is the
live version, otherwise `assistant-version-conflict`), and `Apply` of `NhAssistantDataTriggerRequest`
chooses how it takes effect. `live` (default) replaces the live version at once, in the mode the
trigger has. `shadow` makes it the trigger's candidate next to the live version, which keeps working
unchanged: the candidate fires on every event and schedule its own definition selects, for the
trigger's own agent and always into the agent's shadow thread of the subject, with its own filters,
subject, dedupe, conflict, debounce and daily limit, so it never refuses, queues or deduplicates the
live version's work, nor the live version its work; an event may start either, both or neither. A
publisher's receipts are the live triggers'; a candidate adds none. A deferred fire keeps it, a data
schedule's candidate gets a recurring job of its own, a candidate lets nothing wait while the agent or
skill is paused, and its runs use the live version of the skill it names. A new trigger has no live
version to compare with (`apply: invalid`), and a later change discards an earlier candidate. The
administrator promotes it (`PromoteTriggerCandidateAsync`, `POST
admin/agents/{id}/triggers/{triggerId}/candidate/promote` with its `version`), which makes it the live
version in the trigger's mode, or discards it (`DiscardTriggerCandidateAsync`, `POST
.../candidate/discard`); another version answers `assistant-version-conflict` and none
`404 assistant-trigger-candidate-not-found`. Runs record `TriggerVersion` and `TriggerCandidate`
(`NhAssistantRunDto`); `CompareTriggerCandidateAsync` (`GET .../candidate/comparison`,
`NhAssistantTriggerComparisonDto`) counts the runs, outcomes, usage and proposals per tool of both
versions since the candidate was saved, what people decided about the live version's proposals, and
the events both, only the live version or only the candidate started a run for. Every version stays
as history with its status (`live`, `candidate`, `superseded`, `discarded`), definition, author and
reason (`GetTriggerVersionsAsync`, `GET .../triggers/{triggerId}/versions`,
`NhAssistantDataTriggerVersionDto`), also after the trigger is removed. `SetTriggerShadowAsync`
changes the live version's mode only. A candidate that no longer fits the code is a problem of kind
`trigger-candidate` and fires no more, while the live version keeps working. Triggers saved before
keep their version as the live one (migration `TriggerVersions`). Every saved, promoted and discarded
version is audited as `AdminAgentTriggerDefinitionChanged`. The administration's run configuration
shows a trigger's candidate with what it changes, the comparison, promote, discard and the history,
and the trigger form asks how a change of a saved trigger takes effect.

Keep instructions that the application owns, such as texts it ships with its code, reviews in its own
process or keeps in its own store, in the application and let a data skill or an agent created in the
administration name them instead of copying them into the administration. Implement
`INhAiTextAssetSource` (`GetAsync(id, version)` returns an `NhAiTextAsset` created with
`NhAiTextAssetFactory`, `null` when there is none; `ListAsync` returns the manifests administrators may
choose, empty to let them type the id and version) and register it with `UseTextAssetSource<TSource>()`;
the run catalog then reports `instructionAssets`. A skill version (`InstructionsAsset` of
`NhAssistantDataSkillRequest`, `PUT admin/agents/{id}/skills/{skillId}`) and an agent created in the
administration (`InstructionsAsset` of `NhAssistantAgentRequest` and `POST`/`PUT admin/agents`) store
an `NhAssistantInstructionAssetReference`: the asset's id, its exact version and the SHA-256 of its
content, never the text; inline instructions next to it are refused (`instructions: not-allowed`) and
an agent in code keeps its instructions in code (`instructionsAsset: not-allowed`). A save asks the
source and refuses a reference without a source (`instructionsAsset: not-allowed`), a malformed one or
an asset that is not trusted system instructions of the application, is blank or longer than inline
instructions may be (`invalid`), a missing asset (`not-found`) and another hash
(`instructionsAsset.hash: invalid`); a source that throws answers `503
assistant-instructions-asset-unavailable` and nothing is saved. `GET admin/instruction-assets` lists
the trusted system instructions the source offers, without content, and `GET
admin/instruction-assets/{id}/versions/{version}` reads one read-only with the hash a reference stores;
both are behind the administration policy. Every load of the run configuration asks the source again
for every asset the data names and compares the hash; the verified text is kept per asset id and
version between loads, and an agent's read resolves a reference it has not resolved yet. A missing
asset, another hash, an asset that cannot be instructions, a source that is no longer registered or
one that throws for a text never verified are the problems `asset-missing`, `asset-changed`,
`asset-invalid`, `no-asset-source` and `asset-unavailable`: the skill version or candidate is out of
effect, and the agent is not runnable, its run settings are out of effect with the problem of kind
`agent` and it starts no model turn (`assistant-agent-instructions-unavailable` for a message, a
decision that continues its turn, a run and a consultation), while its approved proposals still
execute and the administration shows the problem (`InstructionsProblem` of
`NhAssistantAdminAgentDto`). A source that fails keeps a text it verified before in effect and is asked
again after half a minute. A new version of the asset never takes effect on its own: an administrator
saves it as the skill's next version or the agent's next version, and the history keeps which version
each one used. `INhAssistantInstructionAssetUsage.ListAsync(assetId)` answers which data skills and
agents created in the administration name an asset (`NhAssistantInstructionAssetUsageDto`): every skill
version with its agent, skill id, version, status (`live`, `candidate`, or `superseded` and `discarded`
as history) and the asset version and hash it names, and every agent with its version and the asset
version and hash, ordered ordinal; an id that is not lowercase dash-case of at most 128 characters fails
with `assetId: invalid`. It only reads, so an application that publishes a new version finds what to
save as each one's next version; nothing follows it by itself (SPM-331). To put a published version on
trial, call `ProposeCandidatesAsync(assetId, assetVersion, actorId, reason)` (SPM-351): every live data
skill that names an older version gets a candidate in shadow through its own save (apply `shadow` on its
live version), with the live version's other fields, the new version and the hash the application's
source gives, and the reason (by default `asset <id>@<version>`); an earlier candidate is discarded as
every new version discards one. `NhAssistantInstructionAssetCandidatesDto` reports per skill
(`NhAssistantInstructionAssetCandidateDto`) `proposed` with the candidate's version, `already-proposed`
when its candidate names that version and hash already, `locked` (`assistant-package-locked` with the
save's `instructions: locked`) when an agent package locks the skill's instructions and `refused` with
the save's code and field errors; one skill never stops the others. Agents created in the administration
that name an older version are listed only: an agent has no candidate, so an administrator saves it. It
fails, saving nothing, with `assetId: invalid`, `assetVersion: invalid`, `reason: too-long`,
`assistant-instructions-asset-not-found` without a source or that version,
`assistant-instructions-asset-unavailable` when the source fails and `instructionsAsset: invalid` for an
asset that cannot be instructions. The candidates are audited as their saves. The audit names the asset as
`InstructionsVersion` (`<asset-id>@<version>`) with
`InstructionsHash` on `AdminAgentSkillDefinitionChanged`, `AdminAgentCreated` and
`AdminAgentUpdated`, and `AgentRunEnded` names the skill and agent versions that pin it; no text
reaches the audit, logs, problems or run facts. The administration's skill form and agent editor choose
between "Written here" and "The application's text" (SPM-320).

Keep an agent from starting itself again with a change it made, with a named filter on the trigger
event that a data trigger selects. `When` on the event concerns every trigger on it and knows no
agent; register a filter per concern instead with `Filter(name, description, (evt, ctx) => ...)`
on `NhAssistantTriggerEventBuilder` (lowercase dash-case name of at most 64 characters, unique per
event; a description of at most 300 characters that the administration shows). The run catalog lists
each event's filters (`NhAssistantRunBlockDto.Filters`), and a data trigger stores the names it
applies (`Filters` of `NhAssistantDataTriggerRequest` and `NhAssistantDataTriggerDto`, at most
`MaxTriggerFilters`; only an event trigger takes them). Every selected filter must accept the event.
The order is fixed: the agent's and the skill's state first (off ignores, stopped rejects), then the
event's `When`, then its `Subject` function, then the selected filters, and only then the message,
dedupe and payload functions. A refusing filter ignores the event with reason `filtered`, without
an audit, exactly like `When`. The filter receives `NhAssistantTriggerFilterContext`: `AgentId`,
`SkillId`, `TriggerId`, `Subject` (what the event's subject function returned, also for an event whose
type is known only when it fires) and `AgentActorId`, the actor the agent acts as in its runs. Stamp
the cause on the event where the agent's tool makes the change: inside the tool handler,
`NhAiInvocationContext.ActorId` (with `ActorKind` `Agent`) is that same actor, and
`NhAiAgentInvocationScopes.Current` is the agent's invocation scope with `AgentActorId` and the subject
the agent works in (`Subject.Subject`); stamp `NhAiInvocationContext.AgentRunId` as well when the
application traces a change back to the run that made it. Skip the event only when the event's actor
and subject both equal the context's: the agent then does not start itself again on the subject it
changed, while its
change made while it worked on another subject (a task's change on its project, say) and every
person's change still start it. Keep filters cheap and free of I/O like `When`. Saving a filter the
event does not register fails with `assistant-trigger-filter-not-found` and the field error
`filters: not-found`; changing the filters is the trigger's next version, audited like every
change. A stored filter that a later deploy removed fails closed: the trigger stays registered,
ignores every event with reason `filter-missing` and logs a warning, and the run configuration shows
it out of effect with the problem `unknown-filter` until an administrator clears the filter.

An agent created in the administration gets runs through run settings as data:
`INhAssistantAgentConfiguration.SaveRunSettingsAsync` or `PUT admin/agents/{id}/run-settings` sets
whom it answers to (`agent`, `subject-responsible`, or `service-account` with an account that exists
and may sign in), its approval policy, budget, run ceiling, retries, gateway reads, triage profile and
rule handler by name. Grants never become data: in headless runs such an agent acts with the rights
of the person or technical account it answers to, within its tool selectors. A tool is allowed when
that account, rebuilt with `INhAiPrincipalFactory` for every decision (with
`UsePrincipalsFromAccessSource` from the application's `INhAiAccessSource`, while its context is
younger than `MaxAge`), passes the tool's policies against the subject and its capability grants
cover the tool's `RequiredCapabilities`; the call,
proposal and audit record `NhAiGrantSource.Accountable`, reads are
shielded from thread readers without the tool's policies (or refused with `ElevatedReads` `deny`),
and an agent that answers to itself has no rights to lend. A person's turn in the thread and a
consultation keep that person's own rights, and the consult and request tools never run with the
account's rights: such an agent's run consults and requests only when it delegates from its
initiator, otherwise it is not offered them. An agent in code opts in with
`ActWithAccountableRights()`; startup refuses it for an agent that answers to itself. Once it has run
settings, the agent gets data skills and data triggers like an agent in code; its threads follow a
change of whom it answers to, and its run settings stay while data skills or triggers belong to it
(`assistant-run-settings-in-use`). An agent in code cannot get run settings as data
(`assistant-run-settings-in-code`). Hosts need `AddAssistantAgentRuns`, as for agents in code.

The initiator a turn delegates from and the account an agent answers to lend their rights for a tool
only as a whole: they pass the tool's policies and their capability grants (`AddCapabilityGrant`)
cover the tool's `RequiredCapabilities`, compared by name as the invoker compares them. One that
misses either lends nothing and the next source decides, in the order initiator, account, the
agent's grant; so an agent in code with a grant for the tool still uses it, while an agent created
in the administration, which has no grants, is neither offered the tool nor allowed it. When no
source fits and one of them failed only on a capability, the call is refused with
`ai-tool-capability-denied` instead of `ai-tool-agent-not-granted`; the tool call, the assistant's
`ToolInvoked` audit and the model read that code. A capability on a tool names the right the tool
itself needs, such as reading logs or managing projects: register it with the policy of that right,
the way the tool's own policies name it. Who may use the assistant at all is its `AccessPolicy`, an
agent's `RequiredPolicy` and, for MCP clients, the MCP endpoint's policy, never a capability on a
tool. A tool that only makes sense for a person is not exposed to agents: leave
`NhAiToolExposure.Agent` out of its `Exposure` instead of tying its capability to a right only people
hold (SPM-296).

An application that imports agents, for example from a package, creates one the way an
administrator does with `INhAssistantAgentConfiguration.CreateAgentAsync(NhAssistantAgentRequest)`
and then gives it run settings, skills and triggers as above; an existing id fails with
`assistant-agent-exists`. Keep agents that only work on their own out of the chat: in code with
`ConfigureAgent(id, run => run.HideFromChat())`, for an agent created in the administration with
`offeredInChat: false` (`OfferedInChat` on the request). Such an agent is left out of the agent picker
and `POST conversations` answers `assistant-agent-not-found` for it; its triggers, runs, subject
threads, consultations and existing conversations keep working. An agent created in the
administration works with the application's chat profile unless it chooses another (`profile` on
`POST`/`PUT admin/agents` and `Profile` on `NhAssistantAgentRequest`, shown as `profile` on
`NhAssistantAdminAgentDto`): only a profile of the run catalog's `Profiles` is accepted, the
registered profiles with chat, function calling and streaming that data skills choose from, so the
administration can never pick an arbitrary or unconfigured model (`profile: not-found` otherwise).
The profile the agent has and the chat profile stay accepted, so an editor that sends the agent back
unchanged is not refused. Omitted on an update keeps it; an agent in code takes its profile from its
code. The run catalog explains its
choices: `ApprovalPolicySummaries` says per policy who approves (the initiator, a subject role or an
authorization policy), how long a proposal waits, four eyes and whether the run waits, and
`AccountSearch` tells whether `GET admin/accounts?query=` finds accounts by name through
`INhActorSearch`; `GET admin/accounts?ids=` returns the names of stored account ids through
`INhActorDirectory`. Without a search the administration asks for the account's id
(`assistant-account-search-unavailable`). Whether an account may answer for an agent is still
checked when the run settings are saved.

People outside the thread hear about waiting proposals, due reminders,
escalations, unknown outcomes and failed runs through Web Push and every
`INhAssistantAgentNotificationChannel`; `INhAssistantAgentNotificationPolicy` picks the
recipients from the subject's member snapshot, by default its approvers. Whatever it picks, a
person the platform no longer resolves, for whom the principal factory rebuilds nobody, such as a
deleted, blocked or locked-out account, is left out for every subject. The proposals of a
run reach each person once per run: the policy is still asked per object
(`RunEndedWithProposals`) or per proposal (`ProposalExpiring`, `ProposalEscalated`), and the
platform then gives Web Push and the channels one context of `RunProposalsWaiting`,
`RunProposalsExpiring` or `RunProposalsEscalated` per group of recipients with the same objects,
whose `Objects` (`NhAssistantAgentNotificationObject`: subject, display name, conversation and
proposal count) name only what waits for them, the run's own thread first. Its `ConversationId`
and `Subject` are the object a click opens: the run's own thread for the run's initiator,
otherwise the object with the most proposals. The run maintenance bundles the reminders and the
escalations of one pass per run and recipient and still records every reminder per proposal; a
run whose reminders fall due in different passes is reminded per pass. A consultation's
proposals arrive the same way without a run, an inline wait as one object. Channels never
receive the per-object kinds any more, so handle the new kinds in a custom channel. For one
object the texts read as before; for several the title is "Assistant" and the text counts the
proposals and the objects, named by the subject type's plural (`NamedPlural`) in the recipient's
language, or "objects". The in-app message groups per run (`assistant-agent:run:{runId}`), and
the Web Push payload carries `runId` and a tag per run. Triggers,
ended runs, executions, resolutions, pauses, stops and resumes are content-free business
audit events; the reason of a state change stays with the state.

Brief the model about its subject with `AddSubjectBriefing<TProvider>()`, one
`INhAssistantSubjectBriefingProvider` per subject type. Every model turn about the subject reads
the briefing after its instructions, as data: headless runs (with the run's events as
`Inputs`), people's turns in the thread and consultations; rule handlers read data themselves and
get none. Return a few `NhAssistantBriefingSection`s with facts and their dates, most important
first, from the same domain checks the agent's rules use, so rules signal and the model decides;
a rule run that hands over gives the model its reason as the briefing's first section, with what
became of each proposal the rules asked for in the run (tool, object, outcome: recorded, superseding
others, placed, already open, recently rejected, not needed with its kind, refused or not placed) and
the recent rejections and invalidations of the thread they were given (tool, date, kind), at most 20
and 10 lines and within the briefing's bound; people's feedback stays in the continuity block. Briefings
are bounded by `MaxBriefingCharacters` (8,000, at most 32,000) at section boundaries, count
against the turn's history, and a provider that throws or takes longer than `BriefingTimeout`
leaves the turn without one. Briefings reach the model provider: include only what every reader of
the thread may see and what the agent's data classification allows.

Retrieved context complements the briefing: the briefing is what the application decides matters
about the subject, retrieved context is what matches the current inputs. An agent with
`UseRetrievedContext` (see `nh-ai-context-retrieval`) retrieves in a headless run with the
messages of its inputs and the subject's name, as the agent: context sources and their policy see
the agent actor, its accountable person, the run and the subject's execution scope, and with the
ASP.NET integration the subject's division where the host maps an active division, exactly as the
agent's tool calls do. `INhAiAgentInvocationContextFactory` creates that context; replace it only
to add execution scopes of your own. A person's message in the thread retrieves as that person, but
within the same subject and division scopes, not the active division of the person's request. The same rule as for briefings applies: what a run retrieves
may reach every reader of the thread through the model's text.

Let a headless run read application data through the API bridge gateway with
`AllowGatewayReads()` and tool selectors that select the gateway's tools (see `nh-ai-api-bridge`
and `AddNewHeapPlatformAIDelegatedReads`). A run that a person started reads with that person's
own rights, so asking an agent never lends someone else's; a run from a trigger reads with its
accountable person or service account. A turn that answers the message of someone other than that
reader, such as a message typed while the run worked, makes no gateway reads. Startup fails for an agent that answers to itself, without
an issuer or without a gateway. Consultations and turns inside a person's request keep reading with
that person's own rights. Each read is recorded with grant source `DelegatedRead` and the person
whose rights it used; only that person sees its result, whatever `ElevatedReads` says, and the run
counts as one that used restricted results, so a hand-off carries no summary of it. In a trigger
run the model may still restate what it read: keep what a subject's readers may see in line with
what its accountable person may read.

Bound the cost of one run with `MaxTokensPerRun(tokens)`: input and output tokens of every model
call of the run, checked before each next model call. A run at its ceiling fails with failure
class `budget` and `assistant-run-token-limit`, keeps its proposals and gets a new allowance when
an approver resumes it; a consultation uses the same ceiling for its one turn. There is no ceiling
by default; set one for every agent that runs on events.

Give one agent a broad role with skills instead of many tools on every call: `Skill(id, skill =>
...)` declares a part of the role with its own `Description`, rules (`UseRuleHandler`), model
profile (`UseProfile`), `Instructions` after the agent's, `Tools` within the agent's selectors,
`MaxTokensPerRun`, daily `Budget` and `UseApprovalPolicy`; it shares the agent's identity, grants,
thread, memory and briefing. A skill may work by rules first and still use a model: when its rules
return `HandOver(reason)`, the run continues in the same skill, with its tools, instructions,
profile (or else the agent's), ceiling, budget and state, and the reason in the briefing. A run
works in one skill, chosen in this order: the trigger (`.Skill(id)`), the agent's rules
(`NhAssistantRuleResult.UseSkill(id, reason)`), model triage
(`Triage(triage => triage.UseProfile(...))`, one tool-free call that may also end the run with
nothing to do) or none. The model sees only the skill's tools and can switch with `use_skill` at
most twice; people's turns start without a skill and load one the same way. Pause, stop and resume
a single skill with `PauseSkillAsync`, `StopSkillAsync` and `ResumeSkillAsync`; the agent's state
covers all its skills. Start with a trigger per skill and add triage only for events rules cannot
route; keep separate roles separate agents.

People's chats with an agent use its own tools only, unless the agent opts in with
`SkillsInChat()` (SPM-333): a chat turn then starts without a skill, `use_skill` lists the
agent's active skills that work live, and the model loads one when the question needs it, at most
twice per turn. There is no triage in a chat, and none of a run's routing applies. Once loaded, the
skill's instructions, `Tools`, profile, classification, file limits and its own `MaxTokensPerRun`
apply as in a run, always within the rights of the person whose turn it is; the skill's daily
`Budget` counts runs only. The agent's message of the turn records the skill
(`NhAssistantMessageDto.SkillId`, the skill it worked in at its end), so the application can tell
which skill answered; the next turn starts without a skill again. Turn it on for a role agent that
people also chat with, such as a planner whose skills each fit a kind of question; leave it off for
an agent whose skills only make sense on events.

Give an agent that only works in people's chats its chat settings with `ConfigureAgent` and
`ChatOnly()` (SPM-343), not with a `WorksOn` that hides it or with run settings it never uses. It
keeps what a chat applies: `Skill` with `SkillsInChat`, `AllowQuestions`, `MaxConsultationsPerTurn`
for the consultations it starts, `MaxTokensPerRun`, `AcceptsFiles`, `DataClassification`,
`UseApprovalPolicy` for its proposals in a chat and `Metadata`. It works on no subject
(`NhAssistantAgentRunOptions.ChatOnly`, `WorksOn` covers nothing): no object's page and no subject's
list of agents shows it, `POST threads/start`, `POST threads/open`, run now,
`INhAssistantAgentTriggers.StartAsync` and a hand-off are refused with
`assistant-agent-does-not-work-on`, as are a data trigger, the agent's shadow switch and a board;
nothing consults it, a replay never runs into or from it, run maintenance leaves it out and it needs no
background operations. The host refuses to start when the agent also calls a builder method that only
applies to runs on subjects (`WorksOn`, `WorksOnAllObjects`, `WorksOnApplication`, `PerDivision`,
`RunPolicy`, `ViewPolicy`, `InGroup`, `AcceptHandOffs`, `AllowConsultation`, `ConsultableAsData`,
`Shadow`, `ShadowOf`, `HideFromChat`, `StartsOff`, `Enabled`, `Accountability`, `ServiceAccount`,
`Grant`, `DelegateFromInitiator`, `ActWithAccountableRights`, `ElevatedReads`, `AllowGatewayReads`,
`Retry`, `Budget`, `Triage`, `Memory` or `UseRuleHandler`), and when a trigger starts it, another
agent is its shadow variant or accepts hand-offs from it, or `UseHandOffsAsData` or
`UseShadowVariantsAsData` names it. Its skills may still be data, and they always work live: a skill
in code that starts in shadow (`Skill(...).Shadow()`) refuses host start, a skill defined as data works
live at once, and the skill's shadow switch is refused with `assistant-agent-does-not-work-on`; the
agent's and its skills' shadow states are `Locked`. The administration shows it as
chat-only (`ChatOnly` of `NhAssistantAdminAgentDto`, `NhAssistantCodeRunSettingsDto` and
`NhAssistantAgentInEffectDto`).

Classify the data an agent sends to its model with `DataClassification(...)` on `ConfigureAgent`
and, for a skill that reads more sensitive data than the rest of its agent, on `Skill(...)`. The
most specific one wins: the skill's, then the agent's, then `NhAiDataClassification.Internal`. It
applies to every model call of the agent: its runs, people's turns in its threads, its
consultations and people's plain chat conversations with it, and the agent's own one to triage; it
also classifies the skill's instructions and the run's hand-off calls. The model profile of each
call must permit it (`PermitDataClassifications`): otherwise its fallbacks are tried, and when none
permits it the turn fails at once with `assistant-classification-not-permitted` (failure class
`policy`), which a run does not retry, so give such an agent or skill a profile, or a profile with a
fallback (`WithFallbackProfiles`), that permits its classification (`UseProfile`). A data skill
stores it as `dataClassification` (`public`, `internal`, `confidential` or `restricted`, see
`NhAssistantDataClassificationNames`); an unknown name is a field error. An agent created in the
administration chooses it in its definition next to its profile (`dataClassification` on
`NhAssistantAdminAgentInputDto` and `NhAssistantAgentRequest`, shown on `NhAssistantAdminAgentDto`;
omitted keeps it on update and leaves a new agent at `internal`); its plain chats and, with run
settings, its runs use it. A classification that no profile of the chain permits never refuses a save
or a start: saving such a data skill or agent succeeds and its DTO reports `warnings`
(`NhAssistantConfigurationWarnings.ClassificationNotPermitted`, `classification-not-permitted`) for
the administration to show, and an agent or skill in code logs a warning at startup. A consultation's
own `DataClassification(...)` keeps classifying its answers in the asking conversation.
SampleProjectManagement shows it (SPM-307): the document reader's `read-documents` skill is
`Confidential`, so its model calls leave the chat profile, which permits internal data only, for the
fallback `project-assistant-confidential`; an agent created in the administration with that chat
profile and `confidential` answers people's chats through the same fallback, and one with `restricted`
is saved with the warning and refused at the model call.

An agent and each of its skills read no files until they are switched on, so a deploy never opens
a new stream of documents unnoticed: `AcceptsFiles()` on `ConfigureAgent` lets people's messages
and the agent's tools give it files, and `AcceptsFiles()` on `Skill(...)` lets a run in that skill
read them while the agent accepts them too. `AcceptsFiles(limits => limits.MaxFilesPerTurn = 2)`
lowers the application's `NhAssistantLimits` for that agent or skill (`NhAssistantFileLimits`); a
value above the application's stops the host. A run opens files through the application's
`INhAssistantFileSource` with its own actor (the agent, with the initiator and accountable owner),
takes the highest classification of its files, agent and skill, and refuses files it may not take
with `assistant-files-not-accepted`; `use_skill` refuses a skill that cannot take the files the turn
already reads. In the administration an agent's run settings and a data skill store
`acceptsFiles`, off for a new one, and next to it lower limits: `maxFileBytes`, `maxFilePages`,
`maxFilesPerTurn` and `maxFileTokensPerTurn` (`null` keeps the application's, for a skill the
agent's). Each is a whole number from 1 to the application's limit, which the run catalog shows as
`fileLimits` (`NhAssistantFileLimitsDto`), and only next to `acceptsFiles`; otherwise the save fails
with field errors `invalid`. A turn takes the lowest of the application's, the agent's and the
skill's, also when the application later lowered its own, and `uploads` of `GET status` and
`GET agents` follows the agent's. The nh-ai-assistant rule covers the file source and its limits.

A person's message to an agent's thread while a run works waits for the next run, and so do its
files: the queued input keeps their references (`AssistantQueuedInput.FilesJson`, never content), and
the run's turn opens them as attachments with the person who wrote the message as actor, even when
another person started the run, within the limits of the skill the run works in. A file it cannot
open, may not read or that does not fit is named to the model with its code instead, the run goes on,
and the message in the thread keeps only the files the model read. A run that works by rules opens
none; a later model turn of the message's author opens them as files of an earlier turn, while a turn
of anyone else names them with `assistant-file-forbidden` instead of reading what that person only
referenced with its own rights.
SampleProjectManagement's document reader (SPM-306) shows it: a document added to a project's folder
starts its `read-documents` skill through a trigger that names the skill, both the agent and the
skill call `AcceptsFiles()`, and the skill's tool names the project's documents in its result
(`INhAiToolResultFiles`) without content, so the file source opens them for the agent.

```csharp
.ConfigureAgent("sample-project-document-reader", agent => agent
    .WorksOn("project")
    .Grant("project-documents.read")
    .TriggerOnly()
    .AcceptsFiles()
    .Skill("read-documents", skill => skill
        .Tools("project-documents.read")
        .AcceptsFiles(limits => limits.MaxFileBytes = 10 * 1024 * 1024)))
```

Let separate agents hand work to each other with hand-offs, never with events published to start
another agent. Both sides opt in: the target lists its sources with `AcceptHandOffs(subjectType,
handOff => handOff.From(sourceAgentId).Description(...).NotifySource().Limits(maxPerSubjectPerDay))`,
and a source's model gets the generated tool `assistant.handoff.{agentId}.{subjectType}` only when
its tool selectors (or its skill's) select it; a source's rules call
`NhAssistantRuleContext.HandOffAsync(targetAgentId, subject, message, payload)`. Only runs hand off;
people never get the tool. The target works in its own thread under its own grants, approval
policies, budget and audit, as trigger `agent-handoff` without an initiator, and reads the message as
data from the named agent. A paused target lets the work wait; a stopped target, an agent that does
not list the source, a subject outside the source's scope key, a shadow run handing to a live agent,
a chain beyond `MaxHandOffDepth` (2) or back to an agent in it, and the daily limit per source,
target and subject refuse with their codes. One run hands the same subject to the same agent once.
With `NotifySource()` the source gets an `agent-handoff-ended` input, in the skill it handed off
from, whose payload is an `NhAssistantHandOffResult` with the outcome and proposal count, and the
target's summary only when both worked on the same subject, every reader of the thread that receives
the follow-up may read the target's thread, and the target read nothing restricted with its own
grant. The audiences match when the target's required policy, and for the whole application its view
policy, names no policy or the source's own, and every person invited to the source's thread may read
the target's thread too; otherwise the follow-up names the outcome and the proposal count only, so a
target with a narrower audience tells its source how it ended without its answer. Shadow work never starts live work on the way back either: when the target's
run or the handing run worked in shadow, the follow-up goes to the source agent's shadow thread of
the subject, whatever the source's mode is by then, and while the source agent or skill is paused it
is ignored instead of waiting for a resume.

Let administrators connect agents with hand-offs as data only within a ceiling in code. Call
`UseHandOffsAsData(handOffs => handOffs.Allow(subjectType, rule => rule.To(...).From(...)).MaxPerSubjectPerDay(n))`:
each `Allow` names, per subject type, the agents that may accept hand-offs as data (`To`, or every
agent created in the administration with `ToAgentsCreatedInAdministration`) and the agents that may
hand off (`From`, or `FromAgentsCreatedInAdministration`), and startup applies the checks of
`AcceptHandOffs` to the agents in code it names. Save a hand-off with
`INhAssistantDataHandOffs.SaveAsync` (or `PUT admin/agents/{id}/hand-offs/{subjectType}`) with
`NhAssistantDataHandOffRequest`: the sources, a description, `NotifySource` and a daily limit within
the ceiling's; at most `NhAssistantDataHandOffLimits.MaxHandOffsPerAgent` per agent, one per subject
type, and never one the agent accepts in code (`assistant-handoff-in-code`). The target and every source
need a run configuration in effect and the target must work on the type, as in code. Once saved it
works as a hand-off in code: the same generated tool with the source's tool selectors as its second
key, the same depth, scope, daily limit and shadow rules, and the audit
`AdminAgentHandOffDefinitionChanged`. Every load checks it again: one that no longer fits the ceiling or
the code (`outside-ceiling`, `unknown-source`, `unknown-agent`, `not-worked-on`, `conflicts-with-code`,
`limit-above-ceiling`, `invalid`) is out of effect as a whole with a problem of kind `hand-off`, and is
never used with less than it was saved with.

Evaluate a change to an agent's instructions, profile, skills or triage before it goes live with a
shadow variant: register the changed agent with its own id and `ConfigureAgent(variant, agent =>
agent.ShadowOf(liveAgentId))`. Every event, scheduled and webhook trigger of the live agent then
fires for the variant too, with the same subject, message, dedupe key and payload; publishers read
the live receipt. The variant keeps what shadow mode guarantees (threads for administrators only, no
notifications, nothing executes) and its own budget and state. `GET admin/agents/{id}/evaluation?
variant=...` pairs the runs that handled the same event or that a replay repeated and returns, per
tool, the proposals both made (same arguments or same objects), only one made, and what people
decided about the live ones, with cost and routing of both; counts, ids and thread links only.
`.../evaluation/export` writes the pairs as audited JSON Lines. `POST admin/agents/{variant}/replay`
fires the recorded runs of a period into a shadow agent, oldest first and against today's state, at
most 500 runs and one replay at a time, until the variant's state, budget or run limits stop it.
Decide from the report; nothing promotes a variant automatically.

Let administrators create shadow variants as data only within a ceiling in code:
`UseShadowVariantsAsData(variants => variants.Of(liveAgentId).OfAgentsCreatedInAdministration().MaxPerAgent(n))`
names the live agents in code that may get them, which startup checks as it checks `ShadowOf`. A data
variant is an agent created in the administration with `shadowOf` (`POST admin/agents`, or
`NhAssistantAgentRequest.ShadowOf` of `CreateAgentAsync`) that names its live agent once and never
changes it. It has its own definition, run settings and data skills, which the administration starts
from the live agent's definition; once it has run settings it receives the live agent's triggers, with
a trigger's skill when it has one of that id, but never its hand-offs or consultations. It is never
offered in the chat, always works in shadow (`assistant-shadow-variant` for its mode and its groups)
and is compared and replayed as a variant in code is. `GET admin/agents/{id}/shadow-variants` lists the
variants in code and as data. A variant that no longer fits (`outside-ceiling`, `unknown-live-agent`,
`live-agent-not-live`, `too-many-variants`) has its run settings out of effect and never runs, live or
in shadow.

Promote a data variant that did better onto its live agent explicitly, never by copying its fields by
hand: `INhAssistantAgentDefinitions.PromoteShadowVariantAsync(variantId, new
NhAssistantShadowVariantPromotionRequest(expectedVersion, MoveSkills, ExpectedVariantVersion, Reason),
actorId)` (`POST admin/agents/{id}/promote` for administrators) copies the variant's definition onto the
live agent as its next version through the update path, with its checks, package locks, version and
audit, then deletes the variant: the variant never goes live itself. The live agent keeps its display
name, MCP assignments, metadata, run settings, triggers, state and shadow mode; an agent in code takes
only the fields it can override (description, instructions as text, tool selectors, required policy
and autonomy), an agent created in the administration also the variant's profile, classification,
what it works on and its asset reference. With `MoveSkills` each data skill of the variant becomes the
next live version of the live agent's skill of the same id, or a new skill, which starts in shadow; a
skill the live agent has in code refuses the promotion before anything changes
(`assistant-skill-in-code`). An agent that is no data variant answers `assistant-not-shadow-variant`, a
live agent that may no longer have data variants `assistant-shadow-variants-not-allowed`, a stale
version `assistant-version-conflict`. The steps run in that order and a failure stops there, with the
variant still in place. The audit records `AdminAgentShadowVariantPromoted` (the live agent, its new
version and the variant) after the events of each step. SPM-330.

Promote a variant whose parts all came from one agent package as that package's next version of the
live agent, not by importing the package onto the live agent again: `PromoteShadowVariantAsync` with
`NhAssistantShadowVariantPromotionRequest { AsPackageVersion = true }` (`asPackageVersion` of `POST
admin/agents/{id}/promote`; `GET admin/agents/{id}/shadow-variants` names a data variant's `PackageId`
and `PackageVersion`) applies every part of the variant to the live agent with the import's own plan
and rules, in one transaction (SPM-344): its definition without a variant's display name, chat offer
and `shadowOf`, its run settings, skills, triggers, consultations, hand-offs and the metadata entries
it carries, with the package's locks and hashes. The live agent keeps its display name, chat offer
and technical account (it takes the variant's only when it has none), an agent in code takes the
fields it can override and keeps its run settings in code; the live agent's parts are linked to the
variant's package id and version, it leaves the package it belonged to before, which keeps its row,
and the variant is deleted with its links. A part of the variant without a link refuses with
`assistant-package-not-linked`, `MoveSkills` with `moveSkills: not-allowed`, and a part the import
would refuse refuses everything with the import's `assistant-package-refused`, whose field errors
name it as `parts.{part}.{field}`; nothing changes then. The parts' change events are audited as an
import audits them, then one `AdminAgentShadowVariantPromoted` with
`NhAssistantShadowVariantPromotions.PromotedAsPackageVersion` and the package's id, version and hash
(`NhAssistantShadowVariantPromotionDto.Package` reports every part's outcome). Without the option the
promotion is unchanged and a package-linked variant stays `assistant-package-locked`.

The live agent's linked parts that the variant does not carry, such as a hand-off or a trigger the
next version leaves out, stay on the live agent unchanged and by default lose their link
(`NhAssistantUncarriedParts.Unlink`). When they belong to the package version as well, promote with
`UncarriedParts = NhAssistantUncarriedParts.Relink` (`uncarriedParts: "relink"` of `POST
admin/agents/{id}/promote`, SPM-352): they stay linked, to the variant's package id and version, with the
locked fields, locked values and hash of their link, so their locks keep holding and `ChangedLocally`
still says whether they changed since their import; the promotion reports them as
`NhAssistantPackagePartOutcomes.KeptLinked` (`kept-linked`) and writes nothing of them. An unknown value
fails with `uncarriedParts: invalid`, `Relink` without `AsPackageVersion` with
`uncarriedParts: not-allowed`. No migration: the link table holds the locks.

Move agents and their configuration as data between hosts as one versioned agent package with
`INhAssistantAgentPackages`, never as a series of saves an application calls one after the other. A
package (`NhAssistantAgentPackage`, format `newheap.assistant.agent-package`, `formatVersion` 1)
holds several agents: an agent created in the administration whole (definition, run settings without
the technical account, live data skills and triggers, consultations, the hand-offs it accepts,
metadata and groups by name), an agent in code as an overlay of what data may change on it (definition
overrides, data skills, triggers, consultations, hand-offs and overrides of `Editable()` metadata),
which the import applies only to the same agent in code. It never holds history, candidates, runs,
run state, shadow overrides, MCP servers, the technical account or secrets. `ExportAsync(agentIds,
options, actorId)` (`POST admin/agent-packages/export`, a JSON download `agent-package-{id}.json`)
takes exactly the chosen agents, sets the package id (the linked package's, else the first agent's),
the version (the next after the linked package's, else 1) and the locks, and warns about every
reference to an agent outside the selection in the header `X-NewHeap-Package-Warnings`
(`NhAssistantAgentPackageRoutes.WarningsHeader`); a host that serves the administration from another
origin exposes it, for example with `UseCors(cors => cors.WithExposedHeaders(NhAssistantAgentPackageRoutes.WarningsHeader))`
of NewHeap.Platform.AspNet.Common, whose policy exposes only `Content-Disposition` and the correlation id. Read a package a tool
wrote with `NhAssistantAgentPackageFormat.Read`: an unknown property, a missing one, another format
or a newer version is refused (`assistant-package-format-unsupported`), and a package above 1 MB or
with more than 20 agents too (`assistant-package-too-large`). `PreviewImportAsync` (`POST
admin/agent-packages/preview`) reports per part (`agent`, `runSettings`, `skill:{id}`,
`trigger:{id}`, `consultation:{type}`, `handOff:{type}`, `metadata:{name}`) whether the import
creates, changes or keeps it unchanged, keeps it local because an administrator unlinked it
(`kept-local`), leaves it behind because the package no longer contains it (`left-behind`), or
refuses it with the field errors of its save, after one check of the whole package against the host
as it would be after the import, so hand-off sources, `shadowOf` and consultation askers may be other
agents of the package, cycles included. `ImportAsync` (`POST admin/agent-packages/import`) writes
every agent and part in one transaction with one change of the run configuration, or nothing
(`assistant-package-refused` with the plain field error `parts: invalid`; the preview names every
refused part with its code and field errors): an agent whose id is
free is created, one the package linked is upgraded, any other agent with the id refuses the package
(`assistant-agent-exists`). Name the technical account per agent in
`NhAssistantAgentPackageImportOptions.ServiceAccounts` (an upgrade keeps the host's); it must be able
to sign in. New skills and triggers start in shadow; with `Apply = "shadow"` a changed skill or
trigger becomes the candidate next to its live version, to compare and promote. Every part is audited
as its save, then `AdminAgentPackageImported` with the package's id, version and hash only.

Lock what a package must keep with `locks` per agent and part, `*` or fields of
`NhAssistantPackageLockFields` (for example a skill's `instructions`). Every save path refuses a
change of a locked field with the field error `{field}: locked` and the removal of a locked part, and
the deletion or reset of an agent that belongs to a package, with `409 assistant-package-locked`;
a save may keep the value it has or take the package's. Run state, pauses, the kill switch, shadow
modes, the technical account and groups stay free. `UnlinkAsync(agentId, part, actorId)` (`POST
admin/agents/{id}/package/unlink`, without a part for the whole agent) takes a part out of the package,
audited as `AdminAgentPackageUnlinked`; the next upgrade keeps it as `kept-local` unless the import's
`Relink` names it, while an unlocked field the host changed is overwritten and shown as changed
locally. The administration's DTOs carry the link of every linked part (`Package`, with its locked
fields), and `AssistantAgentPackage` and `AssistantAgentPackageLink` store packages and links with the
AgentPackages migrations for SQL Server and PostgreSQL. `GetPackageAsync(packageId)` returns the package
the host imported (`NhAssistantInstalledAgentPackageDto`: version, format version, hash, agents, imported
at and by) with the current links of its parts per agent (`NhAssistantPackageAgentLinksDto`, each with
`ChangedLocally`), also after every agent was unlinked, when `GetLinkAsync` returns nothing; it is `null`
for a package the host never imported or an id that is no package id. `ReadAsync(agentIds)` returns
exactly those agents, 1 to 20, with their parts and `Hashes` built as `ExportAsync` builds them but
without locks and without the `AdminAgentPackageExported` audit, to compare an installed agent with a
shipped package or with its links; it refuses the agent ids an export refuses (SPM-331). An
administration that compares over HTTP calls `POST admin/agent-packages/read` with `{ "agentIds": [...] }`
(`NhAssistantAgentPackageReadRequest`, answering `NhAssistantAgentPackageReadDto` with `agents`) and
`GET admin/agent-packages/{packageId}` (answering `NhAssistantInstalledAgentPackageDto`), behind the
administration policy and without an audit (SPM-353; routes `NhAssistantAgentPackageRoutes.Read` and
`Package`): the read answers the field error `agentIds` and `404 assistant-agent-not-found` as the service
does, the package `404 assistant-package-not-found` for a package the host never imported, and a caller
without the administration policy `403 assistant-forbidden`.

The schema of the format, which tools that write packages follow:

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "title": "newheap.assistant.agent-package 1",
  "type": "object",
  "additionalProperties": false,
  "required": ["format", "formatVersion", "package", "agents"],
  "properties": {
    "format": { "const": "newheap.assistant.agent-package" },
    "formatVersion": { "const": 1 },
    "package": {
      "type": "object",
      "additionalProperties": false,
      "required": ["id", "version"],
      "properties": {
        "id": { "type": "string", "pattern": "^[a-z0-9]+(-[a-z0-9]+)*$", "maxLength": 128 },
        "version": { "type": "integer", "minimum": 1 },
        "description": { "type": "string", "maxLength": 500 }
      }
    },
    "agents": {
      "type": "array",
      "minItems": 1,
      "maxItems": 20,
      "items": {
        "type": "object",
        "additionalProperties": false,
        "required": ["id", "source"],
        "properties": {
          "id": { "type": "string" },
          "source": { "enum": ["admin", "code"] },
          "definition": { "type": "object", "description": "displayName, description, instructions or instructionsAsset, toolSelectors, requiredPolicy, autonomy, offeredInChat, worksOn*, perDivision, runPolicy, viewPolicy, profile, dataClassification, startsOff, startsInShadow, shadowOf" },
          "runSettings": { "type": "object", "description": "the fields of NhAssistantAgentRunSettingsRequest without serviceAccountActorId, expectedVersion and reason" },
          "skills": { "type": "array", "items": { "type": "object", "required": ["id", "description"] } },
          "triggers": { "type": "array", "items": { "type": "object", "required": ["id", "source"] } },
          "consultations": { "type": "array", "items": { "type": "object", "required": ["subjectType", "description"] } },
          "handOffs": { "type": "array", "items": { "type": "object", "required": ["subjectType", "sources"] } },
          "metadata": { "type": "object", "additionalProperties": true },
          "groups": { "type": "array", "items": { "type": "string" } },
          "locks": { "type": "object", "additionalProperties": { "type": "array", "items": { "type": "string" } } },
          "hashes": { "type": "object", "additionalProperties": { "type": "string", "pattern": "^[0-9a-f]{64}$" } }
        }
      }
    }
  }
}
```

Every object of a part is read as strictly as the package itself. A part's hash is the SHA-256 of its
canonical JSON: the part as the format writes it without null values, object properties ordered by
name, no white space (`NhAssistantAgentPackageFormat.HashOf`).

Give an agent continuity with `Memory<TMemory>(schemaVersion)`: the model reads and
changes a working memory per thread through the internal `memory_get` and
`memory_update` functions with JSON Merge Patch. Every version must deserialize into
`TMemory` without unknown members and stay within 16 KB; a concurrent change makes the
patch fail instead of overwriting it. Register an `INhAssistantAgentMemoryMigrator` with
`UseAgentMemoryMigrator` when the schema version changes. Tools save larger output with
`INhAiArtifacts` as versioned files of the thread (at most `MaxArtifactBytes`). Each run
starts with an `<agent-continuity>` block holding the memory, the names and versions of
the files and the summaries of the last `MaxRunSummaries` runs; treat all of it as data
the agent wrote, never as instructions. Results the agent obtained with its own grant
reach only people who satisfy the tool's policy; others see `restricted`. Call
`INhAiSubjectRetention.ForgetSubjectAsync(subject, scopeKey)` after the application deleted a
subject, with the scope key its resolver described (`NhAiSubjectInfo.ScopeKey`) read before the
delete, so its threads, runs, memory and files go with it while an equal id of another tenant or
division keeps its own. The overload without a scope key forgets the id in every scope; it is
obsolete and safe only for ids that are unique across tenants and divisions.

Let other assistants consult an agent with `AllowConsultation(subjectType, ...)` on its
`ConfigureAgent`. It generates the governed tool `assistant.consult.{agentId}.{subjectType}`
and, with `AllowRequests()`, `assistant.request.{agentId}.{subjectType}`; the asking agent
uses them only when its own `ToolSelectors` select them (use
`NhAssistantConsultationTools.ConsultToolId`), so both sides opt in. Neither tool can be
granted to an agent or lent by the account an agent answers to: a consultation always runs with
the asking person's rights, the person chatting or the initiator who delegated an agent turn, and
a call in an agent turn that no initiator delegated is refused with
`assistant-consult-subject-unavailable`. Give it a `Description` the
asking model can choose by, a `Policy` the asker must pass (otherwise the agent's
required policy or the assistant's access policy; without a `Policy` of its own every call also
checks the agent's required policy as it is in effect then, so an administrator who changes it
in the agent's editor restricts the tools at once, while the generated tools keep the policy of
code and a change that admits more people does not offer them more), and keep the defaults unless the
subject needs others: `MinimumRole(Reader)` to ask, `Proposals(true, Participant)` before
tools that change anything are offered, `Limits(4 calls, 60 seconds, 4,000 characters)`
and `Exposure(Local | Agent)`; add `NhAiToolExposure.Mcp` only when MCP clients may run
consultations. Name the consultation's tools with `Tools(params string[] toolSelectors)`, by id or
`prefix.*`, when the agent's own tools reach beyond the subject: the consulted model then sees only
those, a call of another tool runs nothing (`assistant-consult-tool-not-selected`), and a
consultation tool of another agent (`assistant.consult.*`, `assistant.request.*`) is offered only
when a selector names it. Each selector must be covered by the agent's own selectors, as a skill's
tools are, or startup fails; the selection only narrows, so effects, routes and the depth limit still
apply. Without `Tools` a consultation offers every tool of the agent. A consult answers in the same turn: the agent reads its working memory,
earlier summaries, recent thread history and the artifacts the asker may read, with its
tools under the asker's rights only. Anything it would change becomes a proposal in its
thread with `consultationId`; it never executes during the consultation, because
`RequireApprovalForEffects` on the agent scope enforces that in the tool invoker. That is why a
consultation with a proposal route in effect for the call offers the tools that change state to an
agent with `Propose` or `Simulate` autonomy as well as `Execute`; `Observe` and `Explain` agents only
read, a consultation that only answers offers them to nobody, and the agent's model keeps being
offered them in its own turns and runs only with `Execute`. Its rules, whose every effect is a
proposal for the same reason, propose with them from `Propose` on. Give an agent `Explain` or
`Observe`, or remove its consultation's proposal routes, when it must not propose. Choose who
decides it with `Proposals(p => p.MinimumRole(...).AskerDecides().AtSubject(rights).Policy(id))`;
each call takes the first route that applies and the audit names it (`ConsultationRoute`):

- `AskerDecides()`: the person who asked decides it right away in their own chat. The asking
  turn waits as for its own inline proposal, the card names the consulted agent and the subject,
  and approving executes it in the asker's own request with their live rights, so no person's
  principal is rebuilt. It applies when the policy lets the asker decide (an approver rule they
  meet, such as `NhAiApprovers.Initiator`, no `FourEyes()`, the tool's approve policies) and the
  asking turn is the asker's own turn in their own request; it lives at most
  `NhAssistantLimits.ApprovalLifetime`. The card may offer "leave for approvers" (decision
  `leave`) when a route at the subject applies. Only the asker decides it; the inbox never shows it.
- `AtSubject(NhAssistantConsultationRights.Asker)`, the route of `Proposals(true, role)`: it waits
  for the subject's approvers and executes later with the asker's rights, which the
  application's `INhAiPrincipalFactory` rebuilds outside the asker's request.
- `AtSubject(NhAssistantConsultationRights.Agent)`: it waits for the subject's approvers and
  executes with the agent's own rights (its grants, trigger-only ones included, or the rights of
  the technical account it acts with), never the asker's. The call must also be one the asker may
  make, the platform always makes it four-eyes, and an asker has at most
  `MaxOpenConsultationProposalsPerAsker` of them open per subject. Startup refuses it for an agent
  that answers to the subject's responsible person, for the whole application and for a policy
  that names only the initiator.

A consultation's own `Policy(id)` applies where the agent has no policy for the tool itself; the
agent's policy was written for its runs. When no route applies, the call is refused with
`assistant-consult-proposal-refused` and a reason of `NhAssistantConsultationRefusalReasons`, and the
consulted agent describes the change instead. Applications that never rebuild a person's
principal use `AskerDecides()` with `AtSubject(NhAssistantConsultationRights.Agent)` or either
alone. Administrators switch each route on or off per agent within what code allows
(`SetConsultationRoutesAsync`, `POST admin/agents/{id}/consultation-routes`), never beyond it. The
asker of a proposal that waited at the subject is told how it ended, with either rights
(`ConsultationProposalDecided`). The consult writes no message into the
thread; `GET threads/{id}/consultations` lists who asked through which assistant, without
the question or the answer. A request starts an ordinary run with the asker as initiator
(`NhAssistantTriggerIds.AgentRequest`) under the rule of a start by hand, as `POST threads/start`
would for the asker, and notifies the initiator with `RequestAnswered`. `McpConnectionMissing` tells only the person whose consent to background use or
connection a personal MCP server needed (`McpConnectionPersonId`), at most once per server and day;
custom notification policies and channels handle every kind. An application whose `INhAiToolDiscoveryPolicy` lists tool prefixes
must allow `assistant.consult.` and `assistant.request.`. Bound consultations with
`NhAssistantLimits.MaxConsultationDepth` (1 by default; an agent already in the chain is
never asked again) and `MaxConsultationsPerActorPerDay`; they also spend the consulted
agent's daily `Budget`. `NhAssistantLimits.MaxConsultationsPerTurn` (1 to 100, unlimited by default)
bounds the consultations one turn starts, and the asking agent's `MaxConsultationsPerTurn(count)` in
`ConfigureAgent` replaces it for that agent's turns, higher or lower; every consultation the turn starts
counts, also one refused later, and the next one is refused first with `assistant-consult-turn-limit`
before the consulted agent runs (SPM-278). A consultation outside a turn, such as one through MCP, has no
turn to count; it starts at depth 1 without an asking agent, unless a reviewed caller of another
application sent its consultation chain (`ContinueMcpChain`, SPM-366): it then continues that chain,
its agents count as askers and the depth limit bounds the whole path, as the assistant rule describes.
Make `INhAiSubjectAccessResolver` accept only the canonical id of a
subject, because the asking model types the id. Unknown subjects and subjects the asker
may not read both return `assistant-consult-subject-unavailable`. About the whole application the
asker reads the agent's work only with the agent's view policy (by default its required policy), so a
person who reads the whole application but fails that policy is refused the consultation and the
request as for an unknown subject, as the agent's thread, runs and proposals already answer them.

Let administrators define consultations as data only within a ceiling in code. Call
`UseConsultationsAsData(data => data.AllowRoutes(...).AllowRequests().MaxLimits(...))`: it
names the routes data may use (answering without proposals is always allowed), whether data
may generate request tools, whether MCP clients may see a consultation that asks for it
(`AllowMcp`, off by default) and the highest limits. Agents created in the administration
with run settings get that ceiling; an agent in code gets consultations as data only with
`ConfigureAgent(...).ConsultableAsData(...)`, whose ceiling applies together with the
application's, and startup refuses a code ceiling beyond the application's or one with
`subject-agent-rights` for an agent without rights of its own. Do not name
`subject-asker-rights` unless the application rebuilds people's principals, and give
`subject-agent-rights` only to agents whose technical account or grants may lend their
rights: for an agent created in the administration it means its technical account's rights,
and saving it is refused for one that answers for itself. Save a consultation with
`INhAssistantAgentConfiguration.SaveConsultationAsync` (or `PUT admin/agents/{id}/consultations/{subjectType}`)
with `NhAssistantDataConsultationRequest`: a description, optional instructions, who may
ask (`minimumRole` and an `askerPolicy` listed with `AddAgentPolicy`; without one the agent's
required policy as it is in effect at each call decides, as in code), the routes, the
proposal role and approval policy, the tools (`ToolSelectors`, within the agent's own selectors,
refused otherwise with `toolSelectors: not-allowed`; none offers every tool of the agent), limits
within the bounds of `AllowConsultation` and the ceiling, requests, MCP and the classification of
the answers; at most
`NhAssistantAgentConfigurationLimits.MaxConsultationsPerAgent` per agent. A consultation is
in effect once saved, versioned like a data trigger, audited with
`AdminAgentConsultationDefinitionChanged` and never changes one in code
(`assistant-consultation-in-code`). A new one starts off only when asked to (`StartsOff` of
`NhAssistantDataConsultationRequest`) or when the application's ceiling says so for every new one
(`StartOff()` on `NhAssistantConsultationCeilingBuilder`, reported as
`NhAssistantConsultationCeilingDto.StartsOff`); consultations saved before keep their state. Off
(`NhAssistantDataConsultationDto.Off`), it is saved and checked but not offered: asking agents get none
of its tools until an administrator switches it on with `SetConsultationStateAsync` (`POST
admin/agents/{id}/consultations/{subjectType}/state` with `NhAssistantConsultationStateRequest`,
`state` one of `NhAssistantConsultationStates`, `on` or `off`), without a new version, and a change
keeps whether it is on. The administration's consultation form offers "Start off", and the list marks
one that is off and switches it on and off. It answers only when an asking agent selects its tool:
the asking side stays the asking agent's tool selectors, so prefer an exact
`NhAssistantConsultationTools.ConsultToolId` over `assistant.consult.*`. Its tools live in
the catalog `assistant-consultations-data`, which follows the configuration in effect on
every host within seconds; a call of a tool whose consultation was removed or whose contract
changed fails with `assistant-consult-agent-unavailable`, while proposals it left can still be
decided. An MCP client sees a consultation that asks for MCP only through
`WithNewHeapPlatformAITools`: a stateful MCP server (`Stateless = false`) tells its sessions with
`notifications/tools/list_changed` when consultations come and go, clients of a stateless server
see the change on their next `tools/list`, and a call of a removed consultation from a stale list
is refused by the export with `ai-tool-not-authorized` before the agent is consulted. Data that no
longer fits after a deploy is reported as a problem of kind
`consultation`: out of effect when its agent, subject type, works-on or asker policy no
longer fits, without the routes, requests or MCP exposure that no longer fit, or with limits
capped at the ceiling. A tool selector the agent's own selectors no longer cover drops with
`tools-not-allowed`; when none is left the consultation offers no tools, never every tool of the
agent (`ToolSelectorsInEffect` is then empty). The route switches of `SetConsultationRoutesAsync` apply to
consultations in code and as data alike. Run settings of an agent created in the
administration stay while consultations belong to it. The administration's run configuration
(`NhAssistantAgentRunConfigurationComponent`) shows them in its Consultations section, with a form
that offers only what the ceiling and the agent allow, the agent's own tools as check boxes under
Advanced (none checked offers every tool), the agents that ask each one, its tools and its
problems.

Learn from rejected proposals before letting an agent learn by itself. After a person
rejects a proposal, offer the optional feedback of `PUT approvals/{approvalId}/feedback`:
a category from `NhAssistantProposalFeedbackCategories` and at most
`MaxFeedbackCharacters` of text, never a required step and never a question before the
decision. The categories fall into `NhAssistantProposalFeedbackGroups`; count only the
`wrong` group as a mistake of the agent, because `not-now` and `already-done` say the
proposal was right at the wrong moment. The person who decided the proposal and the
subject's approvers may give feedback on rejected, expired, withdrawn and stale proposals,
one record each that they may change; `POST approvals/feedback` marks feedback for
proposals rejected together as bulk. `NhAssistantProposalDto` returns only the caller's own
`feedback` and `canGiveFeedback`. The next run on the same subject starts with the thread's
last `MaxRejectionsInContext` rejections of `RejectionContextWindow` and their feedback
inside its `<agent-continuity>` block, as people's notes rather than instructions, so it
does not propose them again unchanged; consultations never receive them. Administrators
compare rejection rates and categories per tool and agent version with
`GET admin/agents/{id}/feedback/summary`, read the texts with `GET admin/agents/{id}/feedback`
and export JSON Lines for their own analysis with `.../feedback/export`, which is audited.
Forward feedback to the application's analytics with `AddProposalFeedbackSink`; the sink
receives the text, the business audit sink only the category
(`AgentProposalFeedbackGiven`). Feedback is deleted with the subject's threads.

Close proposals that people overtook. A proposal can wait for days while a person does the
work by hand. Register an `INhAiProposalGuard` for the tools it knows with
`AddProposalGuard<TGuard>(toolSelectors)`; several guards may apply to one tool and the first
stale result wins. `GetKeysAsync` names the application objects a proposal changes
(`NhAiProposalKey`, such as `("project-task", id)`), which the platform indexes with the
thread's scope key when the proposal is made. `CheckAsync` returns
`NhAiPreconditionResult.Satisfied` or, when the proposal no longer fits, `AlreadyDone`,
`NotApplicable` or `Stale` (changed) with a reason code and an explanation callback, which the
platform renders in every supported UI culture with that culture current, for example "Already
completed by Henk on 14 October". Guards run without a person in their own service scope; keep
them read-only and fast. A guard that throws never closes a proposal. Guards run at every stage
by default (`NhAiProposalGuardStages`): when the application revalidates, before an approval in
either decision endpoint, also for a person's own chat proposal, and right before execution next
to `RequireUnchangedSubject`. Limit an expensive guard to `Decision | Execution`, knowing it then
never closes an open proposal by itself.

Keep work that is already done from being proposed at all. Add `NhAiProposalGuardStages.Proposal`
to a guard's stages (`AddProposalGuard<TGuard>(selectors, NhAiProposalGuardStages.All |
NhAiProposalGuardStages.Proposal)`; `All` leaves it out): its `CheckAsync` then also runs when a
rule (`NhAssistantRuleContext.ProposeAsync`) or the model proposes, before anything is recorded.
A stale result records no proposal, no approval and no `ApprovalRequested` audit event. Rules get
`NhAssistantProposeOutcome.NotNeeded` with the guard's reason code as `Code`, the kind of its result
as `NotNeededKind` (`NhAiStaleKind.AlreadyDone`, `NotApplicable` or `Changed`), its explanation in
the run's culture as `Explanation` and in every supported UI culture as `Explanations` (culture name
to text, the same texts a stale proposal stores), so `Done(culture => ...)` can say in each reader's
language why nothing was proposed; the model gets `assistant-proposal-not-needed` with `reason`,
`kind` and `explanation` as its tool result, and the tool call reads `not-executed`. A guard that
throws at proposal time lets the proposal be recorded; the decision and the execution check again.

Know the call that proposes. At the proposal stage, and for the keys of the new proposal, the
`NhAiProposalGuardContext` also names the call that proposes: `Invocation` (its `NhAiInvocationContext`
with the actor, scope, tenant and run), `ConversationId`, `TurnId` and `Channel`, the application's
channel of `INhAssistantConversationTurns` such as `messaging-chat`. A guard can then refuse before an
approval card is shown, for example a change that a channel's chat may not propose, with a stale result
whose reason and explanation the model reads (SPM-349 keeps the messaging app from proposing to archive
a project). At revalidation, decision and execution no call proposes and they are `null`, as they are
for a proposal an asker leaves for the subject's approvers, so register such a guard for
`NhAiProposalGuardStages.Proposal` alone and never authorize or refuse at the later stages on them.
They take no part in the proposal hash or the approval evidence, and the guard still runs without a
signed-in person: the tool's policies keep authorizing the call.

Revalidate after the change committed. Call `INhAssistantProposalRevalidation`
`RevalidateSubjectAsync(subject)` or `RevalidateKeysAsync(keys, scopeKey)` from the outbox
consumer or after the commit (without a scope key it uses `global`), or declare it with
`AddProposalRevalidation<TEvent>` and its `Subject`, `Keys` and `ScopeKey`:
`INhAssistantAgentTriggers.PublishAsync` then checks whether an agent has an open proposal about
the event's subject or objects and only then queues the revalidation as a background operation,
which runs once the publisher's transaction committed; an event without open proposals costs one
indexed query. Call the explicit form from a consumer that runs after the commit anyway, to skip
the background operation altogether. Revalidation covers the
open proposals of agent runs and consultations; a person's own chat proposal is checked when
they approve it, and repeating a revalidation changes nothing. A proposal that no longer fits
closes as `stale` with `staleKind` (`NhAssistantStaleKinds`), its reason code in
`executionResultCode` and `staleExplanation` in the reader's language; its tool call reads
`not-executed`, `AgentWorkChanged` reaches the subject's members, the business audit sink
receives `AgentProposalInvalidated` with the kind, reason code and stage
(`NhAssistantInvalidationStages`) but no explanation, and a run that waits for the inline
proposal continues with "not executed". Approving such a proposal is refused with
`409 assistant-proposal-stale`; a failing guard refuses it with
`503 assistant-proposal-check-failed` and keeps it open. The feedback report counts proposals
that closed before a decision as `invalidated` per kind and reason, never as rejected, and the
next run's continuity lists the already-done and no-longer-applicable ones in
`<invalidated-proposals>`. The agent inbox and the subject suggestions keep a proposal that
closes while it is shown as a closed card with its reason until it is dismissed.
A guard may name who overtook a proposal with `NhAiPreconditionResult.ByActorId`; proposals then
carry `staleByActorId` and `staleByDisplayName`, and every proposal carries the objects its
guards named as `objects` (`NhAssistantProposalObjectDto`), so clients group proposals by object
without parsing `targets`. A guard that only names keys and returns `Satisfied` is enough.

Keep agents from repeating themselves. Before a proposal of an agent run or a consultation is
recorded, the platform compares it with the same agent's proposals under the thread's scope key.
An identical proposal (the same tool and canonical arguments) that still waits or executes is
not recorded again; the model receives an "already proposed" result
(`assistant-proposal-already-open`) instead. A proposal for the same objects, the keys its guards
name, with other arguments replaces the pending one by default: the older one closes as
`withdrawn` with `assistant-proposal-superseded` and `supersededByApprovalId`, a run that waits
for it inline continues, and an approved or executing proposal is never replaced. Choose
`Duplicates(NhAiDuplicateProposals.Skip)` on the approval policy to keep the older proposal or
`Allow` to record both. People's rejections hold for the policy's `RejectionCooldown`, seven days
by default and at most `MaxRejectionCooldown`: the same proposal, or one for the same objects, is
refused as `assistant-proposal-recently-rejected` within it. Duplicates are audited as
`AgentProposalDeduplicated` and replacements as `AgentProposalSuperseded`; neither counts,
notifies or shows as a new proposal. A person's own proposals in a chat turn are never compared.
A shadow proposal never waits for a decision, so it counts as open for exactly its proposal
lifetime: an identical one (the same tool and canonical arguments) of the agent in its shadow
threads of the scope is not recorded again and the model or the rules receive `AlreadyOpen`, as
live would, with the `AgentProposalDeduplicated` audit event (SPM-324). Shadow proposals also store
the objects their guards name, as live ones do, so after the identity rule the policy's rule for a
newer proposal for the same objects applies among the agent's shadow proposals in its shadow threads
of the scope that are still within their lifetime (SPM-339): `Skip` reports the newer one as
`AlreadyOpen`, `Supersede` records it and closes the older one as `shadow-superseded`
(`NhAssistantApprovalStatuses.ShadowSuperseded`) with `supersededByApprovalId` and the
`AgentProposalSuperseded` audit event, and `Allow` keeps both. The rejection cooldown does not
apply, because nobody rejects a shadow proposal. Shadow proposals, `shadow-superseded` ones included
(`NhAssistantApprovalStatuses.IsShadow`), stay out of revalidation by subject or object, inboxes,
`INhAssistantProposalCounts`, feedback and notifications. The evaluation of a shadow variant counts
every proposal once, in the run that made it: a replaced shadow proposal pairs with the live proposal
that a newer one replaced too, as `withdrawn`, so it compares what the live agent would really propose.

Run an agent by fixed rules when its decisions follow from data. Implement
`INhAssistantAgentRuleHandler` and register it with `UseRuleHandler<THandler>()` on
`ConfigureAgent`: event, schedule, webhook, manual, request and follow-up runs then call the
handler instead of the model, spend no tokens and read `kind: "rule"` (`NhAssistantRunKinds`).
The handler reads the application's own data and evaluates the subject as it is now, because
runs keep no typed event, a coalesced run receives several inputs and retries repeat them; a
trigger may store a small `Payload(...)` per input (at most `MaxInputPayloadCharacters` of JSON)
that the handler reads with `GetPayload<T>()`. It proposes with
`NhAssistantRuleContext.ProposeAsync(toolId, arguments, rationale)` through the same governed flow
as a model's tool call: the agent's selectors and grants, its approval policy (always deferred for
rules), presenters, guards, deduplication, audit and live updates. Only tools that need approval
can be proposed and nothing executes before people decide; a tool the agent may not use, or one
whose arguments do not fit, is `Refused`. Every effect of a rule turn is a proposal, so the rules
of an agent with `Propose`, `Simulate` or `Execute` autonomy propose with its tools that change
state, also while the turn offers or withholds compositions and the script tool; the rules of an
`Observe` or `Explain` agent are refused. A rule turn uses exactly the tools it selects and never
the agent's selectors in their place. The rationale is rendered in every supported culture;
approvers read it as `rationale` on the proposal and its approval part, and it ends the rule's
message in the thread. Finish with `NhAssistantRuleResult.Done(culture => summary)` so people read the
run's result in their own language (`Done(summary)` writes it in the run's culture only), or hand an unclear case
over with `HandOver(reason)`: the same run continues with the model, which reads in its briefing the
reason, what became of each proposal the rules asked for and the recent decisions they saw, and reads
`rule-then-model`. `UseModel()` on a trigger sends its runs to the
model. A person's message in the thread and a consultation always reach the model, so keep a model
profile and instructions that answer questions about the rules' proposals from the thread.

Keep the application's own data about agents, such as the cost center their work is booked on, as
agent metadata instead of a table of the application keyed by agent id. Register each entry once with
`AddAgentMetadata<T>(name, entry => entry.MaxBytes(...).Validate(...).IndexBy(...).Editable())`: `T` is
the value's JSON shape (camelCase), the size limit counts the bytes of its JSON (4,096 by default, at
most `NhAssistantAgentMetadataLimits.MaxBytes`), and a validator reports stable dash-case codes per
field through `NhAssistantAgentMetadataErrors`, never the value itself. An agent in code sets its value
in `ConfigureAgent` with `Metadata(name, value)`; startup checks it exactly as a save checks data, so a
value that a save would refuse fails startup. Administrators change the entry of an agent in code only
when it is `Editable()`: their value is an override with a version of its own, and removing it lets the
value in code apply again. An agent created at runtime gets values with its definition (`metadata` of
`POST` and `PUT admin/agents`, `NhAssistantAgentRequest.Metadata` of `CreateAgentAsync`) or per entry
with `PUT admin/agents/{id}/metadata/{name}` and `INhAssistantAgentConfiguration.SaveMetadataAsync`,
always with the expected version: each change is the agent's next version. Values are stored per agent
and name in `AssistantAgentMetadata` (the AgentMetadata migrations) and removed with the agent. Read them
with `INhAssistantAgentMetadata`: `GetAgentAsync` returns the agent with the values in effect (typed
with `NhAssistantAgentWithMetadata.Get<T>`), and `ListAgentsAsync(name, value)` finds the agents whose
searchable value (`IndexBy`) matches, trimmed and without regard to case on both providers. NewHeap
never interprets metadata and it never grants an agent or a person anything: authorize what the
application does with it in the application. The audit records `AdminAgentMetadataChanged` with the
entry's name and the version only.

Declare several searchable values with `IndexByEach(value => value.Codes)` when one agent belongs to
several of them, such as the departments it works for, instead of joining them into one `IndexBy`
string. An entry declares `IndexBy` or `IndexByEach`, never both. Each value is trimmed, in lowercase
and stored once, at most `NhAssistantAgentMetadataLimits.MaxIndexValuesPerEntry` (50) of at most 256
characters (`value: too-long` otherwise, a failed startup for a value in code), in
`AssistantAgentMetadataIndexValue` (the MetadataIndexValues migrations), written in the same save as the
value and removed with it. `ListAgentsAsync(name, value)` matches any of them, stored or in code;
`NhAssistantAgentMetadataValue.IndexValues` and `NhAssistantAgentMetadataEntryDto.IndexedByEach` show
them. Values stored before the entry declared `IndexByEach`, or before its selector changed, get their
values when the process first reads the agents, each guarded by its version; the main `IndexBy` column
still changes only on the next save. SPM-313.

Change the definitions of agents from code with `INhAssistantAgentDefinitions` (`UpdateAsync(agentId,
request, expectedVersion, actorId)`, `ResetAsync`, `DeleteAsync`), never by writing the agent tables or
calling the administration's endpoints from the application. It applies the administration's checks,
also that `RequiredPolicy` is a registered policy, its versions, package locks and audit, to agents
created at runtime and to overrides of agents in code; `NhAssistantAgentDefinitionRequest` replaces the
six fields of the definition and keeps every optional field, also the MCP assignments, unless it names
them. Read agents as they are in effect with `INhAssistantAgentDirectory` (`GetAsync`, `ListAsync` with
`NhAssistantAgentDirectoryFilter`): `NhAssistantAgentInEffectDto` holds the source, the name and
description in effect (keys for an agent in code), the instructions resolved from an asset or the
asset's problem, tool selectors, autonomy, profile, run state with `INhAssistantAgentControl`'s state,
shadow and `ShadowOf`, and the metadata in effect. A read and a change at its `Version` refuse a change by
someone else in the meantime. Both are server APIs that authorize nothing and the directory returns
instructions in full: call them for an administrator or process the application authorized. SPM-329.

## Avoid
- Deciding rights, routes or approvals from agent metadata in NewHeap hooks; metadata is the
  application's data and never widens what an agent may do.
- Putting secrets, prompts or people's data in agent metadata, or validator codes that repeat the value;
  the administration shows it and export may carry it.
- Registering one metadata name in two compositions; register each entry once, next to the agent that
  needs it, and let other agents get values for it.

- An event `When` that compares the event with one agent: it applies to every trigger on the event.
  Register a named filter with `Filter(...)` and select it on the data trigger.
- Skipping every change an agent made, whatever the subject: its change on one subject may rightly
  start it on another. Compare both the actor and the subject with the filter context.
- Reading the database or calling services in a trigger filter: it runs for every published event.
- Deciding with `INhAssistantProposalDecisionService` for a person the channel did not authenticate,
  such as an actor id or address read from the reply itself: the service believes the person it is
  given. Bind the reply to the person with a token the application issued to them, and rebuild their
  principal as their own sign-in would.
- Deduplicating channel deliveries in the application before deciding, or deciding again after a
  timeout to be sure: pass the delivery as the idempotency key and let a repeat return the earlier
  decision.
- Stopping an agent by editing its definition or sending `isEnabled`; pause or stop it with
  `INhAssistantAgentControl` or `POST admin/agents/{id}/state`.
- Stopping an agent for an import or a migration that should lose no events; pause it.
- Letting a skill read confidential data while its model calls stay `Internal`: any profile that
  permits `Internal` then takes them. Classify the skill or agent with `DataClassification(...)`.
- Pausing an agent for weeks: every event of the period waits and runs at the resume. Switch it off
  instead, so switching it on replays nothing.
- `Enabled(false)` for an agent that administrators should switch on after a deploy: it reads
  `configured-off` until the next deploy. Use `StartsOff()`.
- Deploying to put an agent or a skill live after an evaluation in shadow, or to replace its
  technical account; change it at runtime with `INhAssistantAgentControl`.
- Registering a skill or trigger in code that administrators are meant to change; register its
  building blocks and let them define it as data. Putting a data skill or trigger live without
  reading its shadow runs first, or putting a real change of a live skill or trigger live without
  evaluating it as a candidate (`Apply` `shadow`); changing a live trigger's filters in place to see
  what they would do.
- Creating an agent at runtime that should be evaluated before it acts live and putting it in shadow
  in a later request: use `StartsInShadow`, so no event reaches it live in between. Letting a new
  consultation be offered before it was reviewed when the application wants that: save it with
  `StartsOff` or set `StartOff()` on the ceiling.
- Letting an administration agent answer to a technical account with more rights than its work
  needs, or to itself when it must use tools: it then has no rights at all.
- Changing the text of a published version of a text asset: every load refuses it as `asset-changed`.
  Publish a new version and save it as the skill's or agent's next version.
- A text asset source that answers a missing asset by throwing: return `null`, so the problem says
  `asset-missing` instead of an unavailable source.
- A technical account with more rights than the agent needs: it lends them to gateway reads.
- Letting the model read a subject's basic facts through tool calls in every run; brief it.
- A tool whose input names an object without `[NhAiSubject]` when runs for the whole application or for
  another object propose it: the proposal then waits in the run's own thread and is authorized in the
  run's scope instead of the object's.
- Trusting the model, or a rule, to choose where a proposal waits; declare the subject and let the
  platform check the object.
- Splitting one role into several agents with their own threads only to give its tasks separate
  tools, models or budgets; declare skills. Use separate agents for separate roles.
- Model triage for events a trigger or rule can route.
- Putting judgements, instructions or data that not every reader of the thread may see in a
  briefing.

- Granting a tool to an agent without `AllowAgentGrant` on the tool, or adding
  `AllowAgentGrant` to tools that do not need it.
- Tying a tool's capability to a policy only people pass to keep agents or technical accounts away
  from it, or using capabilities to decide who may use the assistant; leave `Agent` out of the tool's
  `Exposure`, and use the assistant's `AccessPolicy` and the MCP policy.
- Filtering refused tool calls on `ai-tool-authorization-denied` alone: a refusal carries its own
  code, such as `ai-tool-capability-denied`, `ai-tool-agent-not-granted` or `ai-tool-effect-denied`.
- Running an agent under a person's identity so that its actions look like that
  person's actions.
- Letting the agent decide or execute its own proposals, or approving them on a
  client-supplied hash instead of the server's proposal.
- Deriving who may decide a thread's proposal in the client from the subject role alone;
  four-eyes and the tool's approve policy are part of `thread.decisions`.
- Authorizing thread access from the member snapshot instead of the resolver.
- Keeping a person's subject-joined participant rows after their role ended instead of calling
  `INhAiSubjectAccess.RefreshAsync` when the people on the subject change.
- Publishing triggers inside a transaction on a DbContext that does not hold the
  background operations, or inside an ambient `TransactionScope`, which the
  background operations do not support; the platform cannot defer such a trigger.
- Reading the run or thread from the receipt of a trigger published inside a
  transaction; it reads `deferred` until the commit. Keep its `InputId` or the event's dedupe key and
  find the run with `INhAssistantAgentRunLookup` after the commit.
- Finding the run of an event through `AssistantRun.TriggerEventKey` alone: a run that coalesced
  several events records the first event's key only.
- Reading a run's status, summary or result from the assistant's tables; read them with
  `INhAssistantAgentRunLookup.FindRunAsync`, and never render its result as HTML.
- Deciding at revalidation, decision or execution on `NhAiProposalGuardContext.Invocation`,
  `ConversationId`, `TurnId` or `Channel`: only the proposal stage names the call that proposes, and
  the later stages read them as `null`. Register a guard that needs them for the proposal stage alone.
- Comparing whole `NhAiProposalGuardContext` or `NhAssistantExecutionReportReceipt` records, for example
  in a cache key or a test: their init-only properties take part in record equality. Compare the
  members you mean.
- Writing a guard without object keys for a tool whose arguments change on every run, such as a due
  date of today, when its agent is evaluated in shadow: without keys every changed argument is a new
  shadow proposal and the evaluation counts more than the live agent would propose.
- Removing the assistant's schedule registrations by their internal types on a host that must not run
  the schedules, or leaving `RunSchedules` true on a host without a Hangfire server or with other
  agents, whose data schedule sync removes the jobs it does not know; set `RunSchedules` to `false`.
- Placing agent runs on a queue of their own without a reservation when other work can fill the global
  limit, or reserving slots without a queue limit when agent work must leave room for other work.
- Treating `execution-unknown` as failed and executing a non-idempotent tool
  again automatically.
- Waiting for an asynchronous system inside a tool's function, or polling it from an agent run, instead of
  declaring the tool with `ExecuteInApplication` and reporting through
  `INhAssistantProposalExecutionReports` when the system answers.
- Trusting an object id in a tool call's or a hand-off's arguments because the tool's policy passed:
  the policy authorized the active division or the subject, never the id. Load the object within the
  authorized division in the tool's function, and bind it to the approver again in an executor.
- Starting the work of a hand-off twice for one approval id, reporting without the hand-off's
  `TenantId`, putting free text or people's data into a report's code, or putting into its summary what
  the model must follow; the summary is information, the code goes to the audit.
- Cancelling an agent run's background operation to stop the run instead of `POST runs/{id}/cancel`.
- Accepting unsigned webhook calls, sharing one secret between triggers, or passing
  free text from a webhook body to the agent as instructions.
- Picking notification recipients from the run's background-operation owner instead
  of the subject's members.
- Keeping secrets, personal data that outlives the subject or instructions for later
  runs in the working memory or in artifacts.
- Asking approvers to decide on the tool input alone, or on the model's own description
  of the change, which may differ from what the proposal executes.
- Showing results of agent-granted reads to everyone who can see the thread, or
  leaving a deleted subject's agent threads behind.
- Letting a consulted agent use its own grants, execute changes or update its memory, or
  writing the asking person's question and the answer into the consulted agent's thread.
- Offering a consulted agent every tool it has when the consultation concerns one kind of object,
  or narrowing them with a second agent or a skill instead of `Tools(...)` on the consultation.
- Rebuilding a person's principal outside their request to execute a consultation's proposal in
  an application that cannot do so safely, instead of `AskerDecides()` or
  `AtSubject(NhAssistantConsultationRights.Agent)`; letting the asker approve a proposal that runs
  with the agent's rights, or giving that route to an agent that answers to a person.
- Building agent-to-agent calls by hand (for example a tool that calls
  `INhAssistantAgentTriggers` and waits, or an event published only to start another agent)
  instead of `AllowConsultation` or `AcceptHandOffs`, or raising `MaxConsultationDepth` or
  `MaxHandOffDepth` without a reason.
- Accepting hand-offs from every agent, letting a source wait inside its run for the target, or
  passing a target's results to the source beyond its summary.
- Opening every agent created in the administration to every agent in `UseHandOffsAsData` when the
  application means a few connections, or expecting a stored hand-off to keep working with fewer
  sources or a lower limit after a deploy narrowed the ceiling: it goes out of effect.
- Expecting a data shadow variant to go live: its mode never changes; promote it onto its live agent
  with `PromoteShadowVariantAsync`, which copies its definition and removes it, or create the live agent
  separately.
- Importing a package onto the live agent again, unlinking and deleting its variant by hand, to put a
  package version evaluated as a shadow variant live: promote it with `AsPackageVersion`, which does
  all of it in one transaction.
- Hiding an agent that only chats with an empty `WorksOn` statement, `StartsOff` or a narrow
  `RunPolicy`: say `ChatOnly()`, which startup checks, and keep run settings off it.
- Changing agents through the agent tables or the administration's HTTP endpoints from the application,
  or reading `NhAssistantAdminAgentDto` to show an agent: use `INhAssistantAgentDefinitions` and
  `INhAssistantAgentDirectory`, and send the version read with the change.
- Joining several codes into one `IndexBy` value to search an agent by any of them: declare
  `IndexByEach`.
- Importing agents with a series of saves, or writing a package with a technical account, secrets or
  MCP servers in it; import the package once with `INhAssistantAgentPackages` and name the account per
  agent in the import options.
- Locking whole parts to keep one field, or expecting a lock to stop an administrator for good: lock the
  fields that must stay, and know that an administrator may unlink a part.
- Exporting agents only to compare them with a package, which audits an export, or reading the
  installed version from the links, which an unlink removes: use `ReadAsync` and `GetPackageAsync`, or
  over HTTP `POST admin/agent-packages/read` and `GET admin/agent-packages/{packageId}`.
- Saving the skills and agents that name a text asset live automatically when the application publishes
  a new version, or saving a candidate per skill by hand: propose it with
  `INhAssistantInstructionAssetUsage.ProposeCandidatesAsync`, which respects package locks, and let an
  administrator compare and promote each candidate and save each listed agent.
- Unlinking and relinking the live agent's parts by hand after a promotion as the package's version, or
  copying them into the variant only to keep them linked: promote with
  `UncarriedParts = NhAssistantUncarriedParts.Relink`.
- Reading a package leniently or dropping what a host does not know; read it with
  `NhAssistantAgentPackageFormat.Read` and fix a refused package at its source.
- Registering an agent's triggers twice to evaluate a variant; use `ShadowOf`. Promoting a variant
  on percentages of a few runs, or reading a replay as the past: it ran against today's state.
- Requiring a reason before a person may reject a proposal, or counting `not-now` and
  `already-done` feedback as the agent's mistakes.
- Feeding one subject's feedback text into runs on other subjects or scope keys, or
  changing an agent's instructions automatically from feedback without a person's review.
- Writing feedback text to logs or business audit sinks; it is a person's words about a
  subject.
- Presenting a proposal in only one language in a multilingual application, or building
  presentation text from the request culture of whoever happened to start the run.
- Polling agent-run endpoints for changes instead of reloading on `AgentWorkChanged`, or
  putting content in a live update. A run a person started by hand reports its start and end
  to that person too; poll it only as a fallback for a missing hub connection.
- Resolvers whose `ResolveAsync` or `ListMembersAsync` depend on the current HTTP request; live
  updates and notifications call them from background operations.
- Calling the thread lookup once per agent or the subject work once per list row; use the
  subject work for one subject and the summary for lists.
- Starting a run just to open the conversation about a subject; use `threads/open`.
- Revalidating inside the domain transaction with `INhAssistantProposalRevalidation`, where
  guards in their own scope read uncommitted or locked rows, or revalidating on reads instead of
  after changes.
- Guards that change data, call slow external systems on every revalidation, or report stale
  when their own check failed.
- Writing feedback in the name of the system for proposals that closed as stale, or counting
  them as rejections.
- Proposal keys without the scope key in an application whose ids repeat across tenants or
  divisions.
- Driving deterministic decisions through a scripted chat client or a model prompt; register a
  rule handler.
- Changing data from a rule handler through the application's services instead of proposing it,
  or deciding from the run's message text instead of the subject's current state or a payload.
- Choosing `Allow` or a zero cooldown so that rules propose again; fix the rule or let people
  decide the open proposal.
- Reading a whole thread's proposals to refresh the one a live update names, or polling proposals
  by id instead of reading them when `AgentWorkChanged` names them.
- Searching subjects in the client or returning every subject from `INhAiSubjectSearch`; search the
  person's own subjects server-side within the limit, and leave access to `ResolveAsync`.
- Naming subject types in the client's own texts, or registering an application type that starts
  with `nh-`; name the type with `Named` where it is registered.
- Leaving an agent that works on a few object types without `WorksOn`, or checking in a tool or
  page whether an agent may work on a type; declare it and let the platform refuse other starts.
- `Subject(e => new NhAiSubjectRef("project", ...))` on a trigger whose type is fixed; use
  `Subject("project", e => ...)` so startup checks it.
- A placeholder object such as an "application" subject type, or a subject set that yields one fixed
  subject, for work on the whole application; leave the subjects out and declare
  `WorksOnApplication()`.
- `PerDivision()` in an application that does not list its divisions, or a division directory that
  lists divisions the division access does not know.
- Leaving the read policy of the whole application at an access policy that needs an active division;
  nobody would read it app-wide.
- Relying on the approve policy of the whole application to let people run its agents by hand; give
  the agent a `RunPolicy` (by default the administration policy decides).
- A path of your own that creates a run for a person, such as a restart, a waiting message or a
  request, without the rule of `POST threads/start`; call `INhAssistantAgentTriggers.StartAsync` only
  after checking that the person may start the agent.
- Showing a run's result from `NhAssistantLatestRunDto.Result`, `NhAssistantRunHistoryDto.Result` or
  `NhAssistantRunResultDto.Result` as HTML; it is model output and may carry MCP tool output.
- Writing a rule handler's result once with `NhAssistantRuleResult.Done(summary)` in an application
  with more languages; people would read it in the culture the run worked in. Use
  `Done(culture => ...)` with the application's localized texts.
- Listing runs for people straight from the database, or reading a run's answer from its summary; use
  `GET runs` and `GET runs/{id}`, which decide per object and per view policy who sees a run.
- Counting runs for usage or billing by querying the assistant's tables or by parsing `ResultCode`; read
  `TriggerKind`, `Shadow`, `SkillVersion`, `ModelCallCount`, `RunStatus` and `RunFailureCode` of
  `AgentRunEnded` in a business audit sink.
- Granting the read policy of the whole application to people who should not see one agent's work
  there; the agent's thread of the whole application also needs its view policy, so give the agent a
  `ViewPolicy` (by default its required policy decides).
- Building a dashboard page of agents with own queries for their latest results; register a group with
  `AddAgentGroup`, put the agents in it in code or in the administration and show it with
  `nh-assistant-agent-board`, which follows the view policies and records the visits.
- Registering one group id with different names in two compositions; register the same group, for
  example through one shared helper.
- Grouping or naming inbox proposals by calling a subject resolver per proposal, or sorting a page of
  the inbox on the client; name the parent in `DescribeAsync` and ask the server for `grouped=true`.
- Selecting or deciding the proposals of a group on pages the approver has not seen; a group's total
  may span pages, and only the proposals shown may be selected.
- Registering a group id that starts with `nh-`; the platform reserves those ids.
- Storing the whole application's work under the host's tenant, or giving every person no tenant, to
  let one customer's people see it; call `UseSingleTenant` with the tenant that
  `UseAuthenticatedClaimsForSingleTenant` projects.
- Passing `null` as `NhAssistantDecidingPerson.TenantId` for a person who has a tenant: the person's
  digest counts zero, their decisions and subject work read as unknown, and a channel may decide
  whole-application work the person may not take part in; pass the tenant the context resolver gives
  the person, and treat the `NhAssistantTenancy` warning in the log as this mistake.
- Relying on the subject access resolver for the tenant boundary, or calling `INhAssistantSubjectWork`
  without the person's tenant in a host with several tenants; the resolver gets no tenant, so pass
  the person with `NhAssistantDecidingPerson` and let the platform compare tenants.
- Forgetting a deleted subject without its scope key in an application whose ids repeat across
  tenants or divisions, which deletes another tenant's threads with the same id.

## Verification

For subject access, remove a member after they opened the thread and assert on both providers
that the refresh removes their subject-joined participant row while an invitation stays, and that
no live update, Web Push or in-app notification of the thread reaches them, also before the
refresh; assert that agent notifications leave out a person the principal factory no longer
rebuilds (`AssistantLiveReadRuleTests`, `AssistantNotificationReadRuleTests`,
`AssistantUnresolvedPeopleTests`, SPM-270).

For agent metadata, on both providers, save a value of an agent created at runtime and assert the
agent's next version, a stale `expectedVersion` (`assistant-version-conflict`), the field errors of
the validator (`value.<field>`), `value: too-long` above the size limit and `value: invalid` for a
field the type does not have; override an editable entry of an agent in code and assert its own version
and the value in code next to it, refuse a read-only entry (`assistant-metadata-read-only`), find agents
with `ListAgentsAsync` in another case, delete the agent and assert that its values are gone, assert
`403` for people who do not administer and that the audit carries the entry's name only; assert that
startup refuses a value in code that a save would refuse.

For the inbox overview, on both providers, assert the flat expiry order without `Groups`, the grouped
order (groups by first expiry, a group's own subject first, runs together) with each group's `Count`
and `Total` and its continuation on the pages before and after, that the division and run filters
group what they keep with their own totals, that a proposal records the parent its resolver named when it
was made and keeps it when the parent changes later, that a subject named as its own parent is refused,
that `NewApprovalIds` follows the last visit per person and scope (all before the first visit),
that a visit of a division the caller does not work in answers `404` and stores nothing, that the run
maintenance keeps recent inbox visits, and that `AddAgentGroup` refuses `nh-` ids.

For webhook deliveries, on both providers, post a signed delivery that starts a run and a second one
that joins it, let the run claim both and end, and assert that delivering either again answers
`deduplicated` with that run and starts none; do the same for two deliveries that waited while the
agent was paused and that the resume run claimed.

For trigger event filters, publish the agent's own change on the trigger's subject and assert
`ignored` with `filtered` and no audit, publish the same agent's change made on another subject and a
person's change and assert a run each, assert that the filter's `AgentActorId` and `Subject` equal
`NhAiInvocationContext.ActorId` and the ambient scope's subject inside the agent's tool call, assert
that a tool in a run reads the run's id as `AgentRunId` while `RunId` is the turn's, save an
unknown filter and assert `assistant-trigger-filter-not-found`, and store a filter the code no longer
registers and assert `filter-missing` and the problem `unknown-filter`, on both providers.

For the trigger event lookup, on both providers, assert that the receipts of a started, a coalesced, a
waiting and a deferred fire name their input, a deferred one before the commit, and a redelivery none;
that `FindByEventAsync` returns a started event's input with its queued run, a coalesced event's input
waiting without a run and then with the run that claimed it, a waiting event's input and after the resume
the run of the resume, a deferred event only once its fire ran and a redelivery's first delivery; that
`FindByInputAsync` returns the same; and that a lookup for another tenant finds nothing, also for the
whole application's work, which the single tenant finds. For `FindRunAsync` assert the facts of a
queued and an ended run, `null` for an unknown run and for another tenant's run, and the whole
application's run for the tenant that takes part. For hosts that do not run schedules, assert that
a host with `RunSchedules` false starts without Hangfire, fires its schedules and adds or removes no
recurring job next to the scheduling host's storage, and that a host with the default registers the jobs
of schedules in code and as data and removes a data schedule's job its snapshot does not know. For the
agent runs' queue, assert that every assistant operation type uses it and that the queue gets its limit.
SPM-326 is the executable reference.

Publish the event twice and assert one run. Assert that the run's background
operation is owned by the agent, that the deferred proposal is visible only to
people with a role on the subject, that the initiator cannot approve under four
eyes, that an approver executes it once through a background operation, and that
an edited subject closes an approved proposal as stale. Read the thread conversation as
the initiator, a second approver and a participant and assert `canDecide` and
`reasonCode` per proposal, `authorKind: "trigger"` on the trigger's message, and no open
decision once the proposal was decided. Fire a burst of events and
assert one pending run, repeat a schedule tick and a webhook delivery and assert no
second run, refuse a forged or stale signature, engage the kill switch and assert
cancelled runs and rejected triggers, and assert that shadow work notifies nobody.
Patch the memory with a value outside the schema and assert that nothing changed,
assert that the next run receives the memory, the files and the previous summary,
that a viewer without the tool's policy sees a restricted result, and that deleting
the subject removes its thread. Verify the `nhai` migrations and the
background-operation owner columns on SQL Server and PostgreSQL. Consult an agent as a
participant and assert the answer, the consultation record without content and no new
thread message; consult as someone without a role and as a reader and assert the shared
refusal code and that no tool that changes anything was offered; assert that a
consultation's proposal waits deferred and executes with the asker's rights only, that with
`AskerDecides()` the asker decides it in their own chat and it executes in their request, that with
`AtSubject(NhAssistantConsultationRights.Agent)` the asker cannot approve it and it executes with the
agent's grant, with a principal factory that refuses people and counts calls, that every refusal
reason reaches the model and that the maintenance job expires both kinds, that a
loop or a deeper chain is refused and that consultations count in the agent's daily
usage on both providers. Give a consultation `Tools(...)` and assert that a tool outside it is
neither offered nor executed when the model names it; save a data consultation with a tool outside
the agent's selectors and assert `toolSelectors: not-allowed`, then narrow the agent's selectors and
assert `tools-not-allowed` with no tools offered, on both providers. Reject a proposal and assert that only its decider and the
subject's approvers may give feedback, that a pending or approved proposal refuses it,
that a second feedback replaces the first, that the next run's continuity lists the
rejection with the feedback on one line, that the report counts statuses, categories and
distinct authors per tool and agent version on both providers, and that forgetting the
subject deletes the feedback. Make a proposal in a headless run in one culture and read it
in each supported culture; close a deferred proposal by rejection and by expiry and assert
that its tool call reads `rejected` and `not-executed`; let an inline proposal expire under
`FailRun` and under `Withdraw`, once with the run's operation waking first and once with the
maintenance job first, and assert the run's status by the policy, one `AgentRunEnded` event
and one live update of the proposal in both orders, on both providers; let a run wait inline, then
cancel its operation as an operator, fail it as `dispatch-stalled` and fail it on its payload schema,
and assert that the maintenance job leaves it alone until the operation has been terminal for
`StaleAttemptTimeout` of real time, then ends it once with its code and class, withdraws its
proposal, frees its thread for the next run that completes, and that further rounds change
nothing, while a run whose operation still waits stays waiting; cancel a waiting run as a
participant and assert that its thread is idle and the next run completes; approve an inline
proposal, cancel its run as a participant and assert that the job leaves the proposal `approved`
until the operation has been terminal for `StaleAttemptTimeout`, then withdraws it once with one
proposal update and no tool call, on both providers; let a model keep failing for an agent with
`Retry(5)` and with `Retry(0)` and assert six and one failed attempts and a run failed as
transient; decide a proposal and assert that
`AgentWorkChanged` reaches the subject's members. Start an agent by hand on a subject without a
member snapshot, as a person no member list names, let the run propose nothing and assert that
`AgentWorkChanged` reaches that person and the subject's members when the run runs and when it
ends, that it no longer reaches the person once their role is gone, and that a shadow thread
started by hand reaches only its own audience and fills no snapshot. Read a subject's work as an
approver, the initiator, a participant and an outsider and assert the decision rights and the refusal;
summarize several subjects in one request and assert the counts per role; open a thread for a
subject without work and assert that no run started. Change an object by hand, revalidate by
subject and by key and assert that only the guarded proposal closes with its kind, reason code
and an explanation in each supported culture, that a second revalidation changes nothing and
another scope is left alone, that approving it is refused with `assistant-proposal-stale`, that
a throwing guard keeps a proposal open, that a waiting inline run continues with not executed,
and that the report counts the invalidation apart from rejections, on both providers. Start a
rule handler from an event and assert that no model was called, that the run reads `rule` with no
tokens, that the proposal waits deferred with its rationale in each supported culture and that the
thread's message reads `rule`; watch the whole application's work on the hub as a person who passes
an agent's view policy and as one who does not and assert who receives its proposals, that renewing
sends nothing twice and that stopping the watch ends them, on both providers; finish a rule run with `Done(culture => ...)` and assert that `GET
runs/{id}`, `GET runs`, `GET admin/agents/{id}/runs`, the agents of the subject and the board read the
result in an English and a Dutch request and a model's answer as written, on both providers; hand a
run over and assert `rule-then-model` and that the model's briefing names what became of each rule
proposal and the rejections the rules saw; assert `NotNeededKind` and `Explanations` per supported
culture for a proposal a proposal-time guard found not needed; assert that such a guard sees the
invocation, conversation, turn and channel of a channel's turn and refuses before the card, that the
decision's guard sees them as `null` and that the stored proposal does not carry the channel
(SPM-349). Propose the same change
twice from rules and from a model and assert one proposal and the deduplication audit; propose for
the same objects with other arguments and assert the replacement, `Skip`, `Allow` and that an
approved proposal stays; reject a proposal and assert that it is refused within the cooldown and
accepted after it; assert that a tool without approval or outside the agent's selectors is refused
and that the kill switch and shadow mode apply to rule runs, on both providers. Publish inside an
application transaction with the background operations in the application's own context and
assert that a rollback leaves no thread, run, input or operation, that a retried commit with the
same dedupe key starts one run with only its input after the commit, that publications of one
transaction coalesce, that publishing after the commit is unchanged, that a retried deferred
operation fires once and that maintenance queues a run again that lost its operation, on both
providers. Read a proposal by
id in every status and assert its fields and the live decision right of an approver, the initiator
under four eyes and a reader; assert 404 without a role, for another tenant and for a hidden agent,
that a lookup returns only visible proposals in request order and refuses more than 100 ids, and
that reading does not join the thread, on both providers. Decide through
`INhAssistantProposalDecisionService` and assert that every refusal of the decision endpoint is the
service's and its facts' (a wrong hash, an expired proposal, a participant, an outsider, the initiator
under four eyes, an agent, a decided proposal, a proposal decided in a conversation and a proposal of
the whole application for an approver without the agent's view policy), that the audit records the
channel and the idempotency key where the endpoint records `web`, that a repeated delivery returns
the earlier decision once without a second execution or audit event, and that another answer
afterwards is refused as not pending, on both providers. Pause an agent and assert that
events wait once per dedupe key, start no run and skip schedule ticks, that a run queued before
the pause ends without claiming its inputs, and that resuming starts one run per thread; stop it
and assert cancelled runs, withdrawn proposals, dropped waiting events, refused triggers and held
executions that the resume queues, consultations included, behind more than a batch of queued
executions; assert that the version is unchanged, that editing and reset keep the state, that a
code-disabled agent reads `configured-off` and refuses to resume, that a stopped agent and a `When`
filter evaluate no further lambda and that the global switch stops triggers, schedules, runs and
execution queuing, on both providers. Switch an agent off and assert that an event, a data trigger,
a schedule tick and a webhook read `ignored` with `agent-off` without running a lambda, waiting or
an audit; that a run that started finishes and its proposal stays decidable and executes while its
follow-up is ignored; that people still chat and start it by hand; that waiting inputs of a pause
end and switching on starts no run; that a skill that is off ignores its triggers with `skill-off`
and that rules and triage do not route to it; that releasing the kill switch leaves an agent that
was off off and its skills in their states, while a paused or stopped agent resumes, and that
`OffBeforeStop` is true only while an agent that was off is stopped; and that
`StartsOff()` applies only at the first start, also after a new host start and a deploy, on both
providers. Assert that a skill's classification, or else its agent's, reaches the model call, also
in a plain chat with the agent, where it moves to the profile's fallback that permits it; that a
profile chain that does not permit it refuses the call with
`assistant-classification-not-permitted` without a retry; that a data skill and an agent created
in the administration store their classification and refuse an unknown one, and are saved with the
warning `classification-not-permitted` when no profile of the chain permits it, on both providers;
and that an agent or skill in code with such a classification is logged at startup. Assert that a
host whose chat, agent, skill or triage profile does not permit the execution region through its
fallbacks does not start, that a data skill and an agent created in the administration with such a
profile are refused with `profile: region-not-permitted`, and that a turn no profile may serve in the
region for its classification fails with `assistant-model-region-denied` without a model call or a
retry, on both providers. Assert that a shadow run proposes an unchanged finding once within the
proposal's lifetime and again after it, and that approving a proposal of a changed object with
`RequireUnchangedSubject` is refused as `assistant-proposal-stale` through the endpoint and the
decision service, also with the application's own precondition, while the decision facts report
`SubjectChanged`. Assert that an agent configured for runs and a skill refuse files with
`assistant-files-not-accepted` until `AcceptsFiles()` or `acceptsFiles` in the administration switches
them on, that a run opens a file for the agent, and that an agent's limit only lowers the
application's, on both providers. SPM-306 runs the sample's document reader end to end: its skill
reads a PDF and a Word document a tool result names, and an upload to the intake reviewer's thread is
refused with `assistant-files-not-accepted`. Assert that an agent created in the administration keeps the profile it chose, uses
the chat profile without one and refuses a profile that the run catalog does not offer, on both
providers. Assert that a headless run, a consultation and a person's
turn in the thread read the briefing once, before the continuity, with markup neutralized; that a
rule run that hands over passes its reason inside it; that a briefing over its bound is cut at a
section boundary with a note and that a throwing or slow provider leaves the turn without one; and
that a run at its ceiling fails with `assistant-run-token-limit`, keeps its proposals and finishes
after a resume, on both providers. Assert that a trigger with a skill runs that skill and records
it, that rules and triage route to a skill with only its tools, instructions and profile, that
triage can end a run with nothing to do, that `use_skill` switches at most twice and that a person's
turn starts without a skill, that a chat offers `use_skill` only with `SkillsInChat`, without triage,
narrows the tools to the skill's, applies the skill's token ceiling and records the skill on the
turn's message, that a chat-only agent keeps its skills and questions in a chat while every start
path refuses it with `assistant-agent-does-not-work-on`, no subject lists it and startup refuses each
run-only setting with it, that a stopped skill refuses its triggers before any lambda while the
other skills work, that a paused skill lets its events wait and resumes one run per thread, that
a skill's budget stops its runs, that a skill's rules hand over to the skill's own model with its
tools, instructions, profile and ceiling, and that a follow-up run continues in the skill of the
run that proposed unless that skill is stopped, on both providers. Assert that a rule and a model
hand a subject to a target that works in its own thread without an initiator under its own
policy, that one run
hands the same subject off once, that a paused target lets the work wait and runs it after the
resume, that every refusal carries its code, that the chain and daily limits hold, and that the
source's follow-up carries the target's summary only for the same subject without restricted reads,
on both providers. Put an agent and a skill in shadow at runtime and assert that a trigger,
a routed run and a run queued before the change work in the shadow thread with shadow proposals,
that waiting events move along on the resume, that `use_skill` in a live thread does not offer the
skill, that members see the live thread and its open proposals but neither the shadow thread nor a
manual start, that a variant cannot change and that `code` follows the code again; replace a
technical account and assert that an unknown account is refused, that new runs and the threads
answer to the new one while earlier runs keep theirs, on both providers. Add a data skill and a data
trigger and assert that both start in shadow, that the trigger's runs work in the shadow thread in
the skill by its named rules until both are live, that saving refuses names code does not register,
tools outside the agent's selectors and skills or triggers in code with field errors and conflicts,
that every change is a version and audited, that a change stored by another host is followed through
the stamp, that data which no longer fits is reported instead of loaded and that a data schedule
starts its skill for every subject of its set, on both providers. Give an administration agent run
settings and assert that its rules propose with its technical account's rights as `Accountable`, that
an unknown account and names code does not register are refused, that an agent that answers to itself
is refused the tool, that its thread follows a change of whom it answers to and that its settings stay
while triggers belong to it; assert that a read with accountable rights is shielded from a viewer
without the tool's policies, on both providers. Give an account and a delegating initiator a tool's
policy without its capability and assert that an agent in code with a grant runs the tool as `Agent`,
that an agent without a grant is not offered it and is refused with `ai-tool-capability-denied`, and
that a principal with both lends its rights as `Accountable` or `Delegated`. Save a data skill and an agent created in the
administration with a reference to the application's text asset on both providers and assert that the
skill's runs and the agent's turns read the asset's text, that a wrong hash, a missing asset and a
reference without a source are refused at save, that a hash that changes later takes the skill out of
effect and keeps the agent from running at the next load, that a failing source keeps a verified text,
that inline instructions are unchanged and that people who do not administer neither list nor read the
assets. Assert that the asset usage lists every skill version and agent that names the asset with the
version and hash it uses, leaves out an agent whose own instructions merely share the asset's id, keeps
naming the old version after a new one is published until it is saved, and refuses an id that is not an
asset id, on both providers. Save a change of a live data skill for shadow and
assert that an event starts the live version's run live and the candidate's run in the shadow thread
with its own version, rules and inputs, that a repeated delivery starts neither again, that the
comparison pairs the two, that promoting makes the candidate live without a second run, and that a
discarded candidate's queued run ends while its versions stay as history, on both providers. Assert that a `ShadowOf`
variant receives every event, tick and webhook of the live agent once, notifies nobody and executes nothing while its own triggers work; that the
evaluation pairs runs of the same event, counts agreed, live-only and variant-only proposals with the
live decisions and holds no message or argument; and that a replay fires the recorded inputs oldest
first, records the runs it replays, stops at the variant's budget and refuses a live agent and a
second replay, on both providers. Assert that a hand-off defined as data is saved only within
`UseHandOffsAsData` and as `AcceptHandOffs` checks code, that a stored one that no longer fits is out of
effect with its problem, that in effect it offers the generated tool and keeps the depth, scope, daily
limit and shadow-to-live rules, and that a data shadow variant receives the live agent's triggers in
shadow, cannot change its mode and is paired by the evaluation, on both providers; people who do not
administer neither read nor change them. Assert that a headless run with retrieved context retrieves once
with its inputs and subject, as the agent with the subject's scopes, on both providers. Assert that
a run with gateway reads records `DelegatedRead`, reads with its starter's rights when a person
started it, shows the result only to the person whose rights it used, also with `ElevatedReads.Deny`,
and that startup fails without an accountable person or an issuer. Search a subject type as a
member, an outsider and an approver and assert that only readable subjects of the caller's tenant
return, once each and within the limit, that a short query, an unknown type and a type without
search answer their codes, and that the catalog and the thread, proposal and work DTOs name the type
in the reader's language and link to the subject, on both providers; assert that startup refuses an
`nh-` type, two resolvers of one type and a configuration without its resolver. Declare what an
agent works on and assert, on both providers, that an event, a schedule tick, a manual start and a
hand-off on another type are refused with `assistant-agent-does-not-work-on` and audited without a
thread, that an agent created in the administration stores its statement, keeps it when an update
omits it, refuses unknown and reserved types, types next to all objects, nothing, per division without
divisions and unlisted policies at their fields, that its runs follow the statement, that a typed data
trigger is refused for an agent that does not work on its type and goes out of effect when the
statement narrows, and that the hints name the types of typed triggers, hand-offs and threads; assert
that startup refuses typed triggers, hand-offs and consultations on types the agent does not work on,
unknown types and unregistered policies, and that the endpoints answer `409` and `canStart: false`.
Tick a schedule without subjects and assert, on both providers, one run in the agent's thread of
`nh-system/global` and a deduplicated repeat in the same minute, one run per listed division in a
thread per division (scope `division:{id}`, its division on the background operation) within
`MaxSubjectsPerTick`, and refused starts of a per-division agent app-wide and of an app-wide agent in a
division; save a data schedule without a subject set for an agent that works on the application and
one that does not (`subjectSet: required`), and assert that narrowing the agent leaves it out of
effect. Assert the roles on the whole application (reader by the read policy, approver by the approve
policy, none without a principal or division access), that the policies are evaluated for the
rebuilt principal against the subject's division, that a system run's invocation context has no
subject scope and only the division in a division, that the run catalog lists `nh-system` with
`IsSystem` and `Divisions`, and that startup lets the platform's own resolver through, refuses an
application `nh-` type, per division without a division directory, a schedule without subjects for an
agent that does not work on the application and unregistered system policies. Over HTTP on both
providers, assert that the run policy decides who runs an agent for the whole application (not the
approve policy, the administration policy without one), that the agent's statement and scope refuse
starts with `409`, that a person without access to a division does not see it, that a reader of an
object may not start, and that `GET subjects/{type}/{id}/agents` lists only agents that work on the
subject and that the caller may use, with the newest run, the first 300 characters of its result and
the open, unexpired proposals. Assert on both providers that a message to a busy agent thread, a
restart and the request tool create no run and no waiting input for an approver of the whole
application who fails the run policy, a person who fails the required policy or a participant of an
agent in shadow, and that someone who may start the agent gets the run. Assert that a run started by
hand that acts for the accountable account
leaves a personal MCP server out while a scheduled run and the accountable person's own start use
that person's connection. Assert that the generator emits a declared subject, keeps the contract hash of tools
without one and refuses invalid declarations. On both providers, run an agent for the whole application
whose rules propose a tool with a declared subject for other objects and assert that each proposal
waits deferred in the agent's thread at its object with the run, the origin, the object's version and
execution scope, that the run's thread keeps the call as placed, that a type the agent does not work on,
a missing object, another tenant, another division for a per-division agent, an account without read
access and the run's limit are refused with their reasons without failing the run, that a call about
the run's own object and a tool without a declared subject stay in the run's thread, that a run in
shadow places shadow proposals in the shadow thread, that duplicates are refused, that the object's
approver (not the run's initiator, not a reader) decides it and that it executes in the object's scope
with the agent's account; assert that the inbox and the thread list it with `PlacedBy`, and that the
proposals of the whole application reach its approvers in the inbox, the subject summary and the
notifications, and that a placement a later check refuses, such as a duplicate, leaves no thread at the
object. On both providers, execute a run whose model answers more than 20,000 characters and assert the
bounded result with `ResultTruncated` next to the small summary, that a rule run keeps its rules'
summary and that a run of two turns keeps both answers; over HTTP assert that `GET runs` shows the runs
of objects the caller reads and of the whole application by the agent's view policy (by default its
required policy), never another object's or shadow work, that `GET runs/{id}` answers `404` for a run
the caller may not see, lists the proposals placed at objects the caller reads and counts the others,
and serves the run's own files, that `GET admin/agents/{id}/runs` lists every run with shadow marked
for administrators only, and that the filters, the paging and `assistant-run-filter-invalid` behave the
same on SQL Server and PostgreSQL. On both providers end runs started by an event, a schedule, a person
and a trigger defined as data, a shadow variant's run, a run in a skill defined as data and a run that
fails after a retry, and assert that each `AgentRunEnded` carries the stored trigger kind, shadow, skill
version, model calls (those of both attempts, 0 for rules), status and failure code, that the model
calls equal the run's usage records, and that runs, runs without a model call and shadow runs per agent
and skill follow from the events alone. Assert that the migrations leave no pending model changes. On both
providers assert that an agent's thread of the whole application, its conversation, proposals, runs and
files answer `404` to a reader of the whole application who does not pass the agent's view policy, also
after an invitation, that such a person neither finds the agent in the subject's work nor starts it,
that an approver without the view policy neither finds its proposals in the inbox or the summary nor
decides them (`404`) while one with it does, and that operation status, notifications and live updates
reach only people who pass it. Assert that a start by hand answers `waiting` for a paused agent and
`409 assistant-trigger-rate-limited` over its daily limit, that an invalid run filter names its
parameter in `errors`, and that board visit retention removes old visits and visits of unregistered
groups. For groups, assert the startup checks
(unknown group, names and descriptions, a group registered twice), that `PUT admin/agents/{id}/groups`
stores only choices that differ from code and that they win over `InGroup`, that `GET boards` and `GET
boards/{group}` show the agents by their view or required policy, that `IsNew` follows the visit per
person and per scope (the whole application and a division), that open proposals count the proposals a
run placed at objects, and that a board of another division or an unknown group answers
`assistant-board-not-found`. For the overview of an agent, assert that `GET admin/tools` names the
type of a tool with `[NhAiSubject]` and `null` for one without and for MCP tools, that a schedule in
code without subjects reports `nh-system` and an untyped trigger `null`, and that an agent in code
reports its accountability, approval policy and rules while an agent created as data reports none.
For `UseSingleTenant`, on both providers assert that a person projected into the tenant finds the whole
application's proposal in the inbox and decides it, through the endpoint and the decision service, sees
the agent on its board, in the run history, its thread and the subject's work, and that the approver runs
it now; that a caller of another tenant and a person without the agent's view policy see nothing; that
without the mode the same people neither see the work nor decide, resolve, start, open or read it by id
(also through the decision service), while an operator without a tenant who passes the policies does;
and that startup refuses another fixed tenant or a tenant claim.
For the tenant of objects, on both providers sign one person in for two tenants with the same actor id
and a role on a project of one of them, and assert that for the other tenant every route by id, every
decision, resolution and feedback, the decision service, the conversation route, the routes about a
subject, the inbox, the counts and the consult and request tools answer as for an unknown thread,
proposal or subject, write no participant row, change no run or proposal and start nothing, while the
project's own tenant keeps working; and that forgetting a subject with its scope key leaves the threads,
runs, proposals and member snapshot of an equal id in another tenant. `AssistantTenantBindingTests` is
the reference.
For a tool the application executes, on both providers assert that an approved proposal is handed over
with its payload, hash, approver and deadline and waits as `awaiting-application` without running the
tool's function, that each outcome closes it with its status and code, that the audit carries the outcome
and code but never the summary, that the summary reaches the thread without the trusted flag, that the
same report again is a duplicate and a late one is refused, that a report of another tenant reads as
unknown, that the deadline makes it `execution-unknown` and `hand-to-application` hands it over again
with a higher attempt, and that a host that declares such a tool without an executor does not start.
For trigger versions, on both providers assert that a change with `apply: shadow` keeps the live
version firing unchanged while the candidate fires into the shadow thread with its own filters, that
an event the live version filters out starts only the candidate and one both select starts both,
that a candidate's run records `TriggerVersion` and `TriggerCandidate` and `AgentRunEnded` carries
the version, that a deferred fire and a data schedule fire the candidate too, that the comparison
counts both versions and their paired events, that promote and discard check the candidate's
version and keep every version as history, also after the trigger is removed, and that the
administration endpoints refuse people who do not administer and stay within the caller's tenant.
Assert that an agent created with `StartsInShadow` and run settings and a trigger in the same request
runs its first event in its shadow thread, and that a consultation saved with `StartsOff` or under a
ceiling with `StartOff()` offers no tool until `SetConsultationStateAsync` switches it on.
For agent packages, on both providers, export agents and import the package on another host to the same
parts and hashes, assert every outcome of the preview, that a refused part refuses the whole package and
stores nothing, also when a write fails inside the transaction, that missing references are refused with
the save validators' codes, that agents of one package refer to each other in a cycle, that the technical
account is required, must be able to sign in and never travels, that `Apply = "shadow"` makes candidates a
promote puts live, that every save path refuses a locked field and the removal or deletion of a locked part
while operations stay free, that an unlinked part stays `kept-local` until `Relink` names it, that an agent
in code takes the overlay, that the format is read strictly, and that the endpoints refuse people who do
not administer. Assert that `GetPackageAsync` returns the imported version, hash and links, shows a part
changed on the host as changed locally and still returns the package after every agent was unlinked,
and that `ReadAsync` equals the parts and hashes of an export of the same agents and writes no audit.
SPM-321 and SPM-331 are the executable references. Over HTTP assert that `POST admin/agent-packages/read`
and `GET admin/agent-packages/{packageId}` answer the same, refuse people who do not administer and audit
nothing (SPM-353). For a promotion with `UncarriedParts = Relink`, assert that the uncarried parts keep
their locks and the hash of their link, are reported `kept-linked` and are held by the new package version
(SPM-352). For `ProposeCandidatesAsync`, assert a candidate per live skill on an older version, a skill a
package locks reported `locked` while the others are proposed, `already-proposed` on a repeat and that a
refused call saves and audits nothing (SPM-351).
For proposals on their way, on both providers, assert that `GET approvals/inbox` stays `pending` by
default, that `awaiting-application` lists by report deadline only until the report falls due, flat and
grouped, that `execution-unknown` lists by decision, that another status is `400`, that the initiator of
a run and the whole application's live approvers see exactly what they see for pending proposals, and
that `INhAssistantProposalCounts` returns the same totals per status and zero for a tenant that takes no
part in the whole application. For the members of the whole application assert that an unopened thread
notifies nobody without `Members<T>()`, that with it the listed approvers are notified and listed
readers, people without a role and unusable ids are not, that a division counts only people who work
there and that a list is read up to its bound. For `AlsoInline` assert the hand-off of a chat approval,
the turn's tool result, the report posted into that chat without the trusted flag, the deadline and a
refused executor, that the report, the uncertain post and the deadline post read in the culture of the
turn that approved it, title included, that an approval without a supported culture reads in the default
one and that an agent thread keeps the default, that only the person who approved resolves an uncertain chat proposal, that a
proposal in another status is refused and that handing it again starts a new attempt with a new deadline,
and that without it the inline approval still runs the function. For the decision culture assert on
both providers that a decision through the service with `NhAssistantDecidingPerson.Culture`, through
the endpoints in the request culture and an inline approval in the turn's culture reach
`NhAssistantProposalHandOff.DecisionCulture` and `NhAssistantExecutionReportReceipt.DecisionCulture`,
that another culture of a supported language records the supported one and that an unsupported
language records none while the decision stands (SPM-348). For
`MaxConsultationsPerTurn` assert the refusal beyond the limit before the consulted agent runs, the
default without a limit and an agent's own limit. SPM-327 and SPM-328 are the executable references.
On both providers, override the required policy of an agent in code to one an asker lacks and assert
that its consult and request tools, in code and as data without an asker policy, refuse that asker
with `assistant-agent-forbidden` and leave no consultation, run or input, that someone who passes the
override is served, and that a consultation with its own asker policy keeps admitting its askers.
Assert that an agent of the whole application with a `ViewPolicy` refuses the consultation to a reader
of the whole application who fails it, with `assistant-consult-subject-unavailable`, before a
consultation is recorded or its model runs, and serves someone who passes it. Run a delegating agent
whose new data trigger is still in shadow and assert that its consult call of a live agent is refused,
that its request call is a shadow proposal of the asking run, and that the live agent gets no
consultation, thread, proposal or run.
For definitions from code, on both providers, update an agent created at runtime at its version and
with a stale one, assert that its MCP assignments stay unless named, that a field a package locks is
refused, that an agent in code is overridden, reset and never deleted, and that the directory returns an
agent in code, an overridden one, an agent with an asset and with its problem, and stopped and switched
off agents as in effect. For `IndexByEach`, assert normalization, the bound of 50 values, any-value
matches of stored values and values in code, replacement on a change, removal with the agent and the
values stored before the entry declared them. For a promotion, assert the fields an agent in code and
one created at runtime take, moved skills, the variant's removal, the refusals before anything changes
and the endpoint's refusal of people who do not administer. SPM-329, SPM-330 and SPM-313 are the
executable references.
SPM-268 to SPM-280, SPM-282 to SPM-288, SPM-290 to SPM-297, SPM-301 to SPM-304, SPM-310, SPM-314,
SPM-316 and SPM-317 are the executable references.
