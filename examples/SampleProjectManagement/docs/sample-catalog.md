# Sample catalog

This solution uses one small project-management domain to demonstrate NewHeap
features from the frontend through to the database. A sample is complete only
when it:

1. can be executed from one of the frontends;
2. uses the real NewHeap type instead of a local imitation;
3. explains in the UI why and when to use the pattern;
4. has a small, focused test where that adds value.

## Available workbenches

| Workbench | Examples | Main NewHeap mechanisms |
|---|---|---|
| Project register | CRUD, composite, short projection, partial and bulk operations, and outbox events | thin base controller, concrete service, transaction scope, `NhProjection`, `BulkAsync` |
| Collection playground | LIKE, filters, OR and AND, paging, page size, and search debounce | collection request and result models, and collection components |
| Authentication playground | password, OAuth, refresh, multiple roles, scoped permissions, and authentication overrides | authentication service, request claims, policies, guards, and pipes |
| Operations API | CAP, Hangfire, email with a named SMTP security mode, and notification delivery | event configurator, `NhHangfireUtil`, `NhMailService` with `MailServiceSettings.SecurityMode`, notification builder |
| Notification playground | polling, badge, mark-as-read, and archive | abstract notification component, service, and controller |
| Platform playground | text, binary, download, modal, multi-select, custom control, head, and JSON-LD | HTTP options, modal, form, head, and configuration services |
| Utility playground | strings, arrays, encoding, mutex, cookies, safe and date pipes | concrete utilities and components |
| EF Core chunks | ordered project query in bounded asynchronous batches | `IQueryable.ChunkAsync` with cancellation |
| Project members | members with reader, participant and approver roles that decide access to the project's agent threads | `ProjectMemberController`, `ProjectSubjectAccessResolver`, `INhAiSubjectAccess.RefreshAsync` |
| Intake reviews | proposals waiting for the signed-in person, and one project's proposals, runs, working memory and files | `@newheap/platform-ai-chat/agent-runs`, `NhAssistantAgentRunApiService` |
| Executable tests | collection expressions, projection, validation, SQL, and translation parity | library-only contracts without a UI abstraction |

The equality filter (SPM-017) reuses EF query compilation across repeated requests and changed status values. `CollectionAndProjectionSamplesTests` verifies this with both relational providers; the library regression also executes ordinary and nested any/all filters on SQL Server and PostgreSQL.

## Library patterns

The intake reviewer (SPM-268 to SPM-271) is an assistant agent that starts on its
own. `ProjectService` publishes `ProjectCreatedEvent` inside the project
transaction; the sample's CAP consumer receives it after the commit and fires the
`project-created-intake` trigger, whose event id deduplicates repeated deliveries.
The run is a background operation owned by the agent. The agent may propose a
status change because both `ProjectAiTools.ChangeStatusAsync` (`AllowAgentGrant`)
and `SampleIntakeReviewerComposition` grant it, for trigger runs only. Its
proposal waits under the `project-intake` approval policy until a project approver
other than the project's creator decides it; the approved change then executes as
its own background operation. An edit in between makes the proposal stale (SPM-271): the
decision facts say `SubjectChanged`, an approval is refused with `assistant-proposal-stale` and
closes the proposal at once, and an edit after the approval still closes it before it executes. An
approver may also answer with a messaging reply: `SampleApprovalReplyChannel` asks
`INhAssistantProposalDecisionService` whether the person may decide before it issues
them a reply token, and whether the project changed since, and `POST approval-replies` decides their reply as them with the
checks of the decision endpoint; a reply delivered twice returns the earlier decision,
and the audit names the `messaging-reply` channel and the delivery. The review needs four eyes
only while a second person may approve it (SPM-356): on a project whose creator is its only approver,
four eyes would keep it waiting for nobody, so the API's approval policy selector
(`SampleIntakeApprovalPolicySelector`, registered with `UseApprovalPolicySelector`) asks the approver
directory whether anyone else may approve under `project-intake`. When nobody else on the project
approves, the application's project managers decide the review, still four-eyes, under
`project-intake-portfolio` (SPM-378): that policy admits them through the policy rule
`app.project.manage`, which the directory counts completely because `SampleApproverCandidates`
(registered with `UseApproverCandidates`) names everyone who passes it, each checked as a project member
is. Only when the directory is sure nobody else may approve at all does the review wait under
`project-intake-sole-approver`, the same policy without four eyes, which the creator decides. The
proposal keeps the chosen policy, so the decision checks it and a colleague who joins later never changes
it; but a review that waits without four eyes closes at its decision once a second approver joined
(SPM-379): `SampleSoleApproverProposalGuard` reads the policy the review recorded
(`NhAiProposalGuardContext.PolicyId`) and closes it as `not-applicable`, so the creator never approves
alone what a colleague can check. A selector that chooses an unknown policy, throws or answers too late
refuses the proposal with its own code. The reply channel finds whom to ask through the same directory (SPM-357):
`OfferToApproversAsync` lists the people who may decide under the proposal's policy copy
(`INhAssistantApproverDirectory.ListForProposalAsync`), the project's members and the initiator checked as
the decision checks them, and says whether the list is complete. A participant may also ask on the
project page for another status without any run (SPM-364): `POST projects/{projectId}/status-requests`
lets `SampleProjectStatusRequests` record the request as the intake reviewer's deferred proposal for that
person (`INhAssistantProposals.ProposeForAsync`), with the texts the approvers read in English and Dutch,
written by the application, and the page's request id as the idempotency key. The platform checks what a
run's proposal meets, with the participant's own rights instead of the reviewer's grant; the proposal
waits in the reviewer's thread at the project under `project-intake`, a request sent twice finds the same
proposal, someone outside the project is refused, and an approver other than the participant approves the
change, which executes with the participant's rights. Project
members and their roles decide who sees the project's agent thread, through
`ProjectSubjectAccessResolver`; removing a member refreshes the thread's people, so the former
member's participant row goes and no live update or notification of the thread reaches them
(SPM-270), and deleting a person forgets their browsers, notification choice and conversation rows
(SPM-264). A participant can also start the intake reviewer to
request a review (`projects.request-review`, SPM-274), a call that may run only once:
when it fails without a result, the proposal waits as `execution-unknown` until an
approver resolves it. The status advisor (SPM-272, SPM-273) has no grants; a
participant starts it, it acts with that participant's rights without a request, and
its inline proposal suspends the run's background operation until the participant
approves; when nobody decides in time, the proposal expires and its policy fails the
run. The review board answers through a signed webhook trigger, a weekly
schedule reviews projects whose deadline is near, and an administrator can stop the
reviewer with the kill switch (SPM-275). The reviewer keeps its checks in a typed
working memory, saves an intake note as a versioned file of the thread and starts
each run from the memory, the files and the summaries of its last runs; results it
obtained with its own grant are shown only to people who satisfy the tool's policy,
and deleting a project deletes its agent thread in the scope its resolver describes the
project with, so an equal id in another scope keeps its own (SPM-276).
`AssistantAgentRunSamplesTests` runs every flow on PostgreSQL with Hangfire in
memory. The intake reviews page in the management portal (SPM-277) shows the same
work with the components of `@newheap/platform-ai-chat/agent-runs`; sign in as
`project-lead@example.test`, the only Development account with both the
application-wide and the Sample North project permissions that the assistant's
access policy and the agent's tools require. Opened in the assistant panel, a project's
thread offers each proposal to the approvers the server names: the lead, added as
approver on the project members page, approves the proposal of a project the manager
created, while the manager reads that another approver decides. The panel reaches the
live hub over WebSockets because the portal passes its active division as `divisionId`
through `NhAssistantConfig.getHubQueryParameters`.

