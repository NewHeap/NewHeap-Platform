---
id: nh-ai-agent-runs
title: "Run assistant agents on their own from events, with subject threads, deferred proposals, consultations, proposal feedback, revalidation and rule runs"
area: backend
reference: ai-agent-runs
summary: "Start agents from committed application events or from people, run them headless as background operations, keep one thread per agent and subject whose access follows the application's own roles, let people decide the agent's proposals later under a named approval policy, let other assistants consult an agent with the asking person's rights, learn from why people reject its proposals, close proposals that people overtook by hand, run agents by fixed rules without a model, never pile up the same proposal, pause, stop and resume agents from code and the administration, brief agents about their subject within a token ceiling per run, let one agent act as a role with skills, let agents hand work to each other, evaluate shadow variants against the live agent, put an agent or a skill in shadow and change its technical account at runtime, add skills and triggers to an agent as data without a deploy, let agents created in the administration run with the rights of the account they answer to, say what every agent works on, run agents for the whole application, app-wide or per division, place a run's proposals at the objects they concern, and keep every run's result with a history that follows the access of what it is about."
sample-cases: ["SPM-268", "SPM-269", "SPM-270", "SPM-271", "SPM-272", "SPM-273", "SPM-274", "SPM-275", "SPM-276", "SPM-277", "SPM-278", "SPM-279", "SPM-280", "SPM-282", "SPM-283", "SPM-284", "SPM-285", "SPM-286", "SPM-287", "SPM-288", "SPM-291", "SPM-290", "SPM-292", "SPM-293", "SPM-294", "SPM-295", "SPM-296", "SPM-297", "SPM-298", "SPM-301", "SPM-302", "SPM-303", "SPM-304"]
public-symbols: ["ConfigureAgent", "NhAssistantAgentRunBuilder", "NhAssistantAgentRunOptions", "NhAssistantAgentBudget", "AddApprovalPolicy", "NhAiApprovalPolicyBuilder", "NhAiApprovalPolicy", "NhAiApprovalMode", "NhAiApprovalExpiryAction", "NhAiApprovalBinding", "NhAiApprovers", "NhAiApproverRule", "AddSubjectType", "INhAiSubjectAccessResolver", "INhAiSubjectAccess", "NhAiSubjectRef", "NhAiSubjectInfo", "NhAiSubjectAccess", "NhAiSubjectMember", "NhAiSubjectRole", "AddTrigger", "NhAssistantTriggerBuilder", "NhAssistantTriggerConflict", "NhAssistantTriggerIds", "INhAssistantAgentTriggers", "NhAssistantTriggerReceipt", "NhAssistantTriggerOutcomes", "NhAssistantTriggerReasons", "AddAssistantAgentRuns", "NhAiAgentAccountability", "NhAiGrantSource", "NhAiAgentGrant", "NhAiElevatedReads", "NhAiAgentInvocationScope", "NhAiAgentInvocationScopes", "NhAiAgentFailureCodes", "INhAiAgentToolAuthorizer", "INhAiProposalPrecondition", "NhAiProposalPreconditionContext", "NhAiPreconditionResult", "INhAiPrincipalFactory", "NhAiSubjectAuthorizationResource", "NhAssistantRunStatuses", "NhAssistantApprovalModes", "AddScheduledTrigger", "NhAssistantScheduleBuilder", "NhAssistantScheduledTick", "AddWebhookTrigger", "NhAssistantWebhookBuilder", "NhAssistantWebhookDelivery", "NhAssistantKillSwitchDto", "NhAssistantKillSwitchRequest", "INhAssistantAgentNotificationPolicy", "INhAssistantAgentNotificationChannel", "NhAssistantAgentNotificationContext", "NhAssistantAgentNotificationKind", "NhAssistantDefaultAgentNotificationPolicy", "UseAgentNotificationPolicy", "AddAgentNotificationChannel", "INhAssistantAgentMemoryMigrator", "UseAgentMemoryMigrator", "MaxRunSummaries", "MaxArtifactBytes", "INhAiArtifacts", "NhAiArtifact", "NhAiArtifactInfo", "INhAiSubjectRetention", "ForgetSubjectAsync", "NhAssistantThreadMemoryDto", "NhAssistantArtifactDto", "NhAssistantProposalDto", "INhAssistantToolPresenter", "NhAssistantConversationThreadDto", "NhAssistantThreadDecisionDto", "NhAssistantConversationKinds", "NhAssistantMessageAuthorKinds", "AllowConsultation", "NhAssistantConsultationBuilder", "NhAssistantConsultationOptions", "NhAssistantConsultationTools", "NhAssistantConsultationAnswer", "NhAssistantAgentRequestReceipt", "NhAssistantConsultationStatuses", "NhAssistantConsultationDto", "NhAssistantConsultationListDto", "MaxConsultationDepth", "MaxConsultationsPerActorPerDay", "RequireApprovalForEffects", "NhAssistantProposalFeedbackCategories", "NhAssistantProposalFeedbackGroups", "NhAssistantFeedbackTargetKinds", "INhAssistantProposalFeedbackSink", "NhAssistantProposalFeedback", "AddProposalFeedbackSink", "MaxRejectionsInContext", "RejectionContextWindow", "MaxFeedbackCharacters", "NhAssistantProposalFeedbackRequest", "NhAssistantBulkProposalFeedbackRequest", "NhAssistantProposalFeedbackDto", "NhAssistantBulkProposalFeedbackDto", "NhAssistantFeedbackOutcomeDto", "NhAssistantFeedbackSummaryDto", "NhAssistantFeedbackSummaryRowDto", "NhAssistantFeedbackCategoryCountDto", "NhAssistantFeedbackListDto", "NhAssistantFeedbackItemDto", "NhAssistantFeedbackExportRecord", "NhAssistantLiveAgentWorkDto", "NhAssistantToolCallStatuses", "INhAssistantSubjectWork", "NhAssistantSubjectWorkExtensions", "NhAssistantSubjectWorkDto", "NhAssistantSubjectAgentDto", "NhAssistantSubjectProposalDto", "NhAssistantSubjectSummaryRequest", "NhAssistantSubjectSummaryDto", "NhAssistantSubjectSummaryListDto", "NhAssistantOpenThreadRequest", "INhAiProposalGuard", "NhAiProposalGuardContext", "NhAiProposalKey", "NhAiProposalGuardStages", "NhAiStaleKind", "AddProposalGuard", "AddProposalRevalidation", "NhAssistantRevalidationBuilder", "INhAssistantProposalRevalidation", "NhAssistantRevalidationResult", "NhAssistantInvalidatedProposal", "NhAssistantStaleKinds", "NhAssistantInvalidationStages", "NhAssistantRevalidateProposalsRequest", "NhAssistantInvalidationCountDto", "INhAssistantAgentRuleHandler", "NhAssistantRuleContext", "NhAssistantRuleResult", "NhAssistantRuleInput", "NhAssistantRuleProposal", "NhAssistantRuleDecision", "NhAssistantProposeOutcome", "UseRuleHandler", "UseModel", "NhAssistantRunKinds", "NhAssistantRunDto", "NhAiDuplicateProposals", "RejectionCooldown", "MaxRejectionCooldown", "MaxInputPayloadCharacters", "NhAssistantProposalObjectDto", "NhAssistantProposalLookupRequest", "NhAssistantProposalLookupDto", "INhAssistantAgentControl", "NhAssistantAgentState", "NhAssistantAgentStateRequest", "NhAssistantAgentRunStates", "MaxStateReasonCharacters", "INhAssistantSubjectBriefingProvider", "NhAssistantBriefingContext", "NhAssistantBriefingPurposes", "NhAssistantSubjectBriefing", "NhAssistantBriefingSection", "AddSubjectBriefing", "MaxBriefingCharacters", "BriefingTimeout", "MaxTokensPerRun", "Skill", "NhAssistantSkillBuilder", "NhAssistantSkillOptions", "Triage", "NhAssistantTriageBuilder", "NhAssistantSkillChoosers", "UseSkill", "PauseSkillAsync", "StopSkillAsync", "ResumeSkillAsync", "NhAssistantSkillState", "AcceptHandOffs", "NhAssistantHandOffBuilder", "NhAssistantHandOffOptions", "NhAssistantHandOffTools", "HandOffAsync", "NhAssistantHandOffReceipt", "NhAssistantHandOffOutcomes", "NhAssistantHandOffResult", "MaxHandOffDepth", "ShadowOf", "NhAssistantEvaluationDto", "NhAssistantEvaluationRunsDto", "NhAssistantEvaluationToolDto", "NhAssistantEvaluationDecisionsDto", "NhAssistantEvaluationCostDto", "NhAssistantEvaluationRoutingDto", "NhAssistantEvaluationDifferenceDto", "NhAssistantEvaluationDifferenceKinds", "NhAssistantEvaluationExportRecord", "NhAssistantReplayRequest", "NhAssistantReplayDto", "NhAssistantReplayStatuses", "NhAssistantEvaluationLimits", "INhAiAgentInvocationContextFactory", "AllowGatewayReads", "SetShadowAsync", "SetSkillShadowAsync", "SetServiceAccountAsync", "NhAssistantShadowState", "NhAssistantServiceAccountState", "NhAssistantShadowRequest", "NhAssistantServiceAccountRequest", "NhAssistantShadowChoices", "AddTriggerEvent", "NhAssistantTriggerEventBuilder", "AddSubjectSet", "AddRuleHandler", "INhAssistantAgentConfiguration", "NhAssistantRunCatalogDto", "NhAssistantRunBlockDto", "NhAssistantAgentConfigurationDto", "NhAssistantCodeSkillDto", "NhAssistantCodeTriggerDto", "NhAssistantDataSkillDto", "NhAssistantDataSkillVersionDto", "NhAssistantDataTriggerDto", "NhAssistantDataSkillRequest", "NhAssistantDataTriggerRequest", "NhAssistantRunConfigurationProblem", "NhAssistantRunConfigurationProblems", "NhAssistantTriggerConflictNames", "NhAssistantAgentConfigurationLimits", "NhAssistantSkillDefinitionStatuses", "NhAssistantDataTriggerSources", "ActWithAccountableRights", "NhAssistantAgentRunSettingsDto", "NhAssistantAgentRunSettingsRequest", "NhAssistantElevatedReadNames", "NhAssistantSkillApplyModes", "NhAssistantSkillCandidateRequest", "NhAssistantSkillComparisonDto", "NhAssistantSkillVersionStatsDto", "NhAssistantSkillComparisonToolDto", "PromoteSkillCandidateAsync", "DiscardSkillCandidateAsync", "CompareSkillCandidateAsync", "HideFromChat", "CreateAgentAsync", "NhAssistantAgentRequest", "NhAssistantApprovalPolicySummaryDto", "NhAssistantApproverSummaryDto", "NhAssistantAccountDto", "NhAssistantSubjectTypeBuilder", "INhAiSubjectSearch", "NhAiSubjectSearchRequest", "NhAssistantSubjectTypeDto", "NhAssistantSubjectSearchDto", "NhAssistantSubjectSearchResultDto", "NhAssistantAgentWorksOn", "WorksOn", "WorksOnAllObjects", "WorksOnApplication", "PerDivision", "RunPolicy", "ViewPolicy", "AddAgentPolicy", "NhAiSubjectRef.SystemType", "NhAssistantAdminAgentInputDto", "NhAssistantAdminAgentDto", "NhAssistantRunBlockDto", "NhAiSystemSubjects", "UseSystemSubject", "NhAssistantSystemSubjectBuilder", "NhAssistantObjectAgentsDto", "NhAssistantObjectAgentDto", "NhAssistantLatestRunDto", "NhAiSubjectAttribute", "NhAiToolSubject", "MaxPlacedProposalsPerRun", "NhAssistantPlacementReasons", "NhAssistantProposalOriginDto", "MaxRunResultCharacters", "NhAssistantRunHistoryDto", "NhAssistantRunHistoryListDto", "NhAssistantRunResultDto"]
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
initiator may use. Keep `NhAiElevatedReads.Restricted` unless the results of
granted reads may be shown to everyone who sees the thread.

