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
| Operations API | CAP, Hangfire, email, and notification delivery | event configurator, `NhHangfireUtil`, `NhMailService`, notification builder |
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
its own background operation, and an edit in between closes it as stale. An approver
may also answer with a messaging reply: `SampleApprovalReplyChannel` asks
`INhAssistantProposalDecisionService` whether the person may decide before it issues
them a reply token, and `POST approval-replies` decides their reply as them with the
checks of the decision endpoint; a reply delivered twice returns the earlier decision,
and the audit names the `messaging-reply` channel and the delivery. Project
members and their roles decide who sees the project's agent thread, through
`ProjectSubjectAccessResolver`. A participant can also start the intake reviewer to
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
and deleting a project deletes its agent thread (SPM-276).
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
`SampleAccessSourceComposition` records the claims of every signed-in request after the transformation
in the application's own `SampleAccessContexts`, under the platform's actor id and with the time, and
registers it with `UsePrincipalsFromAccessSource` and a maximum age of half an hour; NewHeap stores
none of it. A run the manager started on a project changes its status with the rights of the manager's
last request, and so does an agent that acts with the rights of the project's responsible person; the
colleague without the manage permission and someone the application never saw are refused. Half an hour
later the run is no longer offered the status tool, until the manager's next request; signing out
forgets the rights at once. `AssistantAccessSourceSamplesTests` shows it in a focused composition,
because the sample's own API signs in with the NewHeap user store.
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
person's principal outside that person's request. The reviewer's consultation names its tools
with `Tools(...)`: asked about a project it may only request a review, so its status and task
tools stay with its own runs; a status change its model names anyway runs nothing. The consult tool only ever acts for a
person: an agent created in the administration that acts with the operations account's
rights is not offered it in a run that a created project starts, because it does not
delegate from the person who created the project.
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

An agent is not started again by its own change (SPM-308). The project service stamps the cause on
every status event: the person, or the agent whose status tool changed the status with the subject it
worked in (`ProjectAgentCause`, from the tool's invocation context and the ambient agent invocation
scope). The planner's composition registers the status-changed event with the named filter
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
proposal's card. `AssistantAgentRunSamplesTests` runs the case, with the desk's confirmations standing
in for the supplier's callback.

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

An administrator connects MCP servers that OAuth protects for the whole organization (SPM-299).
Before trusting a planning server, `POST admin/mcp-servers/discover` reads which authorization
server protects its address, from its challenge and protected resource metadata; saving the server
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
consented while connecting; the audit names whose connection a call used. The playground shows the
connect card in the chat, the preferences and the organization's CRM account on the mock.

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

## Filtering

The frontend creates `ProjectCollectionRequestOptions` and adds only active
filters. The preferred approach uses fluent shortcuts such as `.equals()`,
`.isIn()`, `.like()`, `.orderAsc()`, and `.orderDesc()`. Composite trees use
`.and()` and `.or()` with `FilterRequestOptions`. Direct mutation of `filter`
and `orderBy` remains visible only as a lower-level alternative for dynamic
payloads and diagnostics. `NhApiService.getCollection` serializes paging,
search, order-by, and the filter tree into query parameters. The backend accepts
a filter only when the target field is annotated for it.

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

This sample does not change the existing URL matching or token logic of the
authentication, active-division, and server interceptors. The application
therefore configures explicit API bases and demonstrates only optional request
deduplication.

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

The remaining boundary is not the request syntax. In the current generic
processor, a navigation declared as `ICollection<ProjectTask>` is not recognized
as a generic collection during the final expression construction step. It then
tries to read `Title` from `ICollection` itself. The sample isolates this
behavior and shows the generated key; a concrete collection shape can execute
the same request.

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
| `sample@example.test` | manager role with application-wide view, manage, confidential and assistant permissions; division editor in Sample North, so the assistant and its administration work, and no project rights in Sample South |
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
code with the sample's Razor mail layout.

`TwoFactorSamplesTests` applies the PostgreSQL migrations, confirms that the model
matches them and includes the passkey table, completes the seeded account's
challenge with `NhTwoFactorTestCodes` from the reusable test package, and enrolls
the security officer during sign-in.

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

- SPM-033 is now a working resolver example: `WithFilterable` accepts the selector and generates `Tasks{any}.Title`. SPM-031 and SPM-032 cover concrete collection filtering separately;
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