The project subject type describes itself where it is registered (SPM-301):
`AddSubjectType<ProjectSubjectAccessResolver>(type => ...)` names it in English and Dutch from
the application's own resources, binds it to the `Project` entity and links a project to
`/management/intake-reviews?project={id}`, which selects that project on the intake reviews page.
The same resolver implements `INhAiSubjectSearch` and finds the projects a person owns or is a
member of by key or name, with one provider-neutral query; the assistant keeps only results the
resolver lets the person read. The inbox, the proposals and the thread view show "Project" next to
the project's name and link it, and the run catalog lists the type for the administration.
Every agent of the sample says what it works on (SPM-302): the agents in code call
`WorksOn(ProjectSubjects.Type)`, and their triggers and trigger events name the project type up
front with `Subject(ProjectSubjects.Type, e => ProjectSubjects.IdOf(e.ProjectId))`, so startup checks
them. The project starter, created as data, works on projects. Next to it the sample creates the
project follow-up as data: it works on projects and on the whole application, project managers may
run it for the whole application and everyone who reads projects sees its results there, two
policies the composition lists with `AddAgentPolicy`. A project participant may start it on a
project; once an administrator narrows it to the whole application, a start on a project answers
`assistant-agent-does-not-work-on` and a trigger on created projects no longer fits it. Its schedule
every Monday names no subject set, so each tick starts one run for the whole application in the
follow-up's own thread of `nh-system/global`, which the platform registers as "The whole
application". The composition lets everyone who reads projects read the whole application with
`UseSystemSubject(system => system.ReadPolicy("app.project.view"))`, because the assistant's access
policy needs an active division. NewHeap Identity lists the sample's divisions through
`INhDivisionDirectory`, so the editor offers to work per division. The status tool's input names its
project with `[NhAiSubject(ProjectSubjects.Type)]`, and the follow-up's `complete-projects` skill works
by fixed rules: it counts the open projects and, with more than three, proposes to complete every
active project whose tasks are all completed. Its weekly tick, and a project manager's run now that
its routing rules send to the same skill, place each proposal in the follow-up's thread at its project
after the platform checked the project and that the operations account reads it; the project's
approver finds it in the inbox and on the project as "Proposed by a run for the whole application",
opens that run's conversation from it and approves it, which changes that project's status. People
with the application-wide project view permission read every project's agent work. Each approver
hears once per run about what waits for them: the colleague who approves every finished project reads
"4 proposals at 4 projects wait for your decision" in the application's inbox, grouped per run, and an
approver of one project only reads about that project, by its name; a day later each is reminded once
for the run. A clicked notification about a run opens the intake reviews page with that run's
proposals (SPM-310), because the portal's shell subscribes to `NhAssistantPushService.openRun$`.
The whole application belongs to no tenant, so a deployment for one customer, whose people sign in at
the customer's identity provider and all belong to its tenant, pairs
`UseAuthenticatedClaimsForSingleTenant(issuer, tenantId)` with `UseSingleTenant(tenantId)`
(`SampleSingleTenantComposition`): the customer's manager then sees an agent of the whole application
on the project management board and may run it, a colleague reads it, and both read its thread, which
stays stored without a tenant. Startup refuses the mode next to a projection of another tenant or a
tenant claim.
When such a deployment adds its people's rights in its own claims transformation at every request,
the NewHeap user store cannot rebuild them for work outside a request (SPM-315).
`SampleAccessSourceComposition` records the claims of each person's own signed-in requests after the
transformation in the application's own `SampleAccessContexts`, under the platform's actor id and with
the time, and registers it with `UsePrincipalsFromAccessSource` and a maximum age of half an hour;
NewHeap stores none of it. A run the manager started on a project changes its status with the rights of
the manager's last request, and so does an agent that acts with the rights of the project's responsible
person; the colleague without the manage permission and someone the application never saw are refused.
Half an hour later the run is no longer offered the status tool, until the manager's next request; the
manager's own request to the host's sign-out path forgets the rights at once. A request that an agent's
delegated read or delegated MCP token authenticated, and an impersonation session, are not the
manager's own (`NhAiAccessContext.IsPersonsOwnRequest`): they never renew the rights, and after sign-out
they bring nothing back. A headless gateway read asks the host's delegated read issuer for a credential
only while the manager's recorded rights are fresh. `AssistantAccessSourceSamplesTests` shows it in a focused composition, because
the sample's own API signs in with the NewHeap user store.
Each run keeps its whole answer as its result (SPM-304): the follow-up's completion rules write
"N open projects; proposed completing X, Y" with the keys of the projects they proposed, which the run
keeps next to the small summary its next runs start from. The rules write it per culture, so an
English reader reads it in English and a Dutch reader in Dutch, although the run works in the
application's default culture. People who pass the follow-up's view policy and watch its work on the
assistant hub, as the Project management board and the approval inbox do while they are shown, hear its
runs live; others do not. People who pass the follow-up's view policy
find its runs for the whole application in `GET runs` and read a run's result with the proposals it
placed at the projects they read; a colleague without that permission does not see them. The
administration's agent cards offer "Runs": every run of the follow-up with its trigger, object, result,
proposals, usage and duration, filtered by kind of object, object, the whole application, status and
period, each opened with its whole result rendered as safe Markdown. The playground lists earlier runs
of the follow-up through the mock.
The sample also counts what its agents' runs did from the content-free `AgentRunEnded` events alone
(SPM-304), as a usage report or billing would: `SampleAgentRunFacts`, fed by a business audit sink,
counts runs, runs without a model call, shadow runs, failed runs, model calls and what started them
(`NhAssistantTriggerKinds`) per agent and skill, without reading the assistant's tables. A project
manager's run of the follow-up counts as one more live run of its completion skill, started by hand and
done by its rules without a model call.
The portal's Project management page shows the board of the "Project management" group of agents
(SPM-304), which the sample registers in code with English and Dutch names: the project closer and the
project planner join it in code, and the development seeder puts the follow-up, created as data, in it
as an administrator does in the agent editor, where an administrator also takes an agent out of a group
of its code. `nh-assistant-agent-board` shows people who pass the follow-up's view policy its latest
result for the whole application, marked new until they visit the board again, with run now for project
managers, its open proposals and its history; a colleague without the permission does not see the
follow-up there. The thread of the whole application is never readable more widely than the follow-up's
results. The playground shows the same board on the mock.
The project assistant can consult the intake reviewer (SPM-278): the reviewer allows
consultation about projects with `AllowConsultation`, and the project assistant selects
the generated `assistant.consult.*` and `assistant.request.*` tools, which the sample's
discovery policy offers next to the project tools. Asked what is still missing for a
project, the assistant gets the reviewer's answer in the same turn; the reviewer reads
its working memory and earlier runs with the asking person's rights only and writes no
message into its thread, while the intake reviews page lists who consulted it under
Consultations. People outside the project get the same answer as for an unknown project.
A participant can also ask the reviewer to review the project again in a run of its own
and is notified when it ends. When the reviewer proposes to request a review during a
consultation, the participant who asked decides it right away in their own chat, and it
runs in their own request with their own rights. The project assistant also consults the
project closer, whose proposal to complete a project waits for a project approver other
than the person who asked and runs with the closer's own grant. Neither path rebuilds a
person's principal outside that person's request. One answer of the project assistant consults at
most twice (`MaxConsultationsPerTurn`); a third question in the same turn is refused with
`assistant-consult-turn-limit` before the reviewer runs. The reviewer's consultation names its tools
with `Tools(...)`: asked about a project it may only request a review, so its status and task
tools stay with its own runs; a status change its model names anyway runs nothing. The consult tool only ever acts for a
person: an agent created in the administration that acts with the operations account's
rights is not offered it in a run that a created project starts, because it does not
delegate from the person who created the project.
The reviewer's answer to such a request also comes back where the participant asked (SPM-360): its
consultation answers requests in the asking conversation (`AnswerRequests`), so the completed run posts
its answer once into the participant's chat as an application message that only they need to be able to
open, with a link to the intake reviews page of the project. The run keeps that chat and turn; a request
of an MCP client or of an agent run has none, and a shadow run or one that read results people may not
see posts nothing. The project planner takes requests only (SPM-361): the project assistant may ask it
to look at a project in a run of its own, never consult it for an answer, so only its request tool
exists, and the settings of a consultation's own turn would stop the API at startup. An administrator
gives the project follow-up such a consultation as data about the whole application
(`requestsOnly`), and a requests-only save that names the turn's settings is refused at its fields.
A partner application's assistant may consult the intake reviewer over MCP as well (SPM-366), and
the two applications keep one consultation chain: `SampleConsultationChain` names the API in chains
(`UseMcpChainApplication`) and reviews the partner by the issuer its calls carry (`ContinueMcpChain`).
A consultation the partner starts counts the partner's agents as askers and begins one level deeper,
so when the partner's desk, which the reviewer consulted in turn, asks the reviewer again, the call is
refused with `assistant-consult-depth-exceeded` before the reviewer runs. The same chain from an MCP
client of another issuer is ignored, not refused. Once the API has a name in chains, the research
binding sends the chain of the calling turn too (`IncludeChain`). The chain is loop protection
between cooperating applications, not authorization: the policy, the project role, the budgets, the
daily consultation limit and the call's deadline still decide every call.
After rejecting a proposal, an approver may say what was wrong (SPM-279): a category such
as `not-now` and a short text, never required and never asked before the decision. The
next run of the reviewer on that project starts from the rejection and the approver's
words, so it does not propose the same change again unchanged. Administrators read the
rejection rate and the categories per tool and agent version and export the feedback as
JSON Lines; the sample's `SampleProposalFeedbackLog` forwards each feedback to the
application's analytics without its text.
The agent's work also appears on the project itself (SPM-280): the project directory shows a
badge per project from one summary request, the project dialog shows the open proposals of
every agent on the project with the viewer's decision rights, and opening the conversation with
the reviewer about a project creates its thread without starting a run. Whoever starts the
reviewer by hand hears over the assistant hub that its run started and ended, also on a new
project whose members nobody changed yet, so the page refreshes without a reload.

None of this requires NewHeap Identity (SPM-281). The sample's tests host a partner portal with
its own EF context and its own users: it maps background operations into schema `nhops` and
notifications into `nhnotify` next to its tables, registers both without the NewHeap platform
builder, and tells the platform who its users are through `INhActorDirectory`. A submission
event starts an agent whose run joins the portal's transaction; the project's approver hears
about the proposal in the portal's own inbox in their language and approves it, after which the
change executes. Applications on NewHeap Identity keep their model unchanged, and deleting a
user through `NhUserManager` lets every module forget the user in the same transaction.