Give every agent action that changes state a named approval policy with
`AddApprovalPolicy` and `UseApprovalPolicy`. `NhAiApprovalMode.Deferred`
records the proposal, gives the model a synthetic "recorded" result and lets the
run end; people decide in the thread or in `GET approvals/inbox` later.
`NhAiApprovalMode.Inline` suspends the headless run until the decision. Approvers
are subject roles (`NhAiApprovers.SubjectRole`), the initiator or a policy;
`FourEyes()` keeps the initiator out, `RequireUnchangedSubject()` closes a
proposal as stale when the subject changed before execution, and every approver
must also satisfy the tool's approval policy. Approved proposals execute as their
own background operation; a non-idempotent tool runs at most once, and an
execution whose outcome is unknown waits for a person in `resolve`.

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
opened the thread. An update that names a run, about the run or one of its proposals, also
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
data in its endpoints with the same access checks. Refresh such pages on `AgentWorkChanged`,
which names the subject.

Describe each kind of subject with one `INhAiSubjectAccessResolver` registered
through `AddSubjectType`. `DescribeAsync` returns the scope key, division,
version and responsible person; `ResolveAsync` returns the live role of one
person and is called on every request; `ListMembersAsync` feeds the member
snapshot that listings, notifications and live updates use. Both also run inside
background operations, for notifications and live updates, so keep them free of
request-only state. Call `INhAiSubjectAccess.RefreshAsync` after the people on a
subject change.

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
and those who decided its proposals, at most 50, each with the role the resolver gives them now. With
listed divisions `GET subjects/nh-system/search` finds the
divisions a person works in by name (`Searchable` in the run catalog), as the subjects of a
per-division agent.

Run an agent now, by hand, with `POST threads/start` (`agentId`, `subjectType`, `subjectId`, optional
`message`; the caller is the initiator and the trigger `manual`). One rule decides who may, also for a
new thread of `POST threads/open` and every `canStart`: the agent works on the subject and, for the
whole application, on its scope (`409 assistant-agent-does-not-work-on`), the person passes the
agent's required policy, and on an object the person is a participant; for the whole application
(`nh-system/global`, or `nh-system/division-{id:N}` for a per-division agent) the agent's `RunPolicy`
decides, by default the administration policy, evaluated like the roles of the whole application
(for the rebuilt principal, against the subject with its division, with access to the division), and
the person reads it. The approve policy of the whole application gives no start. An agent in shadow is
started by administrators only. `GET subjects/{type}/{id}/agents` (`NhAssistantObjectAgentsDto`) lists
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
(`NhAssistantPlacementReasons`: `does-not-work-on`, `not-found`, `no-access`, `limit`), a rule handler
reads `NhAssistantRuleProposal.PlacementReason`, and the audit records `AgentProposalNotPlaced`; a
placed one answers with the object, `NhAssistantRuleProposal.PlacedAt` names it, and the audit records
`ApprovalRequested` and `AgentProposalPlaced` about the object. Proposal DTOs name the run with
`PlacedBy` (`NhAssistantProposalOriginDto`: its thread, the run and its subject type; the subject's id
and name only for the whole application, which every reader may know). When the run ends, also after a
failure, the approvers of each object are notified once per object, and the run's summary counts its
placed proposals per tool and status. The migration `AgentPlacedProposals` adds the column that marks a
placed proposal.