The assistant and every agent run on the sample's deterministic model,
`SampleAssistantChatClient`, which recognizes a few fixed phrasings to demonstrate the governed
flows and is the model every test uses. Optionally, `SampleProjectManagement.Assistant.Model` in
the local secrets connects an OpenAI-compatible model instead (SPM-245), so agents and skills whose
model decides can be tried with free instructions. `SampleAssistantModel` validates the settings at
startup without logging the key and registers the client under the same model key before the
deterministic one. The assistant then sends the model's execution region, `external` by default,
through `UseExecutionRegion`; the assistant's chat profile and the planner's triage profiles permit
that region next to `local`, and the assistant's profile gets a larger call budget and timeout.
Messages and the project data that tools read leave the machine, so use test data only; the
[README](../README.md#a-real-model-for-the-assistant) lists the settings.

The assistant reads files of the sample's media library (SPM-305). `ProjectMediaFileSource`
implements the assistant's file source: a person attaches a PDF, an image, or a Word or Excel
document in the chat panel, the upload endpoint checks it and the source stores it in a media
library folder of that conversation, with the `Confidential` classification the assistant gives
every upload. The sample maps its own extension `.jfif` to JPEG with `MapExtension`, so a photo
that arrives without a type uploads as `image/jpeg`, still checked as a JPEG, and the panel's file
picker offers `.jfif` files. A turn opens each file through the source for its own person or agent: an upload
opens only in its conversation, a project document for the people who see the project's agent work
and for an agent on that project, and the media library's own endpoints never serve the uploads.
Confidential files move the model call to the chat profile's fallback, which permits them. The
model reads PDFs and images itself; `OfficeDocumentReader`, the example reading tool the sample
registers as `office-documents`, turns Word and Excel documents into text with DocumentFormat.OpenXml,
because NewHeap's packages carry no document library. The deterministic model says which files it
received; with a real model, its `MediaTypes` setting lists the types it reads itself. The document
reader (SPM-306) reads a project's documents when one is added to the project's `documents` folder in
the media library: its `read-documents` skill opts in to files with `AcceptsFiles()`, like the agent,
and its tool names the media items in its result without content. The other agents never opted in,
so the chat panel shows the attach control only for the project assistant and the document reader.
`AssistantFileSamplesTests` runs both cases, and the safety evaluation of SPM-233 a turn with a
malicious document, whose text stays untrusted data that changes no tools or approvals.

Uploads go with their conversation (SPM-359). The sample registers `ProjectMediaUploadRetention` with
`UseFileRetention`: once the owner archived a chat, or subject retention deleted an agent's thread, the
assistant asks it to forget the conversation, and it removes the media items of the conversation's
upload folder and their content. Forgetting a conversation again finds nothing and succeeds, and a
retention that fails only leaves a warning: the archive stays. The quick answers agent runs on
`project-assistant-internal`, a profile for a model that may receive internal data only, without a
fallback. Uploads are confidential, so the agent list gives it no `uploads`, the panel and the
messaging app offer it no attachments, and an upload to its chat answers `409
assistant-files-not-accepted` instead of failing in the turn. `AssistantChannelFileSamplesTests` runs
the case.

Model calls carry a data classification (SPM-307): the skill's, then the agent's, then `Internal`,
in runs and in people's plain chats alike. The document reader's `read-documents` skill classifies
its calls with `DataClassification(Confidential)`, because what project documents say is
confidential; the chat profile permits internal data only, so every model call of the skill moves to
its fallback `project-assistant-confidential`, also when the documents themselves are internal. An
administrator creates an agent with its model profile and classification in the administration; a
confidential agent on the chat profile answers people's chats through the same fallback, and one
with a classification that no profile of the chain permits is saved with the warning
`classification-not-permitted`, while its chats fail with `assistant-classification-not-permitted`.
`AssistantClassificationSamplesTests` and `AssistantFileSamplesTests` show which profile served the
calls through the sample's budget manager (`ProjectAiBudgetUsage.ProfilesOf`).

Every profile the assistant uses runs in its execution region (SPM-323). The sample's chat profile
and its confidential fallback permit `local`, where the deterministic model runs, and the region of
the real model its secrets configure, which `UseExecutionRegion` names. `AssistantRegionSamplesTests`
moves the sample assistant to a region its profiles do not permit: the host does not start, and the
error names the chat profile and the region, instead of every model call failing later. The
administration refuses a profile that does not run in the region with `region-not-permitted`, and a
turn that no profile may serve there fails with `assistant-model-region-denied` without a retry.

An agent is not started again by its own change (SPM-308). The project service stamps the cause on
every status event: the person, or the agent whose status tool changed the status with the subject it
worked in (`ProjectAgentCause`, from the tool's invocation context and the ambient agent invocation
scope), and the agent run the change belongs to (the context's `AgentRunId`; `RunId` is the turn's
id). The planner's composition registers the status-changed event with the named filter
`not-own-change`, and the application adds a status follow-up trigger to the planner that selects it.
The filter skips the event only when the trigger's own agent changed the status on the subject the
trigger fires for, so the planner's change of a project does not start it on that project again,
while its change made while it worked on another project and a person's change do.
`AssistantAgentRunSamplesTests` runs the three cases.

Approvers get an overview of their inbox (SPM-310). The project resolver names each project's division as
its parent, and every proposal records that parent when it is made, so the inbox groups without asking
a resolver per proposal. The intake reviews page shows the inbox grouped: a division with its projects'
proposals, per project and run, how many of a group the page shows of its total and whether the group
continues on another page, and "Select group" selects only the shown pending proposals the person may
decide. The page can show the active division's proposals only. What came since the person's last
visit of the inbox in its scope is marked new, also what arrives while it is shown. A run's result
lets its reader select and decide the run's proposals together. `AssistantAgentRunSamplesTests` proves the
grouped order and totals across pages, the recorded parent, the division filter and what is new.

What waits for people reaches them (SPM-327). The sample's daily approval digest, `SampleApprovalDigest`,
counts per person what the approval inbox lists per status with `INhAssistantProposalCounts`: proposals
to decide, approved orders the order desk has not reported on yet and executions whose outcome nobody
knows, ids and counts only. `GET approvals/inbox?status=awaiting-application` and `status=execution-unknown`
list those proposals with the visibility of the pending ones. The whole application keeps no member
snapshot, so `SampleDecisionDigestComposition` lists its people with `UseSystemSubject(s => s.Members<...>())`:
`SampleSystemMembers` names the users of the administrator role and of roles that grant
`app.project.manage`, so a company-wide proposal in a thread nobody opened still reaches the people who
decide it, while the policies keep deciding everyone's role.

Administrators let other assistants consult an agent without a deploy (SPM-311). The project starter's
composition calls `UseConsultationsAsData`: data may let the person who asked decide a proposal, or let
a project approver other than that person decide it with the agent's own rights, and may generate request
tools; the route that rebuilds the asker's principal later is not named, and MCP clients never see such
consultations. The project assistant selects the consult tool of the project starter in code, which
selects nothing until an administrator saves the starter's consultation about projects with
`SaveConsultationAsync`, as `SampleAgentConfigurationService` does; the development seeder defines it
once. The tool then appears in the administration's tool list and the assistant's next turn. A project
approver asks whether a draft can start, the starter answers with that person's rights and proposes to
start it, and the person decides it on the card. The administrator chose the starter's status tool for
the consultation (`ToolSelectors`); a tool outside the starter's own is refused with
`toolSelectors: not-allowed`, and while the starter's own tools no longer cover it the consultation
answers without any tool and reports `tools-not-allowed`. The starter has `Propose` autonomy: a consultation with
a proposal route offers its model the status tool all the same, and nothing changes before the
decision. A reader gets the answer only. With the asker's route
switched off for the starter, the proposal waits for another project approver and executes with the
operations account the starter answers to. After the consultation is removed the tool is gone, while
the waiting proposal can still be decided. `AssistantAgentRunSamplesTests` runs the case, with a
principal factory that refuses the people involved. In the portal's assistant administration, and in
the playground on the mock API, the starter's run configuration lists the consultation in its
Consultations section: who may ask, who decides, the project assistant that asks it with a link to its
editor, and the switches that turn a route off for all of the starter's consultations. Administrators
add, change and remove consultations there with only the routes, requests and limits the ceiling and
the agent allow, and the flow's "When it runs" step names the consultation with its problems.

The application keeps its own data about agents as agent metadata (SPM-313). The project planner's
composition registers the cost center with `AddAgentMetadata<SampleAgentCostCenter>`: its JSON shape,
at most 512 bytes, a validator that accepts only codes such as CC-4100 (`value.code:
unknown-cost-center` otherwise), the code as its searchable value and `Editable()`. The planner sets
CC-4100 in `ConfigureAgent`, which startup checks as a save checks data. In the portal's assistant
administration, and in the playground on the mock API, the planner's editor shows an Application fields
section below its form with a slot for the cost center, where the portal's own editor
(`AssistantCostCenterEditorComponent`, registered with `provideNhAssistantAgentMetadataEditors`) shows
the value from code and saves an override; "Use the cost center from code" removes it again. An agent
created in the administration gets its cost center with its definition or in the slot, each change its
next version. `AssistantAgentRunSamplesTests` overrides the planner's cost center, creates an agent with
one, finds both with `INhAssistantAgentMetadata.ListAgentsAsync` whatever the code's case, and proves
that people who do not administer neither read nor change it; NewHeap itself never reads the cost
center.

The planner also works for two departments, planning and operations, a second entry of agent metadata
(SPM-313). `SampleAgentDepartmentsComposition` declares every department code as a searchable value
with `IndexByEach`, so the application finds the agents of a department by any of their codes with
`ListAgentsAsync`; NewHeap stores the codes once per agent, at most 50, in a table of their own, and a
code that is no department is refused at its field. The portal shows the entry without an editor of its
own. `AssistantAgentDefinitionSamplesTests` finds the planner by a code in code and a desk created at
runtime by a code it was given.

The application executes some approved proposals itself (SPM-314). The intake reviewer may propose to
order equipment for a project, and the sample's order desk works asynchronously: its supplier confirms
an order hours later. `SampleEquipmentOrderComposition` therefore declares the order tool with
`ExecuteInApplication` and a report deadline of eight hours and registers `SampleEquipmentOrderExecutor`
with `UseProposalExecutor`. Once a project approver approves the proposal, the assistant hands the
approved arguments, the proposal hash, the approver and the proposal id to the executor, which puts the
order on `SampleEquipmentOrderDesk` instead of running the tool's function, and the proposal waits as
`awaiting-application`. When the supplier confirms, the desk reports through
`INhAssistantProposalExecutionReports` with its code and a short summary: the proposal closes as
executed, failed or uncertain, the approvers are told, the summary appears in the project's thread as a
message of the application, and the audit records the outcome and the code only. An order nobody
confirms in time becomes `execution-unknown`, and an approver hands it to the desk again from the
proposal's card. The hand-off carries the approved arguments, not the division the call was authorized
for, so the executor reads the ordered project's division and refuses the order with
`order-project-out-of-scope` unless the approver manages projects there; the order and review tools
themselves act only on a project of the division their call is authorized for. `AssistantAgentRunSamplesTests`
runs the case, with the desk's confirmations standing in for the supplier's callback.

A person can also order equipment in their own chat with the project assistant (SPM-328). The order tool
is declared with `AlsoInline`, so approving the card in the chat hands the order to the order desk as
well, instead of running the tool's function: the turn ends saying the desk has it, and the desk's report
is posted into that chat as a message of the application with the outcome, the supplier's code and its
summary. The approval records the UI culture of the person's turn, so the report, an uncertain outcome
and a passed deadline read in that language, title included: English for a person whose client reads
English, although the application defaults to Dutch, and Dutch for one whose client reads Dutch. An
approval handed off before the culture was recorded reads in the default culture, and agent threads
keep it. An order the supplier does not confirm in time becomes uncertain, and its card in the chat
offers the person who approved it to hand it to the order desk again or to mark it done. An order for a
project of another division is refused by the executor in the chat too. The order tool also continues
the chat after the desk's report (`ContinueChatAfterReport`): shortly after the report, the project
assistant tells the person by itself that the desk placed the order, in a turn nobody typed. That turn
runs as the person who asked, with their rights, and could only propose a change for them to approve.

In shadow the project closer proposes an unchanged finding once (SPM-324). A shadow proposal never
waits for a decision, but within its seven-day lifetime it counts as open, as the live rules would see
it: when a task of the project is saved again, the next shadow run finds the same proposal, records
nothing new and is audited as `AgentProposalDeduplicated`, so evaluating the closer in shadow compares
what it would really propose live. `AssistantAgentRunSamplesTests` runs it.

In shadow the intake reviewer keeps one order per item, as it would live (SPM-339). Shadow proposals
name their objects as live ones do: `ProjectEquipmentProposalGuard` names the project and item of an
equipment order. While an operator evaluates the reviewer in shadow, an administrator starts it to order
three laptops for a project and then five: within the proposal's lifetime the newer shadow order
replaces the first under the intake policy's `Supersede`, the first becomes `shadow-superseded` and
names its successor, and the audit records `AgentProposalSuperseded`. Nothing reaches the order desk and
neither order waits in an inbox. A policy with `Skip` reports the newer one as already open instead.

Shadow work never starts live work, also when a live agent hands work to one in shadow (SPM-287). While
an operator evaluates the project closer in shadow, the live planner still hands it a finished project;
the closer answers in its shadow thread, and the planner hears how that run ended in its own shadow
thread of the project, which only administrators see. No live planner run starts, and the project's
approver never reads the closer's shadow answer. `AssistantAgentRunSamplesTests` runs it.

A changed data trigger can be tried in shadow before it replaces the live one (SPM-316). The planner's
status follow-up skips the planner's own changes (SPM-308); `SampleAgentConfigurationService` saves the
change that drops that filter with `Apply = NhAssistantSkillApplyModes.Shadow`, so it becomes the
trigger's next version as its candidate while the live version keeps firing unchanged. Every event the
trigger receives is evaluated by both: the planner's own change starts only the candidate, in the
planner's shadow thread of the project, and a person's change starts the live version in the live
thread and the candidate in the shadow thread, which records the trigger's version and that a
candidate started it. `CompareTriggerCandidateAsync` counts both versions' runs and the events both
handled, and the team discards the candidate with a reason; the trigger's history keeps every version.
In the assistant administration, the run configuration shows the trigger's candidate with what it
changes, the comparison, "Promote" and "Discard", and the trigger's history.

An agent created at runtime can start in shadow, and a consultation can wait for an administrator
(SPM-317). `SampleAgentConfigurationService` creates the shadow starter with `StartsInShadow` and in
the same call gives it run settings and a live trigger on created projects: its first run already
works in its shadow thread, where nobody is notified and nothing executes. Its consultation about
projects is saved with `StartsOff`, so asking agents get no consult tool until an administrator
switches it on with `SetConsultationStateAsync`, after which the tool appears at once. The agent
editor offers "Start in shadow" for a new agent with runs, and the consultation form "Start off"; a
consultation that is off shows that and offers "Switch on". `AssistantAgentRunSamplesTests` runs both
cases.

Administrators connect agents with hand-offs as data, within a ceiling in code (SPM-318).
`SampleAgentLinksAsDataComposition` calls `UseHandOffsAsData`: agents created in the administration may
accept hand-offs about projects from each other and from the project planner, at most five per source
and project a day. The test creates an intake desk that works with the model and a kickoff desk that
works by the kickoff rules, connects them with `PUT admin/agents/{id}/hand-offs/project` and sees the
intake desk's model hand a draft project over through the generated tool; the kickoff desk proposes to
start it in its own thread, as a hand-off in code does (SPM-287). A source or target outside the ceiling
is refused, and a stored hand-off that no longer fits is out of effect with a problem. The same
composition calls `UseShadowVariantsAsData` (SPM-319): an administrator creates a trial of the project
planner with `shadowOf`, gives it run settings with the progress-note rules, and the trial receives the
planner's events in a shadow thread, never goes live and is paired with the planner in the evaluation.
In the assistant administration, and in the playground on the mock API, the run configuration of an
agent lists its hand-offs and shadow variants below the flow. `AssistantAgentRunSamplesTests` runs both
cases.

The two desks of SPM-318 travel as one agent package (SPM-321). `SampleAgentPackagesComposition` names
the package and what it locks: the kickoff desk's instructions and those of its review skill. The test
exports both desks with `POST admin/agent-packages/export`, a JSON download without the operations
account they answer to, and imports it again with both desks relinked, so they belong to the package. A
changed copy with new review instructions is an upgrade: the preview shows the skill as changed, the
import in shadow makes the new instructions a candidate next to the live version and the promote puts
them live, while the hand-off between the desks stays in effect. A change of the kickoff desk's locked
instructions is refused with `instructions: locked`; once the administrator unlinks the desk's
definition with `POST admin/agents/{id}/package/unlink`, they change, and the next upgrade keeps them as
`kept-local`. `AssistantAgentRunSamplesTests` runs the case.

The next version of the kickoff desk can also be evaluated first (SPM-344).
`SampleAgentPackagesComposition.NextKickoffVersion` turns the exported desk into a shadow variant of the
live desk in a package of its own, `sample-project-desks-next`, with new instructions and review skill
and the same locks; the shadow variants of the kickoff desk name that package version. Promoted the
usual way the variant stays, since its package holds it (`assistant-package-locked`). Promoted with
`asPackageVersion`, its instructions, run settings and review skill apply to the live desk in one
transaction with the import's rules: the desk keeps its name and operations account, its new
instructions are locked by the new package version, the review skill's next version is live, and the
desk is linked to `sample-project-desks-next` while the intake desk stays in `sample-project-desks`.
The variant is gone with its links and one audit event records the promotion with the package.
`AssistantAgentRunSamplesTests` runs the case.

The next version carries no hand-off, so the kickoff desk's hand-off from the intake desk would lose its
link on that promotion (SPM-352). Promoted with `asPackageVersion` and `uncarriedParts` `relink`, the
hand-off stays as it is and stays linked, now to `sample-project-desks-next`, with the locks and hash of
its link, and the promotion reports it as `kept-linked`; the desk's hand-offs show it in effect with that
package, and `sample-project-desks` no longer holds the kickoff desk. `AssistantAgentRunSamplesTests`
runs the case.

A skill defined as data may take its instructions from the application's own text assets instead of
text an administrator writes (SPM-320). `SampleInstructionAssetComposition` registers
`SampleInstructionAssets` with `UseTextAssetSource`: the planner's risk review in two published
versions, fixed in code. `SampleInstructionAssetService` adds the risk review to the project planner
naming version 1 of the asset by its id, version and hash; the skill stores that reference, never the
text, and the assistant checks the hash whenever it loads the text, so a changed or missing text takes
the skill out of effect with a problem instead of being followed. Version 2 takes effect only when it
is saved as the skill's next version. In the assistant administration, and in the playground on the
mock API, the skill form offers "The application's text" next to "Written here", lists the texts and
shows the chosen one read-only with its hash. `AssistantInstructionAssetSamplesTests` runs the case,
including the refusal of a reference whose hash differs from the text's.

The sample's operations code changes the assistant through the platform's services, never through the
administration's endpoints or its tables (SPM-331). `SampleAssistantOperationsService` connects the
milestones MCP server with `INhAssistantMcpServers`: the API key is protected and never returned, the
synced lookup is enabled as read-only and the server is assigned to the status advisor without a new
version of the advisor, audited as `AdminMcpServerAssignmentChanged`. With
`INhAssistantInstructionAssetUsage` it lists which data skills and agents name the risk review's text of
SPM-320 and which version each uses, and with `GetPackageAsync` and `ReadAsync` of
`INhAssistantAgentPackages` it compares a shipped package with the host's agents without exporting them.
A test host of the environment `Testing` reaches the milestones server on loopback only with
`NewHeap:AI:Assistant:Mcp:AllowLoopback`, which Production refuses. `AssistantOperationsSamplesTests`
runs the case.

Once the application publishes version 2 of the risk review's text, the operations code proposes it to
every live data skill that still names version 1 (SPM-351). `ProposeRiskReviewVersionAsync` calls
`INhAssistantInstructionAssetUsage.ProposeCandidatesAsync`: the planner's risk review gets a candidate in
shadow through its own save, with the live version's other fields and version 2 with its hash, while the
live version keeps version 1 until an administrator promotes or discards the candidate. The risk desk,
an agent created in the administration, is listed for an administrator to save; proposed again, the
candidate stays as it is. An administrator also compares a shipped package with the host over HTTP
(SPM-353): `POST admin/agent-packages/read` answers the desk's parts with a hash per part as an export
builds them and `GET admin/agent-packages/{packageId}` the installed package with the link of every part,
so the description changed on this host shows as the part `agent`, changed locally, without an export
or its audit; a package the host never installed is `404 assistant-package-not-found` and a colleague
without the administration policy gets `403`. `AssistantOperationsSamplesTests` runs both cases.

An agent editor that was opened before an MCP server was assigned no longer drops it when it saves
(SPM-345). An administrator assigns the milestones server to a milestone desk with
`PUT admin/mcp-servers/milestones/agents/sample-milestone-desk`, which changes only the assignment;
the editor then saves the desk's version without `mcpServerIds`, so the desk's next version keeps the
server, and `DELETE` removes the assignment again. The library's agent editor saves an existing agent
that way and then assigns or unassigns only the servers the administrator changed. The operations code
can also prepare the milestones server before the host can reach it (SPM-346):
`PrepareMilestonesAsync` stores the lookup's reviewed settings before any sync has seen the tool, the
administration lists it as not seen yet and no turn offers it, and `SyncMilestonesAsync`, the first sync,
makes it available with those settings. `AssistantMcpAssignmentSamplesTests` runs both cases.

A person may continue a chat with the project assistant in a messaging app (SPM-332). Signed in to the
portal, they link the chat with `POST assistant-channel/links`; the messaging provider then delivers
their messages and their answer to an approval card with the link token, without a sign-in.
`SampleAssistantMessagingChannel` rebuilds the person from the user store and runs each turn through
`INhAssistantConversationTurns`: the endpoint's turn with its access rules, in the person's active
division and with the person's own rights for every tool, never the rights of the provider's request.
The answer carries the assistant's text and, when the turn waits, the approval card with its proposal
hash; a `yes` approves it. A repeated delivery starts no second turn, a link to someone else's chat finds
nothing, and the audit records the `messaging-chat` channel. Only the person's own session links a
chat: an administrator who impersonates the person gets `403 assistant-not-allowed-while-impersonating`,
because the link would act as the person after the impersonation ended. Every link carries a digest of
the person's security stamp, so a password change or a revocation of the person's sessions ends all
their links, and a chat started from the messaging app gets a link that expires with the one it was
started from. In Development the sample also sets
`NewHeap:AI:Assistant:KeepTurnsAfterDisconnect`, so closing the browser lets a person's turn finish and
the chat shows its answer when it is opened again. `AssistantMessagingChannelSamplesTests` runs the case.

The messaging app also offers what the portal's panel lists (SPM-358). `POST assistant-channel/chat-list`
lists the person's chats through `INhAssistantConversations.ListAsync`, newest first, each with a link
token of its own that expires with the presented link, so the person continues in the chat they pick.
`POST assistant-channel/agents` lists the agents they may chat with through `ListAgentsAsync`, with what
they may attach to each. `POST assistant-channel/files` uploads the provider's attachment to the linked
chat through `UploadAsync`, with the checks, codes and statuses of the portal's upload endpoint: the
platform reads the buffered file again for its content check, the sample's scanner and the media
library, and answers with the reference that the person's next message carries in `files`. The file's
name never reaches a log. `AssistantChannelFileSamplesTests` runs the case.

The provider's web widget on a portal page sends the page the person has open with the message
(SPM-380): `page` carries its route, title and the project it shows, and the channel passes it as
`NhAssistantConversationMessage.ClientContext`, so the turn shows the assistant the person's screen,
cleaned and stored as the portal's own messages are. A page the platform would not keep whole, such as
one with more records than the five it shows, answers `400 assistant-message-invalid` and starts no
turn. Every refusal of the channel answers the status the portal's endpoints give its code (SPM-381):
`SampleAssistantMessagingChannel.StatusOf` maps the channel's own codes first and every other code
through the platform's `NhAssistantHttpStatuses.Of`, so the channel keeps no table of its own.
`AssistantMessagingChannelSamplesTests` and `AssistantChannelStatusSamplesTests` run the cases.

People who chat with the planner reach its skills too (SPM-333). With `SkillsInChat` a chat turn starts
without a skill, and the model loads the task follow-up skill with `use_skill` when the person asks
which task comes next: no triage call, the skill's instructions and only the status tool it selects,
with the person's own rights. The planner's message of the turn names the skill (`skillId`); triage and
the skill's daily budget stay with runs. The status advisor may ask a structured question in a chat
(SPM-334): with `AllowQuestions` it asks which status a project gets, with the statuses as choices, and
the turn waits as for an approval. A colleague in the shared chat sees the question but is refused when
they answer; the person whose turn asked answers through `POST conversations/{id}/questions/{questionId}/answer`,
and the turn continues with the answer. The management playground shows the question card with one
button per choice on the mock API. `AssistantChatSkillQuestionSamplesTests` runs both cases.

The assistant lab also opens the assistant with a prepared message (SPM-350). "Draft a status brief"
calls `NhAssistantPanelService.open({ agentId, draft, replaceDraft })`: a new chat with the project
assistant opens with a status brief in the message box, in the person's language, about the project
page the lab has open or else about all active projects. Nothing is sent until the person sends it;
text they already typed stays unless "Replace what I typed" is on, and the brief is capped at the
message limit. A chat on a page of its own takes a draft from `NhAssistantStore.setDraft` the same way.

What the person types and does not send stays with its conversation (SPM-369). The lab's "Unsent
message" card shows what `NhAssistantStore.unsentMessage()` keeps for the open conversation: close the
drawer with a question in the message box and it is there when the drawer opens again, while another
conversation shows its own text. The store keeps it in this tab's memory only, the drawer and the chat
on a page of its own show the same text, sending forgets it and empties the message box, also when fast
automated typing types and sends between two renders, and switching the access permission off, as
signing out does, forgets every unsent message. A prepared brief treats the kept text as typed text.

While the portal's Chat page shows, the assistant opens there (SPM-376). The page's panel
(`mode="page"`) registers with `NhAssistantPanelService`, so the launcher moves the focus to the page's
message box, a clicked notification and every `open(conversationId)`, `open({ agentId })` or
`open({ draft })` act on the page, and an open drawer closes when the person opens the page. Each panel
tells the store whether it shows the open thread (`registerThreadSurface`), so the conversation is
marked as read while the page shows it, whichever panel was created or closed last. The topic bar's
"Conversations" button switches the panel's own list off for a wide thread (`conversationList="none"`)
and on again; a host can also open the list itself with `[(conversationsOpen)]`, and the list beside the
thread has the class `nh-assistant-sidebar`. The same page shows the host's own content in the panel
(SPM-377): a topic bar above the thread (`nhAssistantAboveThread`) names the open conversation, and
`beforeSubmit` holds the first message of a new conversation while the person has an earlier
conversation with the assistant. A choice above the message box (`nhAssistantAboveComposer`) asks the
question in the latest conversation, starts the new conversation with it or leaves it for the person to
change, each through the store; every other message goes at once. While the conversation has no
messages, the page's own empty state (`nhAssistantEmptyState`) takes the place of the panel's, with its
own words and two opening questions that put a prepared draft in the message box. The library specs
prove both cases in the drawer and in a page.

The chat helper works in people's chats only (SPM-343). `SampleChatHelperComposition` configures it with
`ChatOnly()`: it keeps its meeting notes skill, offered with `use_skill`, structured questions and a
limit on the consultations of the intake reviewer it starts in one turn, but it works on no subject. No
project's page lists it, a start on a project answers `409 assistant-agent-does-not-work-on`, nothing
triggers it and it needs no background operations of its own; the administration shows it as chat-only.
The host would refuse to start if it also said what it works on, accepted hand-offs or worked in shadow.
`AssistantChatSkillQuestionSamplesTests` runs the case.

The project chat answers in a person's chat about one project (SPM-363). `SampleProjectChatComposition`
says it works on projects and requires a subject for a chat (`RequireChatSubject()`), so `GET agents` and
the panel's status leave it out and `GET agents?subjectType=project&subjectId=` lists it for a project the
person may read. The planner's shadow variant (SPM-288) works on projects too, but a shadow variant is
never offered in the chat, so neither list names it. The project dialog's "Chat about this project" calls `NhAssistantStore.setChatSubject` with
the project and opens the panel with the project chat; the first message creates the chat with the project
as its subject, and the panel says what the chat is about. The project's access resolver decides when the
chat is created and on every turn, read and invitation: a reader of the project chats and each message
reads the project's briefing, someone of another division is refused with
`403 assistant-subject-access-denied`, a chat with the project chat without a subject with
`400 assistant-subject-required`, and a member removed from the project can no longer read or continue the
chat while they still delete it. The chat stays the person's and its tools run with their own rights.
`AssistantProjectChatSamplesTests` runs the case.

The operations process also reads and changes agents through services (SPM-329).
`SampleAgentDefinitionService` lists every agent that is no shadow variant as it is in effect with
`INhAssistantAgentDirectory`, with its run state and cost center, and gives an agent a new description
with `INhAssistantAgentDefinitions`: it reads the agent and sends its definition back at the version it
read, so the planner is overridden at its next version, keeps its instructions and MCP assignments, and
a change at the old version is refused. Resetting returns the planner to its code; an agent created at
runtime is retired, while the planner in code is not. The same service promotes the planner's trial, a
shadow variant created at runtime, onto the planner once the evaluation shows it does better (SPM-330):
the planner takes the trial's description, instructions, tools, required policy and autonomy, keeps its
name, run settings and live mode, gets the trial's progress review as a new skill in shadow, and the
trial is deleted without ever going live. In the assistant administration, and in the playground on the
mock API, a data variant offers "Promote onto {agent}" with a confirmation. `AssistantAgentDefinitionSamplesTests`
runs both cases.

The management portal's handbook page reads a bundle of the team's Markdown documents with
`nh-assistant-document` of `@newheap/platform-ai-chat` (SPM-336). The text is untrusted: inline HTML
shows as text, and a `javascript:` link or a path outside the bundle is no link. A relative link such as
`release.md#checklist` resolves from the open document's folder against the bundle's paths; the
document reports it through `linkClick` and the page opens that document at the heading, while the
headings it reports through `headingsChange` are the page's "On this page" list. The sample has no
diagram package, so the portal supplies a small renderer for its own `flow` language
(`sampleFlowDiagramRenderer`), whose drawing module loads only when a document has a `flow` block, as
an application would load Mermaid. The document shows the sanitized SVG in a shadow root, and the
release guide's block with a mistake shows its source with the renderer's reason.

An administrator connects MCP servers that OAuth protects for the whole organization (SPM-299).
Before trusting a planning server, `POST admin/mcp-servers/discover` reads which authorization
server protects its address, from its challenge and protected resource metadata. The challenge
answers the MCP `initialize` POST a client starts with, so the stateless planning server, which
answers `GET` with `405`, shows its scopes too; saving the server
with `oauth.confirmAuthorization` pins that issuer, so the assistant asks only it for tokens, always
for the server's own address, and suspends the server's connections when it later names another
one until an administrator confirms again. With `oauth-client-credentials` the sample's own client
obtains a token on first use and reuses it: the planning server sees the client, never a person.
With an `oauth` server for the organization the administrator authorizes the organization's account
once in a popup. The callback checks the state, the issuer and PKCE, exchanges the code and hands a
one-time completion code to the administrator's page, which completes the connection with the
administrator's own session; the code alone completes nothing. Everyone who uses the server's agents
then acts as that labelled account. The sample reads the callback address and the origins that may
start an authorization from `NewHeap:AI:Assistant:Mcp:OAuth` in its development settings, and
`AssistantAdministrationSamplesTests` runs both flows against `NhAiTestAuthorizationServer`.
Disconnecting revokes the tokens at the authorization server, and no secret, code or token reaches a
response, a log or an audit event.
A server can also let each person connect their own account (SPM-300, `connectionScope: user`); it
has no organization connection, so the administrator connects their own account to synchronize its
tools. When Ann asks before she connected, the answer goes on without the server, says why and shows
`nh-assistant-connection-required`, a card that survives a reload; she connects in a popup, completes
with her own session and asks again, and the agent calls the server with her account. Bob is asked
for his own account and never gets Ann's, and disconnecting in `nh-assistant-mcp-connections` revokes
her tokens at once. The status advisor, which a participant starts and which acts with that
participant's rights, uses the participant's connection in its background run only when an
administrator offers the server in background runs (`headlessUse: personal`) and the participant
consented while connecting; the audit names whose connection a call used. An administrator who
impersonates Ann connects no account in her name: an impersonation session neither consents to
background use, nor starts or completes a personal connection, nor registers a browser for push
(`assistant-not-allowed-while-impersonating`), while withdrawing and disconnecting stay allowed and the
audit names the administrator. The playground shows the connect card in the chat, the preferences and
the organization's CRM account on the mock.
A tool that a remote MCP server offers is never passed on (SPM-362): an imported MCP tool may serve the
API's chats and agents, but its import cannot ask for the MCP exposure, so a partner that calls the
API's `/mcp` never reaches the remote server on the API's credential. To offer what the planning
service's `next_milestone` knows, the API writes its own tool, `projects.next-milestone`
(`ProjectPlanningMilestoneTools`): its own contract, view policy, read capability and exposure, a
project of the authorized division only, the project's key as the one argument it sends and a bounded
answer. `SamplePlanningMilestonesComposition` calls that one remote tool with the address and key of
`SampleProjectManagement:PlanningService`; without the section the tool fails closed with
`planning-not-configured`.
The other direction works too: an MCP client outside the application signs in to the API's own
`/mcp` (SPM-370). With `SampleProjectManagement:McpSignIn` naming the public URL of `/mcp`, the
application's authorization server and the scope `projects.read`, `SampleMcpSignInComposition` makes the
exposure a protected resource of that server (`UseProtectedResource`): `/mcp` answers a request without a
valid token with `401` and a challenge that names its protected resource metadata, which names the
authorization server. Any client that follows the MCP authorization flow registers there, lets the
person sign in and consent, and comes back with a token whose audience is the API's `/mcp`; the sample
has no client-specific code. The token's person is the account linked to its issuer and subject, an
Identity login (`SampleMcpSignInComposition.LoginInfo`), with that account's current rights: the
division editor lists and calls the project tools in Sample North, the viewer signs in but gets none,
and a subject linked to no account or a token for another address is refused. The token is never passed
on to the API's own endpoints. The API's own clients keep calling `/mcp` with their NewHeap token, and
without the section `/mcp` stays as it was. `McpSignInSamplesTests` runs the official MCP SDK
client through the whole flow against a stand-in authorization server; the authorization server, the
registration of clients and the consent stay the application's.

Whether the assistant uses compositions and the script tool is the portal's own setting (SPM-371). The
assistant settings page stores it per environment and agent in the portal's table
`AssistantCompositionSetting`, and `SampleAssistantCompositionSwitch`, registered with
`UseCompositionSwitch`, answers the assistant from that table through a short cache at the start of every
turn, run step and MCP request: a person switches the script tool on for the project assistant and the
next turn follows, without a restart or a configuration change. Code keeps the ceiling. The project
assistant allows both features in code, agents created in the administration may only get the script tool
(`UseCompositionsAsData`), as the scripting desk of the development seed does in its own definition, and
the MCP exposure allows the script tool and compositions, whose setting the page lists under MCP clients.
Next to each checkbox the page shows what the assistant actually does and why, from
`GET admin/agents/{id}/compositions`: for the scripting desk, compositions are not allowed for agents
created in the administration, and the script tool its definition uses is off by the application until
the portal's setting of the desk has it on. `AssistantCompositionSwitchSamplesTests` saves a
setting and asks the switch per environment and agent, and reads the effective state and the refusal of
compositions for an agent created in the administration over the assistant API.

A known chain of tools can run as one tool (SPM-372). `SampleProjectOverviewComposition` declares the
project overview, `projects.overview`, with `AddComposition`: it reads the project (`projects.get`),
then its open tasks (`projects.list-open-tasks`) and its members (`projects.list-members`) with the
project's id from the first result, and returns only the overview with the owner's name. The project
assistant calls it once instead of three times, so the model saves two rounds and never reads the
intermediate results. Every step is a governed call of its own tool with the person's rights and
active division, audited under the composition's call; a step that waits for an approval or fails
would stop the chain and return what succeeded. The overview only reads, so it runs at once and is
read-only over MCP, where the API's exposure allows compositions. It is offered where the project
assistant's ceiling allows compositions and the portal's setting has them on, and the agent's Activity
in the administration shows each call with the tool calls it ran in order, the rounds it saved and the
size of the result the model received (`GET admin/agents/{id}/compositions/activity`).
`AssistantCompositionSamplesTests` switches compositions on in the portal's setting, runs the chat turn
and reads the activity.

A chained question costs one call when the model writes a script (SPM-373). The project assistant's
agent context allows scripts, `AddNewHeapPlatformAIScripting` gives its scripts at most four calls and one
change, and where the portal's setting (SPM-371) has scripts on the assistant offers `run_script`, whose
description lists the turn's tools as TypeScript-style signatures. Asked which projects cover the roadmap
and the launch, the model searches both topics in one short program that runs on the server and returns
only the distinct keys; each search runs as the person's own call would, and the model reads the value,
not the search results. A call that does not match its signature stops the script with the exact
signature and the field to correct, so the next script gets it right. The script's memory budget counts
only what the program allocates, not what the searches allocate while they run. A change inside a script gets the
sample's approval exactly as the same call on its own (SPM-374): the status change stops the script, the
person sees the usual approval card and nothing after the change runs until they decide. The agent's
Activity lists a script call with its calls as it does a composition's.
`AssistantScriptSamplesTests` switches scripts on in the portal's setting and runs both on the sample's
host.

A date and a time of day are strings in every tool contract (SPM-375). `projects.due-by`
(`ProjectDeadlineAiTools`) lists the open projects due on or before a day, optionally before a time on
it, in UTC; its input has a `DateOnly` and a `TimeOnly`, and the generated schema writes them as strings
with the formats `date` and `time`, exactly as System.Text.Json reads and writes them. A script sees
`dueBy: string /* date */` and `before?: string /* time */ | null`, passes `"2026-11-01"` and `"12:00"`,
and the tool receives the date and time it meant; a date in another form, such as `"1 November 2026"`,
stops the script at `dueBy` before the tool runs. `AssistantScriptSamplesTests` seeds deadlines in the
host's PostgreSQL database and runs both scripts.

Collection cases SPM-028 and SPM-031 also demonstrate opt-in table field selection
through `ProjectController`, `ProjectTableViewModel` and
`ProjectFieldSelectionSamplesTests`. The protected discovery endpoint reports
authorized scalar fields; `fields` activates projection and field access while
omitting it preserves the existing response. JSON and optional MessagePack share
selection metadata. See [field selection](field-selection.md) for the compatibility
boundary, custom policies, provider tests and encoding measurements.

Media composition case SPM-177 consumes Common result contracts directly. Media's
Common dependency has an independent minimum of 6.0.0; the media regression suite
runs against that public package and current Common source, including both real
relational providers. Updating Common does not automatically raise Media's minimum.

Media authorization case SPM-181 shows that `ProjectMediaAuthorizationModule` decides every
mutation where it writes. `MediaMutationAuthorizationSamplesTests` lets a division manager try to
move a file and a folder into another division's projects folder and to retag a file there: the
library asks the module for a create at the destination and an update of the file, the active
division's path scope refuses them, and the other division's listing stays unchanged. A move onto a
name the folder already holds is refused, a viewer cannot localize, and only a file's title,
description and alt text can be localized (SPM-184).

The media routes themselves admit only signed-in users: `MapSampleProjectMedia` maps them with a
bearer policy, and the module authorizes a path in the active division's projects from the user's
current rights, the application permission `app.project.view` or `app.project.manage`, or the
division permission `project.view` or `project.manage` that `HasDivisionAccessAsync` reads from the
user store. No request header grants a permission. `MediaLibrarySamplesTests` sends every route
anonymously with a header that claims both permissions and gets 401, and shows that a North member
can read and delete North's documents but not South's.

Downloads (SPM-183) are served by the stored file, not by the name a request sends. The library route
and `MediaSamplesController` both open the file with `IMediaLibraryService.OpenDownloadAsync`, which
reads it through `DownloadFileAsync`, so its authorization and any override still apply, and returns the
stored reference with the content type `NhMediaContentTypes` lists for its extension.
`MediaLibrarySamplesTests` uploads an HTML page and a PNG: the page comes back as an
`application/octet-stream` attachment and the image inline as `image/png`, both with
`X-Content-Type-Options: nosniff`, so a browser saves the page instead of rendering it.

Proxy case SPM-238 is exercised by `ProxyContractBoundarySamplesTests`,
`ProxyLiteralRedirectSamplesTests`, `ProxyAdministrationSamplesTests` and the
standalone `SampleProjectManagement.Proxy` application. The preferred two-call
flow loads SQLite redirects before requests and provides embedded MVC management.
An opt-in regex checkbox enables captures over the escaped path and query string;
the target alone determines query values. Exact matching remains the default.
The panel authenticates one configured account, enforces optional IP restrictions,
audits login attempts, tests unsaved literal rules and saves/activates revisions
without restart. Real SQLite and HTTP tests cover persistence, conflicts,
authentication, CSRF, audit failure, PathBase and activation retry.

See [proxy administration setup](proxy-administration.md) for the runnable sample.
`ProxyHostAuthenticationSamplesTests` extends SPM-238 with host-owned ASP.NET
cookies and bearer tokens, independent UI/API policies, logout, scheme isolation
and API change auditing without local administrator credentials. Existing account
and Basic authentication remain defaults; either surface can migrate independently.
See [custom authentication](../../../docs/how-to/use-newheap-proxy.md#custom-authentication).
The **Proxy demo** launch profile needs no setup: it supplies a local test account,
isolated demo storage and one example redirect. The PowerShell smoke check verifies
the actual executable, health endpoints and persistence across restarts. The Aspire
AppHost also starts this profile as `sample-project-management-proxy`, with a
dashboard link to the panel, an assigned port and an independent SQLite file.
`ProxyAdministrationSamplesTests` also demonstrates administrator credentials bound
from environment variables, the recommended production setup. `Password` is hashed
at startup; a precomputed `PasswordHash` remains an alternative.
SPM-239 is exercised by `ProxyRewriteSamplesTests` and the embedded Rewrites panel.
Both proxy packages are published together in the independent `nuget-proxy`
unit; the sample uses local project references and releases retain the public
Common dependency version.
SPM-238 and SPM-239 support hosting at the origin root only. Subdirectory hosting
with a non-empty `PathBase` is unsupported and deferred until a concrete use case
requires it. Existing isolated PathBase tests do not establish full proxy support.
It demonstrates isolated managed-rule previews, SQLite rewrite persistence, native
YARP route/transform parity, independent confirmed activation and real forwarding.
Content headers appear in previews and the backend receives the intact body.
Local chain matching carries transformed general and content headers, including
multiple values and header replacements/removals.
The administration top bar tests a GET URL against saved redirects and rewrites,
showing the winning rule, target and edit link through `TestSavedAsync`.
`ProxyAdministrationSamplesTests` demonstrates `/old-projects?source=quick-test`
using the proxy's current scheme, host and port, including host-restricted matching.
It also verifies that a missing transform kind returns HTTP 400, preserves the
editable draft and leaves saved and active revisions unchanged. It checks complete
saved rewrite JSON, including the first path transform,
and the CSP nonce for the editor script. The developer-facing JSON and form fields
synchronize in both directions; invalid JSON is retained for correction.
Native YARP APIs/configuration cover rewrites from appsettings and other sources.
These sources and host customizations intentionally remain outside preview.
Regex with capture groups covers prefix and route-template redirects. Local
managed chains are checked before execution with a configurable MaximumChainDepth
of two by default; the sample proves refusal before an overlong chain reaches its
backend. External origins and backend responses remain outside chain analysis.
SQL Server and PostgreSQL remain explicit v1 provider gaps.

The samples are built directly on the public NewHeap surface. Collections use
the fluent request builder, and project mutations call one concrete application
service that owns the full transaction scope. Database writes are saved,
events are published through CAP inside that scope, and one commit follows only
afterward. There is no local base service or repository wrapper.

## Reusable test helpers

SPM-216 also includes `SampleProjectManagement.CommonConsole`, a standalone
Common consumer with no ASP.NET host. Run it with `dotnet run --project
examples/SampleProjectManagement/src/Back-end/Applications/SampleProjectManagement.CommonConsole`
from the Platform repository root. It exercises result propagation and queue
resolution and checks that its runtime artifacts contain neither ASP.NET nor
Hangfire SQL Server storage. `RepositoryFoundationSamplesTests` launches the
console and demonstrates the ModelState adapter from
`NewHeap.Platform.AspNet.Common` in HTTP composition. See
`docs/how-to/migrate-common-aspnet-boundary.md` in the Platform repository for
the migration from the former virtual TaskResult method.

The consumer test project references `NewHeap.Platform.Common.Test` and
`NewHeap.Platform.AspNet.Common.Test` for DI contexts, DbContext and repository
registration, `TaskResult` assertions, and NSubstitute predicates. These are
packable support libraries for consumers. NewHeap's own regression tests are
deliberately not in those packages; they live in non-packable plural `.Tests`
projects under `src/Back-end/Tests` in the Platform repository.

The in-memory DbContext helper is intended only for isolated unit tests. Query
translation, migrations, relational constraints, and transactions still require
evidence on real SQL Server and PostgreSQL providers.

SPM-309 keeps that relational evidence fast. `SamplePostgreSqlServer` is an
xUnit assembly fixture that starts one PostgreSQL container when a test first
needs a database. `CreateMigratedDatabaseAsync` gives every host a copy of a
template database that is migrated once per run, so parallel test classes never
see each other's rows. Hosts register `SampleTestClock` as their `TimeProvider`,
and a test moves it past a debounce or a lifetime instead of waiting.
`FastTestInfrastructureSamplesTests` proves both.

SPM-335 tests structured output where a provider checks it. `ProjectAiStatusDigestService` asks the
governed project model for a status digest whose two lists share one item type. The libraries pass
the schema on unchanged, so `ProjectAiStructuredOutput`, the sample's own code, generates it with
closed objects and every property required and moves the second list's reference into `$defs`.
`AiStructuredOutputSamplesTests` sends it to `NhAiDeterministicChatClient` with
`StrictStructuredOutput`, which refuses the schema as generated with HTTP 400, as a provider in
strict mode does, and accepts the normalized one. A digest whose call passes the run's deadline
returns the failed result `ai-model-timeout` instead of throwing. `AiAgentHistorySamplesTests`
extends SPM-225: the project agent keeps its session in the `InMemoryChatHistoryProvider` that its
request's `ChatHistoryProviderFactory` creates, and its second question reaches the model with the
first exchange through the governed client.

## Filtering

The frontend creates `ProjectCollectionRequestOptions` and adds only active
filters. The preferred approach uses fluent shortcuts such as `.equals()`,
`.isIn()`, `.like()`, `.orderAsc()`, and `.orderDesc()`. Composite trees use
`.and()` and `.or()` with `FilterRequestOptions`. Direct mutation of `filter`
and `orderBy` remains visible only as a lower-level alternative for dynamic
payloads and diagnostics. `NhApiService.getCollection` serializes paging,
search, order-by, and the filter tree into query parameters. The backend accepts
a filter only when the target field is annotated for it.

A nested key is checked segment by segment against the view models, in every
AND and OR node. `ProjectCollectionSampleService.GetByNestedFiltersAsync`
(SPM-034) filters projects with `ProjectNestedFilterViewModel`: its owner
navigation and task collection are `[Filterable]`, and so are the owner's
e-mail address and the task title. `ownerUser.email` and `tasks{any}.title`
therefore filter on SQL Server and PostgreSQL. A key that names a member the view
models do not mark filterable, such as `tasks{any}.completedByUserId`, returns a
safe `filter` error although the entity has that member, because the task view
returns the completing user without marking it filterable.

Order keys follow the same rule against `[Orderable]`. The nested view marks the
owner navigation and the owner's e-mail address orderable, so `ownerUser.email`
orders the projects. `creationDateTime` is orderable too, but an order key ends
there: a key that continues behind it, such as `creationDateTime.year`, is
ignored like an unknown order key, so the projects keep the default order by
name and nothing of the key reaches the database.

Rebuild filters whenever UI state changes so stale filter values do not remain
silently in the request.

## Root configuration and HTTP interceptors

The sample registers `NhCommonModule.forRoot(...)` exactly once at the
application root. Standalone and lazy components then import only
`NhCommonModule` for shared directives, pipes, and components. This follows the
existing consumer arrangement; the library's legacy providers deliberately
remain at module level for compatibility.

`deduplicateGetRequests` remains `false` by default in the library so an upgrade
does not silently change existing application behavior. The sample explicitly
sets it to `true` because that is the recommended setup for new implementations.
Only identical concurrent GET requests share an observable; `finalize` removes
the entry so a later request reaches the network again. In addition to URL and
query, the key includes `Accept`, language, authorization, cookie, and active
division. Requests from different users or divisions are therefore never shared.

The sample sets `apiBaseUrl` and `authApiBaseUrl` explicitly to `/api` (SPM-106).
The authentication and active-division interceptors add the access token and
`X-NH-ActiveDivisionId`, and the server interceptor forwards session cookies,
only to a request on the base URL's origin whose path is the base path or
continues it after a `/`. Project requests under `/api` therefore carry both,
while the translation files under `./i18n/`, a path such as `/apiary` and any
other origin receive neither. An empty base URL stands for the application's
own origin, so it never sends credentials to another origin, but an explicit
base states where the API is. `verify-frontend-integrity.mjs` keeps both bases
explicit; the library's interceptor spec proves the matching rule.

## Document head and structured data

The platform playground sets the title, the description and a canonical link
(SPM-151) and builds JSON-LD with `NhJsonLdService`, review placeholder keys and
`<nh-json-ld nhToHead>` (SPM-152). The description starts as rich text:
`NhMetaService.stripHtml` returns its text and parses the markup in an inert
document, so images in it never load and inline handlers never run.

Review text is written by people and may contain markup, as the sample's review
body does. `nh-json-ld` writes every `<` in the serialized JSON as a
JSON unicode escape and escapes the line and paragraph separators, so that text
stays data inside the one structured-data script element and parses back
unchanged.

## Backend configuration for runtime and automation

The API and AppHost explicitly pass their `args` to the NewHeap configuration
extensions. Runtime, migration tooling, and pipelines therefore use the same
appsettings and secret substitution. Environment variables override files, and
CLI arguments have the highest precedence. This order also applies during the
initial step that resolves `NewHeap:PlatformCommon:AppSecretsDirectoryPath`.

A production appsetting can therefore contain a Linux path while a Windows
runner points `NewHeap__PlatformCommon__AppSecretsDirectoryPath` at its own
temporary secrets directory. In automation, prefer passing sensitive values such
as connection strings directly as environment variables; CLI is more suitable
for non-secret host settings.

## Read-only database diagnostics

SPM-237 provides a checked-in `.newheap/database-read.json` profile, a typed
JSON request, and an executable validation test for `newheap-db`. The request
keeps the project ID in the parameter collection instead of concatenating it
into PostgreSQL text and applies `LIMIT` in addition to the request-level row
and timeout limits. Standard input carries the request and standard output
contains one schema-versioned JSON result, which makes the contract suitable
for developers, Codex, and other agent environments.

Validation is deliberately local and returns `requiredCapabilities` with
`outbound-network` without opening a connection. Schema and query execution do
require outbound database access. The checked-in agent guidance therefore tells
Codex to use a narrow reusable `newheap-db` network permission on the first
execution attempt instead of first probing a restricted sandbox. Connection
failures return safe provider metadata, an execution stage and a retry hint;
authentication and unknown-database failures remain distinct, and raw server,
credential and exception details are never exposed.

For an agent investigating persisted sample data, the repository profile, local
database-read README and checked-in request files form a direct fast path. The
agent makes one bounded tracked-file search when it starts above the backend,
uses the directory containing the only nested catalog as its diagnostic working
directory, and selects the catalog's only `sample-development` profile without
asking for an environment. It makes only the schema calls needed to confirm
deployed identifiers, validates the bounded query request, executes it and stops
when the requested evidence is available. It does not inspect controllers,
entities, EF configuration, appsettings, secrets or package internals merely to
reconstruct routing or JSON fields that the repository already declares.

The profile selects the existing NewHeap application configuration and the
`NewHeapDiagnosticsReadOnly` connection-string name. The actual connection
string is resolved by `newheap-db`, remains in the normal secrets file and must
use a separate login with only the approved `SELECT` permissions. The parser,
transaction rollback,
timeouts, and row/output limits are additional safeguards; the database login
is the security boundary. Platform integration tests prove that the same login
can read but cannot update on real SQL Server and PostgreSQL instances while
the diagnostic queries use provider-native `TOP` and `LIMIT` caps.

A consumer whose diagnostic database is MySQL or MariaDB names a `mysql` or
`mariadb` profile (SPM-367). The tool first checks that the server is that
flavor, then verifies the principal from the server's own `SHOW GRANTS` with
every role it can use active, including mandatory roles on MySQL and the grants
to `PUBLIC` on MariaDB, and rejects anything beyond SELECT outside the system
schemas, as well as a view that runs as another definer. The transaction starts
read only with the flavor's statement and lock limits, and schema evidence comes
from `information_schema`. A SQL Server profile may opt in to SELECT on inline
table-valued functions (SPM-368) with `sqlServerFunctionPolicy:
inline-table-valued-select`; the tool then accepts a function only when it is
schema-bound and depends on tables and views that the principal may select
itself, which it reads with `VIEW DEFINITION` on the function. The sample's own
database is PostgreSQL, so the sample MCP shows only how the new verification
checks and a profile of the wrong flavor reach the agent, and its tests validate
such profiles without a connection; the platform's provider tests prove both
features on real MySQL 8.4, MariaDB 11.4 and SQL Server servers.

## Recommended deferred lazy dropdown

`deferLazyLoadUntilOpened` is also disabled as a library default for backward
compatibility. SampleProjectManagement explicitly enables it in the shared root
configuration. The utility playground demonstrates the recommended flow live:
an already selected value is fetched immediately through
`selectedLazyLoadLambda`, while the full collection loads only when the control
is first opened. Request counters make the difference visible.

Identical concurrent lookups for the same selection share one active request,
and the completed selection is reused while the value stays the same. When the
value changes, an old active lookup is terminated. An existing screen can opt
out locally with `deferLazyLoadUntilOpened: false`, but new samples present the
opt-in as the preferred approach.

## Public, composite, and short surfaces

`GET /public/projects` derives directly from `PublicNhBaseController`, is exposed
with `AllowAnonymous`, and obtains its query policy from
`ProjectCollectionSampleService`. The controller therefore contains only HTTP
and collection mapping; the choice to return only active and completed projects
remains in the service.

`/project-composites` uses the concrete NewHeap composite controller and service
bases end to end. `ProjectCompositeService` delegates domain mutations to
`ProjectService`, so the composite surface does not acquire a second set of
business rules. The NewHeap mapping profile builds the project with its tasks.
The sample registers its profile through the source-compatible
`ConfigureAutoMapper` entry point. `NewHeap.Platform.Mapping` applies its
recursion-depth guard to both platform and consumer mappings, maps nested task
collections, and keeps entity navigation references intact when a mutate model
does not expose those members. `ProjectMappingFeatureProfile` also uses
`IncludeBase` to inherit a configured base view into a derived detail view and
maps status metadata into an `IReadOnlyDictionary`, providing executable
evidence for base-map and dictionary compatibility.

`CircularMappingSamplesTests` demonstrates a project and assignment read graph
with a back-reference. The mapper preserves the project destination within its mapping context, allowing
JSON `ReferenceLoopHandling.Ignore` to omit the loop. The serialized result can
be read with `MaxDepth = 32`. Write requests should still contain only the
explicit mutate fields, without read-model navigation graphs.
`MappingCompatibilitySamplesTests` additionally proves that public destination fields retain source descriptions, as well as shared graph reuse,
collection depth boundaries, inherited write conditions, runtime derived views,
null converter defaults, current-culture imports, enum name conversion and
flattened read models with private setters.

`GET /projects/short` executes the real short-projection extension with an
explicit `Project -> ProjectShortViewModel` expression. The source query defines
a stable name order and the response contains only ID, key, and name.

## Expression resolver

The collection playground calls
`GET /projects/expression-resolver?taskTitle=...`. The service maps the public
key `open-task-title` through `WithFilterable` to
`project.Tasks.Select(task => task.Title)`. The resolver turns this into
`Tasks{any}.Title`, which is valid request notation for an any filter on task
titles.

The service then runs that filter for the requested task title and returns the
match count. Collection processing treats a navigation declared as
`ICollection<ProjectTask>` as a collection step, so the inferred key executes as
an any filter over the tasks.

## Partial update

`ProjectController.UpdatePartial` exposes a protected, authorized `PATCH` route
that accepts a top-level JSON object and delegates directly to
`DoUpdatePartial`. The controller allow-lists status, deadline, and description;
the base controller maps only supplied properties to typed setters and rejects
unknown, invalid, or forbidden properties before calling the service.

Both Angular applications exercise that endpoint through
`ProjectApiService.updateStatus`, which delegates to
`NhBaseApiService.updatePartial<void>(id, { status })`. The shared library sends
an HTTP PATCH to the standard entity route. Because the endpoint returns 204 No
Content, the applications apply the known status locally only after success and
restore the previous selection after an error.

The service loads the existing entity, builds the regular mutate model, applies
only the mapped setters, and runs `PreparePartialUpdateMutateModelAsync` before
the normal update validation. The project service uses that hook to trim and
normalize the patched description consistently with a full update.
`ProjectService.UpdatePartialAsync` keeps the transaction and project-update
event in the application service. The JSON mapper also respects configured JSON
names and property-level Newtonsoft converters.

For domain-specific commands, an explicit setter remains a useful lower-level
alternative. `ProjectController.UpdateStatus` calls the status service method,
which ultimately selects the field with:

```csharp
calls => calls.SetProperty(x => x.Status, mutateModel.Status)
```

## Bulk update

The HTTP mutate model contains only serializable data: IDs, status, and
`ContinueOnError`. The controller translates this into the delegate structure
expected by `BulkCRUDMutateModel.UpdatePartial`. `BulkAsync` manages save
changes, the transaction, and rollback. The response reports success and errors
per item.

Do not use `Promise.all(ids.map(update))` when the operation must be atomic; those
are separate HTTP transactions. The status bulk response therefore preserves
every `BulkCRUDResultModel` item as an ID, success flag, and error list. The UI
splits those results into successful and failed groups; counts are only a
summary of those concrete results.

## Transaction and CAP outbox

The API uses CAP with `UseEntityFramework<SampleProjectManagementDbContext>` and
the RabbitMQ resource from Aspire. The domain write and outbox record therefore
share the same SQL transaction. The application service publishes before
`CommitAsync`; CAP may send the message to RabbitMQ only after the commit.

`POST /projects/transaction-rollback-sample` is a deliberate diagnostic case.
The service stores a project, publishes the event in the same scope, and then
calls `RollbackAsync`. The response contains the project ID and event ID. The
project must subsequently return 404 and the event ID must not appear in
`GET /library-samples/events`. The Transactions workbench performs both checks
live without putting domain logic in the controller.

When `PublishAsync` throws, `ProjectService.CreateAsync` explicitly rolls back
and never calls `CommitAsync`. Nested partial and bulk operations receive a
non-owning scope; only the outer service can commit the real transaction.

`POST /project-composites/with-initial-task` also demonstrates the explicit
`ITransaction` variant. `ProjectSetupService` opens the outer transaction, lets
`ProjectService` and `ProjectTaskService` participate in it, and commits only
after both mutations and their events succeed. A failure in either step rolls
back the complete setup.

## Background operation cancellation

The project-analysis child of the portfolio analysis checks
`ThrowIfCancellationRequestedAsync` before every pass (SPM-325). When its owner
cancels, the handler stops at the next pass instead of waiting for the next
heartbeat, and the operation ends `Cancelled`, not `TimedOut`: the runner reports
a time-out only when the soft time-out fired, and otherwise checks the attempt's
durable cancellation. `BackgroundOperationCancellationSamplesTests` runs the
child through Hangfire on the migrated sample database with a heartbeat a minute
apart and proves that no heartbeat ran before the operation ended cancelled. The
library's provider tests also show that a failure code or message key longer
than its column completes the attempt with the generic code and key, and that a
failed write of a failed step keeps the step's own exception.

After its passes the child stores its analysis in `ProjectAnalysisArchive`, the
sample's stand-in for another system, outside a step. Like some client
libraries, the archive reports a cancelled call as an error of its own, and the
handler lets that error travel instead of translating it. When the owner cancels
during that call, the heartbeat sees the request and cancels the call, and the
runner, which finds the attempt's durable cancellation, ends the operation
`Cancelled` with its single attempt and logs the archive's error as a warning;
without a cancellation request the error would fail the attempt as
`handler-failed` and retry it. The second test of
`BackgroundOperationCancellationSamplesTests` proves it with heartbeats 200 ms
apart.

## Agent run capacity and the run of an event

`SampleAgentRunCapacityComposition` places the assistant's background operations
on the queue `agent-runs` (SPM-326). The queue keeps two of the eight slots of
the global limit for itself, so a burst of portfolio analyses cannot take every
slot while an intake review waits, and runs at most four operations at the same
time, so the agents leave the other shared slots to the rest of the work.
`Program.cs` and the agent run sample host share the composition. When a
project is created, the intake reviewer's receipt names the queued input that
holds the event, and the maintenance page finds the review by the event's id
(`GET /agent-maintenance/intake-reviews/{eventId}`) through
`INhAssistantAgentRunLookup`: the input, the run it started and that run's
status once it claimed the event. A redelivered event finds the review of its
first delivery. The page then reads the review run's facts by its id
(`GET /agent-maintenance/intake-reviews/{eventId}/run`) with
`INhAssistantAgentRunLookup.FindRunAsync`: its agent, project, thread, trigger,
status, failure, attempt, proposal count, summary and result, without reading the
assistant's tables. The library tests prove the reserved slots on both providers,
the lookup for every kind of fire, the run facts within their tenant and a host
with `RunSchedules` off.

An operator switches agent work off on the API host with
`NewHeap:AI:Assistant:AgentWork` (SPM-365), which `appsettings.Development.json`
names with its default `true`, and the host keeps serving. `GET status`, the
administration and the maintenance page report `agentWork: false` and no agent is
active, while the agent list says so in a banner. A new project's intake review is
refused with `assistant-agent-work-off` and starts no run; the owner keeps chatting,
and the project assistant's consultation of the intake reviewer answers that code as a
tool result. Once the configuration switches agent work on and the host reloads it,
the next review starts. The library tests prove every kind of agent work refused on
both providers, approved proposals held and released by the maintenance job once
agent work is on, and a person's chat and inline decisions working throughout.

## Edit modal

`ProjectEditModalComponent` supports both create and update operations. The
mutate model deliberately contains no `creationDateTime` or
`lastModifiedDateTime`. The register opens the modal as follows:

```ts
const modal = modalService.open(
  ProjectEditModalComponent,
  new NhModalOptions({ modalClasses: 'large' }),
  { project, lifecycleReporter }
);
```

The modal content derives from `NhModalMutateBaseComponent`. `appOnInit` chooses
create or update and initializes form data; `appAfterViewInit` reports that the
view is available, and `appOnDestroy` performs component-specific cleanup. The
Angular hooks owned by the base are not overridden. `NhModalComponent` itself is
the shell managed by `NhModalService`, not the base for consumer content.
The large editor with the project's agent work is taller than the page: it
scrolls vertically within the modal's holder, and the shell keeps the modal next
to the holder's scrollbar, so nothing scrolls sideways (SPM-116).

The editor works with the keyboard alone, without code of its own (SPM-115). The shell is a dialog
named by its title, with a translated close label. It focuses the close button while the form data
loads and the key field once the form is there, keeps Tab and Shift+Tab inside the dialog, skips the
disabled save button, and gives focus back to the "Edit" button when it closes. Escape closes the
topmost modal that can close: for the editor it calls `closeDialog` through `onModalEscape`, as
"Cancel" does, and the processing modal of the platform playground, opened with `closeable: false`,
ignores it.

After `created` or `updated`, the register updates the concrete row and then
closes the modal. The parent subsequently receives the `closed` event through
`modal.onClose`, disposes both content subscriptions, and displays the event
order in the UI. This also works with local data in sample mode; with a connected
API, the exact same component uses `ProjectApiService`.

## Session expiration and mutation integrity

`provideSampleProjectManagement(...)` starts one `SampleAuthSessionService` for
both Management and Workspace. The coordinator observes
`sessionExpirationInformationChanged`. When a previously valid session expires,
it clears local token state and navigates to `/auth/login` with
`reason=session-expired` and a local `returnUrl`. The login page explains the
redirect. In the authentication workbench, the Start 10-second session button
starts a reproducible end-to-end check while the NewHeap expiration stream shows
the remaining time live.

A session can also end on the server before it expires, for example after a
password change on another device. `NhCommonModule` then answers the first 401
with one check against the account endpoint; when that endpoint rejects the
session too, `BaseNhAuthService` clears it and reports `rejectedByServer`. The
coordinator treats that as an expired session, so the user lands on the same
login message instead of a page whose requests and notification polls keep
answering 401. To reproduce it, restart the sample's AppHost while you are
signed in: the recreated database no longer knows the session.

Project mutations also use explicit connection state. Only a browser network
failure with HTTP status `0` enables local demo mode. A server response with 400,
401, 403, or 500 proves that the API is reachable and must never cause a local
success mutation. A rejected status update therefore restores the previous
selected value in both apps. Create, update, delete, and bulk operations display
success only after the API confirms the action; a local simulation has its own
visual status and explicitly says that the API was not changed. Mutation
controls are visible only with the same `app.project.manage` permission enforced
by the endpoints. In addition to the evidence check, `npm run verify:samples`
runs `verify-frontend-integrity.mjs` for this behavior.

## Form validation and collection lifecycle

The interaction playground derives directly from `NhCollectionBaseComponent`.
Search, sorting, and paging pass through `beforeLoad`, `onLoad`, and `afterLoad`;
the latest events remain visible. `appOnInit` demonstrably runs before that first
load cycle, and `appAfterViewInit` only after the view becomes available. The
Angular hooks remain owned by the base so request state and the active request
subscription are preserved. The real base lifecycle sends request state to
`ProjectApiService` and falls back to the same local dataset when no API is
available.

The ModelState button retrieves a real nested error from
`GET /library-samples/validation/model-state`.
`NhServerSideFormValidationService` places `project.name` on the nested control
and the empty key on the form. The TaskResult button runs
`NhTaskResultFormValidationService` with one field error and one general error,
so both error channels remain independently visible.

## Page lifecycle and dirty navigation

`DirtyRouteSampleComponent` derives from `NhPageBaseComponent` and displays the
order of `appOnInit`, `appOnInitAndLoad`, and `appAfterViewInit` in the UI.
`appOnInitAndLoad` is the route-sensitive loading point; `appOnDestroy` is the
component-specific cleanup point. The sample deliberately does not override
`ngOnInit`, `ngAfterViewInit`, or `ngOnDestroy`, allowing the base to keep
managing metadata, breadcrumbs, route parameters, and every subscription. Its
independent project-summary request is started with `void ...catch(...)`, so page
metadata and the remaining lifecycle can continue without losing error
observation; `takeUntilDestroyed` makes component destruction safe. This is an
explicit scheduling choice: consumer code awaits a task whenever later
initialization depends on its result, and only detaches it when repeated
invocations, errors, cancellation, and stale results are safe. The same page
implements `ICancelNavigationComponent`, so dirty state activates
`NhCanCancelNavigationGuard`.

## Match-one authorization

`GET /library-samples/authorization/match-one` executes the real
`ClaimMatchOneAuthorizeAttribute` contract. A user is allowed with either the
`app.project.manage` permission or the `administrator` role; without either,
the endpoint returns `Forbid`. The attributes describe the rules while
`ProjectAuthorizationSampleService` is the concrete enforcement point.

## Application, division, and project authorization

The authentication playground contains four Development accounts with the same
local password, `Sample123!`. The accounts make the distinction among the three
scopes visible and verify it against real endpoints:

| Account | Assigned access |
|---|---|
| `sample@example.test` | manager role with application-wide view, manage, confidential and assistant permissions; division editor and division manager in Sample North, so the assistant and its administration work and it manages the memberships of Sample North; no project rights and no membership management in Sample South |
| `viewer@example.test` | viewer role with only the application-wide view permission |
| `division-editor@example.test` | division role with project permissions in the active Sample North division |
| `project-editor@example.test` | project-specific `confidential.view` permission and `project-editor` role for Authorization Alpha |

For global access, the backend uses the existing application claim; for division
access, it uses the existing division role and claim relationships.
`ProjectPermission` is deliberately a consumer implementation: the claim type,
requirement, handler, and frontend pipes live in the sample. The claim value has
the form `{projectId}_{permission}`. The handler also verifies that the project
belongs to the active division before a project-specific claim grants access.

The precedence is explicit: an application permission applies everywhere, a
division permission applies inside the active division, and a project permission
applies only to the named project. The UI uses the same hierarchy to show or
hide actions. Probe buttons remain available so a hidden action can also be
shown to receive a `403` response from the API.

The memberships of a division are managed under `/division-memberships` by
`DivisionMembershipController`, a thin controller derived from the library's
`NhBaseDivisionUserController`. Its `app.division.view` and `app.division.manage`
policies are division policies (`SampleDivisionPolicies`), and the manager holds
the `sample-division-manager` role in Sample North only. With Sample North
active, a body that names Sample South returns `403` and a Sample South
membership returns `404`, because the inherited update and delete first
resolve their row through the controller's `GetQueryableAsync`. A lockout the
manager sets on a membership removes that member's claims for Sample North from
the next request until the lockout ends; the other members keep theirs. Its
dates count in whole UTC days of the application's clock, from the start day
through the end day, so a lockout that ends at midnight UTC still applies that
day. The security officer, without an active division or memberships, keeps the
application permissions. Membership is also what a division check without requirements asks for, such as
the gate of the division-exclusive portfolio operations: the division editor
works in Sample North only, and a member without division roles is still a
member. `SampleRuntimeAuthorizationClaims` keeps the membership claim out of the
token with the other division claims and restores it per request.

## Authentication service overrides and request claims

The preferred route for consumer-specific authentication behavior is to preserve
the existing NewHeap flow and replace only the service:

```csharp
options.WithAuthenticationService<SampleAuthenticationService>();
```

`SampleAuthenticationService` derives directly from `NhAuthenticationService`.
The sample override normalizes the username and customizes `GetClaimsAsync`:
stable application claims remain in the JWT, while large and mutable division
and project claims stay out. Password login, cookies, refresh tokens,
impersonation, and account endpoints therefore remain standard NewHeap endpoints.

For backend authorization, the omitted claims are restored before every request
through the standard ASP.NET Core hook:

```csharp
builder.Services.AddScoped<
    IClaimsTransformation,
    SampleRuntimeClaimsTransformation>();
```

The transformer reads current claims through `INhUserManager`, stores the result
in `HttpContext.Items`, and adds only missing type and value combinations.
Multiple authentication calls in the same request therefore cause neither an
extra database read nor duplicate claims. The account endpoint continues to send
the complete claim set to the frontend, so frontend pipes see the same rights.
If the user behind a still validly signed token no longer exists, the user
manager returns an empty claim set and the transformer makes the principal
anonymous. The request then ends with `401` through the normal pipeline.

`GET /authorization-samples/overrides/runtime-claims` displays the authentication
service that was actually selected and the claims rehydrated for that request.
The authentication playground executes this endpoint interactively.

## Two-factor authentication

`SampleTwoFactorComposition` enables every second factor on the standard
authentication builder, and `Program.cs` maps the two-factor and passkey
endpoints next to the password endpoints:

```csharp
options.AddTwoFactor(twoFactor => twoFactor
    .EnableAuthenticator(authenticator => authenticator.Issuer = "Sample Project Management")
    .EnableRecoveryCodes()
    .EnableEmailCodes()
    .UseSecurityNotifications()
    .UseMessageComposer<SampleTwoFactorMessageComposer>()
    .EnableRememberDevice(TimeSpan.FromDays(30))
    .EnablePasskeys(passkeys => { /* relying-party domain and allowed origins from configuration */ })
    .RequireFor(requirement => requirement.Roles(SampleAuthorizationDefaults.SecurityOfficerRole))
    .UseAdministrationPolicy(SampleAuthorizationPolicies.TwoFactorAdministration));

authentication.AddTwoFactorEndpoints();
authentication.AddPasskeyEndpoints();
```

`Program.cs` and `TwoFactorSamplesTests` share that composition, so the tests
prove the configuration the API runs with. `WithBackgroundOperations` registers
the policy operations with `AddTwoFactorOperations<NhUser>()`, and e-mail codes
and security notifications use the configured notification pipeline.

`SampleAuthenticationService` passes `NhTwoFactorAuthenticationContext<NhUser>`
to its base constructor, and its PIN sample completes the first factor through
`CompleteFirstFactorAsync`. An enrolled account therefore receives the same
second-factor challenge after a PIN as after its password.

`SampleProjectManagementDbContext` overrides `IncludeIdentityPasskeys`, and the
`AddIdentityPasskeys` migration adds only the `AspNetUserPasskeys` table. WebAuthn
needs a domain: open the applications on `http://localhost:4210` and
`http://localhost:4220`, not on `127.0.0.1`, to register and use passkeys. The
`TwoFactor` section of `appsettings.json` holds the relying-party domain and the
allowed origins.

The shared login page uses the components from
`@newheap/platform-common/two-factor`, loaded with `@defer` only when a sign-in
needs them:

- `nh-two-factor-challenge` completes a challenge with an authenticator code,
  an e-mailed code, a recovery code or a passkey, and can remember the device.
- `nh-two-factor-enrollment` enrolls an authenticator or a passkey when the
  policy requires a second factor, then shows the recovery codes once.
- `nh-passkey-login-button` signs in without a username or password.

The shared profile page embeds `nh-two-factor-settings`, which manages the
authenticator, e-mail codes, passkeys, recovery codes and remembered devices and
asks for the password or a code before sensitive changes.

The Development seeder also adds `project-lead@example.test` for the assistant and
the agent-run pages (see above), and two accounts for two-step verification, all
with the password `Sample123!`:

- `two-factor@example.test` is enrolled with the fixed key
  `JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP`. Add the key to an authenticator app to
  complete its challenge.
- `security-officer@example.test` is in the `sample-security-officer` role, which
  requires a second factor. It has none, so its first sign-in enrolls one.

Managers have the `app.two-factor.administer` permission. The authentication
playground calls every two-factor endpoint directly: the challenge, e-mail codes,
passkeys, required enrollment, remembered devices, and the administration reset
and background operations. `SampleTwoFactorMessageComposer` renders the e-mailed
code with the sample's Razor mail layout and sets `ExpiresAt` on the e-mail
delivery to the code's expiry, so the notification processor removes the code from
the delivery store once the e-mail is sent and never keeps it after the code
expired. `TwoFactorMessageComposerSamplesTests` proves that expiry.

`TwoFactorSamplesTests` applies the PostgreSQL migrations, confirms that the model
matches them and includes the passkey table, completes the seeded account's
challenge with `NhTwoFactorTestCodes` from the reusable test package, and enrolls
the security officer during sign-in. Its recovery codes are stored as keyed hashes
under `NewHeap:PlatformAspNetCommon:Authorization:TwoFactor:CodeKey`, which
`appsettings.json` reads from the secrets file next to the JWT key.

## API-to-API client

`SampleProjectManagementApiService` shows how a backend service calls another
NewHeap API. Registration is placed directly on the service provider:

```csharp
builder.Services.AddNhApiClient<SampleProjectManagementApi>(
    builder.Configuration.GetSection("ApiClients:SampleProjectManagement"));

builder.Services.AddScoped<
    ISampleProjectManagementApiService,
    SampleProjectManagementApiService>();
```

The `SampleProjectManagementApi` marker lets multiple endpoint services share one
client configuration. The library registers the named `HttpClient`, factory, and
optional authentication handler itself.

The derived service only needs to describe the endpoint:

```csharp
public sealed class SampleProjectManagementApiService
    : BaseNhApiService<SampleProjectManagementApi>
{
    public SampleProjectManagementApiService(
        ILogger<SampleProjectManagementApiService> logger,
        INhApiHttpClientFactory<SampleProjectManagementApi> httpClientFactory)
        : base(logger, httpClientFactory)
    {
    }

    public Task<TaskResult<SampleApplicationInfoModel>> GetApplicationInfoAsync(
        CancellationToken cancellationToken = default)
    {
        return DoGetAsync<SampleApplicationInfoModel>("/", cancellationToken);
    }
}
```

The Aspire AppHost places the current HTTP endpoint URL in
`ApiClients__SampleProjectManagement__BaseAddress`. As a result,
`GET /samples/api-client` makes a real API call back to the anonymous root
endpoint without hard-coding a port.

For a protected target API, add `Authentication` under the same configuration
section. Configure the password through secrets or environment variables, not
in the committed JSON file.

## Audit: library boundaries

The additional audit checks public library behavior around `ChunkAsync`, outer
transaction scopes, Hangfire, and locks. Complete business flows, the CAP
outbox, publication before commit, and rollback are present as executable
SPM-189 through SPM-200 cases. SPM-201 through SPM-209 also test boundaries that
are easy to miss: batch size and cancellation, server-side semaphore release
after a failure, safe formatting, and Identity and JWT transformations.

Application-specific channels, pipelines, and integrations are not separate
NewHeap cases when they demonstrate no public library surface. Only a real
external boundary receives a separate concrete sample.

## Complete library coverage

The traced catalog is in the
[NewHeap library sample plan](library-sample-plan.md). The Angular catalog is
generated for every build from that plan and
`sample-implementation-status.json`.

The current status is **215 implemented, 0 partial, 0 planned, and 3 gap cases
out of 218**. Implemented cases show evidence paths to tests, endpoints, or
frontend code; only the remaining gaps stay explicitly visible.

- SPM-033 is now a working resolver example: `WithFilterable` accepts the selector, generates `Tasks{any}.Title` and runs it over the task collection. SPM-031 and SPM-032 cover concrete collection filtering separately;
- SPM-102: the OneOf OpenAPI transformer does not yet construct variants;
- SPM-113 and SPM-160: the SampleProjectManagement frontends do not yet have their own Angular SSR target and host.

This makes completed work and focused follow-up immediately visible; nothing is
incorrectly presented as implemented.

## Explicit ASP.NET database providers

SPM-051 selects `NewHeap.Platform.AspNet.Common.SqlServer` or `.PostgreSql` through `UseNewHeapSqlServer` and `UseNewHeapPostgreSql` in API composition. SPM-053 uses the same per-context registration for native bulk upsert. Common remains free of both database provider stacks. Hangfire storage is configured explicitly in its callback. `DatabaseProviderSamplesTests` verifies both composition variants; the library boundary tests and real-provider scenarios cover package isolation, bulk execution, locks, and identity-context configuration. See `docs/release-notes/v-next.md` for the required upgrade changes.

## Proxy URL diagnostics

SPM-238 and SPM-239 also demonstrate an optional server-to-server management API
in `ProxyApiSamplesTests`. `MapProxyEndpoints()` opts into Basic authentication,
revision-checked redirect/rewrite snapshot updates, status, activation retry and
isolated previews. The standalone proxy maps it only with `ProxyApi=true`;
otherwise the API remains unavailable. The tests use real SQLite and verify
OpenAPI contracts. See [API setup](proxy-administration.md#optional-server-to-server-api).
API requests do not create login records or consume browser login attempts.
Configuration changes are stored with before/after snapshots and actor attribution
in the same SQLite transaction, with a separate persistent audit accessible through
`GET /newheap-proxy/api/audit`. Reads, previews and rejected saves create no changes.
SPM-239 also verifies native transform construction and regex constraint compilation
before saving: malformed patterns are rejected, including in disabled rules, while
persisted revisions and change-audit counts remain unchanged.

SPM-239 demonstrates saved URL and rewrite draft tests with per-rule diagnostics.
The panel shows rule names, paths, mismatch reasons and explicit skips; native
routing checks rewrite conditions and identifies matching alternatives that lose
on priority or specificity. SPM-238/239 exercise the embedded panel and SQLite.
SQL Server and PostgreSQL proxy stores remain explicit provider gaps.