Read what runs did from their results and history (K13, K14). A run keeps its whole answer as Markdown
in `AssistantRun.Result`, at most `NhAssistantLimits.MaxRunResultCharacters` (20,000) characters with
`ResultTruncated` when it was cut: the final answer of each of its turns that completed, separated by a
thematic break, the rules' summary of a rule run included, while `NhAssistantRunSummaries` keeps writing
the small summary the agent's next runs start from. Files the run saved stay artifacts. The result is
model output, MCP tool output included: render it only with a safe Markdown renderer, never as HTML.
It lives with the run, so it follows the thread's retention. `GET runs` lists for people the runs they
may see (`NhAssistantRunHistoryListDto` of `NhAssistantRunHistoryDto`: agent and its name, subject,
trigger, status, kind, skill, usage, times and the first 300 characters of the result), newest first,
with the filters `agentId`, `subjectType` (`nh-system` for the whole application), `subjectId`,
`status`, `from` and `to` (creation, from inclusive, to exclusive) and `page` and `itemsPerPage` (at most
50); an invalid filter answers `400 assistant-run-filter-invalid`. A run on an object is visible to
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

Start agents from application events with `AddTrigger<TEvent>` and
`INhAssistantAgentTriggers.PublishAsync`, and set `Dedupe` to the event id or to the
changed row and its version, so an at-least-once delivery starts one run. Publish
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
once. The assistant's own endpoints, schedules, webhooks and follow-ups always fire
at once. Maintenance queues a run again whose operation never committed. Choose
`NhAssistantTriggerConflict` for events that arrive while a run is active:
coalesce, queue, supersede or ignore. Bound agents with `Budget`, `Retry`,
`Enabled`, and `Shadow()` on the agent or a skill to start it in shadow for evaluation on real
events: its work goes to the agent's shadow thread of the subject, which administrators alone see,
where nobody is notified and nothing proposed executes. Call `AddAssistantAgentRuns()` inside
`WithBackgroundOperations`.

Other sources start agents too. `AddScheduledTrigger` runs a Hangfire recurring job on
a cron schedule and fires the trigger for each subject its `Subjects` query returns,
at most `MaxSubjectsPerTick`; a repeated job in the same minute starts nothing twice.
`AddWebhookTrigger` exposes `POST {prefix}/triggers/{id}` to an external system without
a user: the caller signs `{timestamp}.{deliveryId}.{body}` with HMAC-SHA256 and a shared
secret of at least 32 characters read for every call, calls more than five minutes off
are refused, and a delivery id starts at most one run. Treat the body as untrusted and
take only identifiers and known values from it. A follow-up run after a decided or
executed proposal is opt-in per approval policy (`StartFollowUpRun`). It continues in the skill of
the run that made the proposal (`skillChosenBy` `follow-up`) while the agent declares that skill:
a paused skill lets it wait and a stopped one refuses it.

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
stopped state. The kill switch, `POST admin/agents/{id}/kill-switch`, is stop and resume
without a reason. A state change never changes the agent's definition or version; editing
and resetting never change the state, and `isEnabled` in the definition is ignored. An
agent disabled in code with `Enabled(false)` reads `configured-off` and cannot be resumed;
with `NewHeap:AI:Assistant:Enabled` off, triggers, schedules, runs and execution queuing
stop as well. A stopped or code-disabled agent's triggers evaluate no lambda.

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
hand, and consultations and requests are refused. A shadow variant always works in shadow
(`assistant-shadow-variant`). `SetServiceAccountAsync(agentId, actorId | null)` lets another
technical account answer for an agent that answers to one in code (`ServiceAccount(actorId)`
stays the default; `assistant-service-account-not-used` otherwise). The account must exist and be
allowed to sign in according to `INhAiPrincipalFactory` (`assistant-service-account-not-found`).
New runs and the agent's threads answer to it; runs that started and the proposals they made keep
the account they were made with. The account lends its rights to gateway reads: choose one with
no more rights than the agent needs.

Add skills and triggers to an agent configured in code as data, without a deploy, when an
application manages them in screens or imports them from packages. Code registers the building
blocks under dash-case names joined by dots: `AddTriggerEvent<TEvent>(name, ...)` for an event a
data trigger can start a skill on (its `Subject`, `Message`, `Initiator`, `Dedupe`, `Payload` and
`When` are what every trigger on it receives; one published event serves code and data triggers
alike), `AddSubjectSet(name, description, subjects)` for the subjects a data schedule starts a
skill for, and `AddRuleHandler<THandler>(name, description)` for rules a data skill works by.
Approval policies, model profiles and subject types are named by their ids as in code.
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
and shown as a problem in `GET admin/agents/{id}/configuration` instead of failing startup.

An agent created in the administration gets runs through run settings as data:
`INhAssistantAgentConfiguration.SaveRunSettingsAsync` or `PUT admin/agents/{id}/run-settings` sets
whom it answers to (`agent`, `subject-responsible`, or `service-account` with an account that exists
and may sign in), its approval policy, budget, run ceiling, retries, gateway reads, triage profile and
rule handler by name. Grants never become data: in headless runs such an agent acts with the rights
of the person or technical account it answers to, within its tool selectors. A tool is allowed when
that account, rebuilt with `INhAiPrincipalFactory` for every decision, passes the tool's policies
against the subject; the call, proposal and audit record `NhAiGrantSource.Accountable`, reads are
shielded from thread readers without the tool's policies (or refused with `ElevatedReads` `deny`),
and an agent that answers to itself has no rights to lend. A person's turn in the thread and a
consultation keep that person's own rights. An agent in code opts in with
`ActWithAccountableRights()`; startup refuses it for an agent that answers to itself. Once it has run
settings, the agent gets data skills and data triggers like an agent in code; its threads follow a
change of whom it answers to, and its run settings stay while data skills or triggers belong to it
(`assistant-run-settings-in-use`). An agent in code cannot get run settings as data
(`assistant-run-settings-in-code`). Hosts need `AddAssistantAgentRuns`, as for agents in code.

An application that imports agents, for example from a package, creates one the way an
administrator does with `INhAssistantAgentConfiguration.CreateAgentAsync(NhAssistantAgentRequest)`
and then gives it run settings, skills and triggers as above; an existing id fails with
`assistant-agent-exists`. Keep agents that only work on their own out of the chat: in code with
`ConfigureAgent(id, run => run.HideFromChat())`, for an agent created in the administration with
`offeredInChat: false` (`OfferedInChat` on the request). Such an agent is left out of the agent picker
and `POST conversations` answers `assistant-agent-not-found` for it; its triggers, runs, subject
threads, consultations and existing conversations keep working. The run catalog explains its
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
recipients from the subject's member snapshot, by default its approvers. Triggers,
ended runs, executions, resolutions, pauses, stops and resumes are content-free business
audit events; the reason of a state change stays with the state.

Brief the model about its subject with `AddSubjectBriefing<TProvider>()`, one
`INhAssistantSubjectBriefingProvider` per subject type. Every model turn about the subject reads
the briefing after its instructions, as data: headless runs (with the run's events as
`Inputs`), people's turns in the thread and consultations; rule handlers read data themselves and
get none. Return a few `NhAssistantBriefingSection`s with facts and their dates, most important
first, from the same domain checks the agent's rules use, so rules signal and the model decides;
a rule run that hands over gives the model its reason as the briefing's first section. Briefings
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
to add execution scopes of your own. The same rule as for briefings applies: what a run retrieves
may reach every reader of the thread through the model's text.

Let a headless run read application data through the API bridge gateway with
`AllowGatewayReads()` and tool selectors that select the gateway's tools (see `nh-ai-api-bridge`
and `AddNewHeapPlatformAIDelegatedReads`). A run that a person started reads with that person's
own rights, so asking an agent never lends someone else's; a run from a trigger reads with its
accountable person or service account. Startup fails for an agent that answers to itself, without
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
target's summary only when both worked on the same subject and the target read nothing restricted
with its own grant.

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
`INhAiSubjectRetention.ForgetSubjectAsync` after the application deleted a subject, so
its threads, runs, memory and files go with it.

Let other assistants consult an agent with `AllowConsultation(subjectType, ...)` on its
`ConfigureAgent`. It generates the governed tool `assistant.consult.{agentId}.{subjectType}`
and, with `AllowRequests()`, `assistant.request.{agentId}.{subjectType}`; the asking agent
uses them only when its own `ToolSelectors` select them (use
`NhAssistantConsultationTools.ConsultToolId`), so both sides opt in. Neither tool can be
granted to an agent: a consultation always runs with the asking person's rights, the
person chatting or the initiator who delegated an agent turn. Give it a `Description` the
asking model can choose by, a `Policy` the asker must pass (otherwise the agent's
required policy or the assistant's access policy), and keep the defaults unless the
subject needs others: `MinimumRole(Reader)` to ask, `Proposals(true, Participant)` before
tools that change anything are offered, `Limits(4 calls, 60 seconds, 4,000 characters)`
and `Exposure(Local | Agent)`; add `NhAiToolExposure.Mcp` only when MCP clients may run
consultations. A consult answers in the same turn: the agent reads its working memory,
earlier summaries, recent thread history and the artifacts the asker may read, with its
tools under the asker's rights only. Anything it would change becomes a deferred proposal
in its thread with `consultationId`, decided by the subject's approvers and executed with
the asker's rights, never with the agent's grants; `RequireApprovalForEffects` on the
agent scope enforces that in the tool invoker. The consult writes no message into the
thread; `GET threads/{id}/consultations` lists who asked through which assistant, without
the question or the answer. A request starts an ordinary run with the asker as initiator
(`NhAssistantTriggerIds.AgentRequest`, participant role) and notifies the initiator with
`RequestAnswered`. An application whose `INhAiToolDiscoveryPolicy` lists tool prefixes
must allow `assistant.consult.` and `assistant.request.`. Bound consultations with
`NhAssistantLimits.MaxConsultationDepth` (1 by default; an agent already in the chain is
never asked again) and `MaxConsultationsPerActorPerDay`; they also spend the consulted
agent's daily `Budget`. Make `INhAiSubjectAccessResolver` accept only the canonical id of a
subject, because the asking model types the id. Unknown subjects and subjects the asker
may not read both return `assistant-consult-subject-unavailable`.

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
whose arguments do not fit, is `Refused`. The rationale is rendered in every supported culture;
approvers read it as `rationale` on the proposal and its approval part, and it ends the rule's
message in the thread. Finish with `NhAssistantRuleResult.Done(summary)`, or hand an unclear case
over with `HandOver(reason)`: the same run continues with the model, which reads the reason and the
proposals already made, and reads `rule-then-model`. `UseModel()` on a trigger sends its runs to the
model. A person's message in the thread and a consultation always reach the model, so keep a model
profile and instructions that answer questions about the rules' proposals from the thread.

## Avoid

- Stopping an agent by editing its definition or sending `isEnabled`; pause or stop it with
  `INhAssistantAgentControl` or `POST admin/agents/{id}/state`.
- Stopping an agent for an import or a migration that should lose no events; pause it.
- Deploying to put an agent or a skill live after an evaluation in shadow, or to replace its
  technical account; change it at runtime with `INhAssistantAgentControl`.
- Registering a skill or trigger in code that administrators are meant to change; register its
  building blocks and let them define it as data. Putting a data skill or trigger live without
  reading its shadow runs first, or putting a real change of a live skill live without evaluating it
  as a candidate.
- Letting an administration agent answer to a technical account with more rights than its work
  needs, or to itself when it must use tools: it then has no rights at all.
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
- Running an agent under a person's identity so that its actions look like that
  person's actions.
- Letting the agent decide or execute its own proposals, or approving them on a
  client-supplied hash instead of the server's proposal.
- Deriving who may decide a thread's proposal in the client from the subject role alone;
  four-eyes and the tool's approve policy are part of `thread.decisions`.
- Authorizing thread access from the member snapshot instead of the resolver.
- Publishing triggers inside a transaction on a DbContext that does not hold the
  background operations, or inside an ambient `TransactionScope`, which the
  background operations do not support; the platform cannot defer such a trigger.
- Reading the run or thread from the receipt of a trigger published inside a
  transaction; it reads `deferred` until the commit.
- Treating `execution-unknown` as failed and executing a non-idempotent tool
  again automatically.
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
- Building agent-to-agent calls by hand (for example a tool that calls
  `INhAssistantAgentTriggers` and waits, or an event published only to start another agent)
  instead of `AllowConsultation` or `AcceptHandOffs`, or raising `MaxConsultationDepth` or
  `MaxHandOffDepth` without a reason.
- Accepting hand-offs from every agent, letting a source wait inside its run for the target, or
  passing a target's results to the source beyond its summary.
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
- Showing a run's result from `NhAssistantLatestRunDto.Result`, `NhAssistantRunHistoryDto.Result` or
  `NhAssistantRunResultDto.Result` as HTML; it is model output and may carry MCP tool output.
- Listing runs for people straight from the database, or reading a run's answer from its summary; use
  `GET runs` and `GET runs/{id}`, which decide per object and per view policy who sees a run.
- Treating the view policy of an agent as the access to its thread for the whole application: it
  decides who sees its results in the histories; the thread itself follows the read policy of the whole
  application.

## Verification

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
consultation's proposal waits deferred and executes with the asker's rights only, that a
loop or a deeper chain is refused and that consultations count in the agent's daily
usage on both providers. Reject a proposal and assert that only its decider and the
subject's approvers may give feedback, that a pending or approved proposal refuses it,
that a second feedback replaces the first, that the next run's continuity lists the
rejection with the feedback on one line, that the report counts statuses, categories and
distinct authors per tool and agent version on both providers, and that forgetting the
subject deletes the feedback. Make a proposal in a headless run in one culture and read it
in each supported culture; close a deferred proposal by rejection and by expiry and assert
that its tool call reads `rejected` and `not-executed`; decide a proposal and assert that
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
thread's message reads `rule`; hand a run over and assert `rule-then-model`. Propose the same change
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
that reading does not join the thread, on both providers. Pause an agent and assert that
events wait once per dedupe key, start no run and skip schedule ticks, that a run queued before
the pause ends without claiming its inputs, and that resuming starts one run per thread; stop it
and assert cancelled runs, withdrawn proposals, dropped waiting events, refused triggers and held
executions that the resume queues, consultations included, behind more than a batch of queued
executions; assert that the version is unchanged, that editing and reset keep the state, that a
code-disabled agent reads `configured-off` and refuses to resume, that a stopped agent and a `When`
filter evaluate no further lambda and that the global switch stops triggers, schedules, runs and
execution queuing, on both providers. Assert that a headless run, a consultation and a person's
turn in the thread read the briefing once, before the continuity, with markup neutralized; that a
rule run that hands over passes its reason inside it; that a briefing over its bound is cut at a
section boundary with a note and that a throwing or slow provider leaves the turn without one; and
that a run at its ceiling fails with `assistant-run-token-limit`, keeps its proposals and finishes
after a resume, on both providers. Assert that a trigger with a skill runs that skill and records
it, that rules and triage route to a skill with only its tools, instructions and profile, that
triage can end a run with nothing to do, that `use_skill` switches at most twice and that a person's
turn starts without a skill, that a stopped skill refuses its triggers before any lambda while the
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
without the tool's policies, on both providers. Save a change of a live data skill for shadow and
assert that an event starts the live version's run live and the candidate's run in the shadow thread
with its own version, rules and inputs, that a repeated delivery starts neither again, that the
comparison pairs the two, that promoting makes the candidate live without a second run, and that a
discarded candidate's queued run ends while its versions stay as history, on both providers. Assert that a `ShadowOf`
variant receives every event, tick and webhook of the live agent once, notifies nobody and executes nothing while its own triggers work; that the
evaluation pairs runs of the same event, counts agreed, live-only and variant-only proposals with the
live decisions and holds no message or argument; and that a replay fires the recorded inputs oldest
first, records the runs it replays, stops at the variant's budget and refuses a live agent and a
second replay, on both providers. Assert that a headless run with retrieved context retrieves once
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
the open, unexpired proposals. Assert that a run started by hand that acts for the accountable account
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
same on SQL Server and PostgreSQL. Assert that the migrations leave no pending model changes.
SPM-268 to SPM-280, SPM-282 to SPM-288, SPM-290 to SPM-297 and SPM-301 to SPM-304 are the executable
references.
