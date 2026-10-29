# v-next

## @newheap/platform-ai-chat: a prepared draft in the composer

`NhAssistantPanelService.open` takes `NhAssistantPanelOpenOptions` next to a conversation id:
`conversationId`, `agentId` (a new conversation with that agent), `draft` and `replaceDraft`. The draft
shows in the composer for the person to review and send and is never sent by itself; it is trimmed and
capped at `maxMessageChars`, and it keeps what the person already typed unless `replaceDraft` is set.
`NhAssistantStore.setDraft(text, { replace })` does the same for a panel in a page, and
`pendingDraft()` (`NhAssistantComposerDraft`) feeds the composer's `restore` input, which now also takes
a prepared draft. `restoredDraft()` keeps naming only a message the server refused.

## @newheap/platform-ai-chat: the unsent message survives a closed drawer

`NhAssistantStore` keeps what the composer holds and has not sent per conversation, in the tab's memory
only: `unsentMessage()` (`NhAssistantUnsentMessage` with `text` and `prepared`) for the open
conversation or the new chat, and `keepUnsentMessage(message)`. The composer takes it through its new
`unsent` input and reports every change through `unsentChange`; `nh-assistant-panel` binds both, so a
closed drawer, another conversation or a page left behind keep the text, and the drawer and
`mode="page"` show the same one. Each conversation now shows its own unsent text instead of the text
the composer held before. Sending forgets it, a refused message comes back as the person's own text of
its conversation, a new chat's text moves into the conversation created for it, and choosing another
agent in the composer takes it along; a deleted or left conversation, signing out and another person
signing in forget it. `setDraft` keeps the person's own unsent text and returns `false` for it, and
`open({ agentId, draft })` leaves the open conversation its own text.

## NewHeap.Platform.AspNet.Common: division lockouts in whole UTC days

`NhUserManager.GetValidClaimsByUserIdAsync` decides whether a division membership is locked out by
comparing its `LockOutStartDateTime` and `LockOutEndDateTime` with the start of today and of
tomorrow in UTC, instead of reading their `.Value.Date`. A membership stays locked out from its start
day through its end day, or indefinitely without an end date. The predicate no longer reads the value
of an empty date, so a user without an active division or memberships gets the same claims on
EF Core InMemory as on SQL Server and PostgreSQL. The day follows the application's clock: a
registered `TimeProvider`, otherwise the system clock.

| Breaking change | Required action |
|---|---|
| A lockout date stored with a non-zero offset counts in the UTC day of its instant at the day edges; SQL Server's `CONVERT(date, …)` used the day of the stored offset before, PostgreSQL already stored UTC. | None, unless an application stores lockout dates with a local offset and expects that offset's day; store them in UTC for the day they mean. |
| A registered `TimeProvider` decides the current day of division lockouts. | None, unless a host registers a clock that differs from real time outside tests. |

## NewHeap.Platform.Common and NewHeap.Platform.AspNet.Common: an explicit SMTP security mode

`MailServiceSettings.SecurityMode` names how `NhMailService` secures its SMTP connection
(`NhSmtpSecurityMode`): `StartTlsRequired` upgrades with STARTTLS and fails against a server that does
not offer it, `ImplicitTls` speaks TLS from the first byte (SMTPS), and `None` sends in plain text.
Settings without a mode keep the meaning of `EnableSsl`: `true` requires STARTTLS and `false` sends in
plain text, so existing settings connect as before. The service now sends with MailKit (MIT) and
converts the `MailMessage` with `MimeMessage.CreateFromMailMessage`, so `SendAsync(MailMessage, ...)`
and its callers stay as they are. It validates the server's certificate with no switch to skip it,
authenticates only when `User` is set, writes no protocol log, never retries in plain text after a
TLS failure and honours cancellation. `WithMail` and `AddNewHeapNotifications<TContext>` validate the
settings when the host starts.

Common now depends on MailKit 4.18.2 and MimeKit, and the release unit's packages require
`System.Security.Cryptography.Pkcs` 10.0.13 or later instead of 9.0.21: MimeKit needs the 10.0 line,
and 10.0.13 is its fix for CVE-2026-84008 (GHSA-846f-f764-76mx).

| Breaking change | Required action |
|---|---|
| `NhMailService.SendAsync` throws MailKit's exceptions instead of `System.Net.Mail.SmtpException`, such as `SslHandshakeException`, `NotSupportedException` for a server without STARTTLS, `AuthenticationException`, `SmtpCommandException` and `SmtpProtocolException`. | Catch those exceptions, or `Exception`, where code caught `SmtpException`. |
| A TLS connection now also checks the revocation of the server's certificate; a host that cannot reach the certificate authority's revocation service fails the delivery. | Allow the host outbound access to the authority's OCSP and CRL endpoints; trust a private authority in the host's certificate store. |
| Host start reads `MailServiceSettings` and fails when `SecurityMode` is not an `NhSmtpSecurityMode` or a value cannot be bound, instead of failing the first delivery. | Use `StartTlsRequired`, `ImplicitTls` or `None`, or leave `SecurityMode` out to keep `EnableSsl`, and fix values that do not bind. |
| `NhEmailNotificationDispatcher` rethrows the `OperationCanceledException` of a dispatch whose token was cancelled instead of returning a failed `TaskResult` with `DispatchError`. | Let the cancellation travel; catch `OperationCanceledException` where code calls `DispatchAsync` with a token it cancels. |

## NewHeap.Platform.Media.FileStructureStorage.SqlServer: cryptography dependency on the 10.0 line

The SQL Server media provider requires `System.Security.Cryptography.Pkcs` 10.0.13 or later instead
of 9.0.21, in line with the mail dependencies of `NewHeap.Platform.Common`; 10.0.13 is the 10.0
line's fix for CVE-2026-84008 (GHSA-846f-f764-76mx). No API or schema changes are required.

## NewHeap.Platform.AI.Chat: presentation limits that a presenter's unit test checks

`NhAssistantPresentationLimits.FindBrokenRule(presentation)` returns the first limit an
`NhAssistantApprovalPresentation` breaks, as an `NhAssistantPresentationRules` name, or `null`. It is the
check the assistant drops a presenter's result by, so a presenter's unit test can present with the longest
texts the application stores, in every supported culture, and fail before production logs the warning and
shows approvers the generic summary (SPM-347). Nothing else changes.

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: the culture a person decided in

`NhAssistantProposalHandOff.DecisionCulture` and `NhAssistantExecutionReportReceipt.DecisionCulture` name
the UI culture in which the person approved a proposal, so the application that executes it can address
them in their language. Deferred decisions now record it as inline approvals did: the decision endpoints
record the request culture, and `INhAssistantProposalDecisionService.DecideAsync` records the new optional
`NhAssistantDecidingPerson.Culture`, the language of the message the channel asked the person in. Only a
supported UI culture of `RequestLocalizationOptions.SupportedUICultures` is recorded, by its exact name or
as the supported culture of its language (`nl-BE` as `nl-NL`); a culture of another language records none
and the decision stands. An inline approval records the turn's culture the same way, now also in an agent
thread. The assistant's own posts into an agent thread keep the default culture, and a chat still reads
the close of its hand-off in the recorded culture. The `ApprovalCulture` migrations hold the column; no
migration is added (SPM-348).

| Breaking change | Required action |
|---|---|
| A record comparison with `new NhAssistantExecutionReportReceipt(...)` or `new NhAssistantProposalHandOff(...)` now also compares `DecisionCulture`, which decisions through the endpoints, inline approvals and a deciding person's `Culture` fill. | Set `DecisionCulture` in the expected record, or compare the members you mean, such as `ApprovalId`, `Status` and `Duplicate`. |

## NewHeap.Platform.AI.Common and NewHeap.Platform.AI.Chat: a proposal guard that knows the call

`NhAiProposalGuardContext` has init-only `Invocation`, `ConversationId`, `TurnId` and `Channel`. The
assistant sets them when a call proposes, for the `NhAiProposalGuardStages.Proposal` check and the keys of
the new proposal, so a guard can refuse before an approval card is shown, for example a change that a
channel's chat may not propose (SPM-349). They are `null` at revalidation, decision and execution, and
for a proposal an asker leaves for the subject's approvers; they take no part in the proposal hash or the
approval evidence.

| Breaking change | Required action |
|---|---|
| `NhAiProposalGuardContext` record equality now also compares `Invocation`, `ConversationId`, `TurnId` and `Channel`, which the proposal stage fills. | Compare the members you need, such as `ProposalId`, instead of whole contexts in a guard's cache key or a test; decide on the new members only in a guard registered for `NhAiProposalGuardStages.Proposal`, never at the later stages where they are `null`. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: candidates of a new text asset version

`INhAssistantInstructionAssetUsage.ProposeCandidatesAsync(assetId, assetVersion, actorId, reason)`
proposes a version of one of the application's text assets to every live data skill that names an
older version: each skill gets a candidate in shadow through its own save (apply `shadow` on its live
version), with the live version's other fields and the new version's hash, which an administrator
compares and promotes or discards. A skill whose instructions an agent package locks is reported
`locked` (`assistant-package-locked` with `instructions: locked`) and keeps its version; the outcome
is reported per skill (`NhAssistantInstructionAssetCandidatesDto`, `proposed`, `already-proposed`,
`locked`, `refused`). Agents created in the administration that name an older version are listed only.
Nothing takes effect by itself.

| Breaking change | Required action |
|---|---|
| `INhAssistantInstructionAssetUsage` has the new member `ProposeCandidatesAsync`. | A class that implements or mocks the interface implements the member, or delegates to the registered service. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: uncarried parts of a promoted package version

`NhAssistantShadowVariantPromotionRequest.UncarriedParts` (`NhAssistantUncarriedParts`) decides what a
promotion with `AsPackageVersion` does with the live agent's linked parts that the variant does not
carry. `Unlink`, the default, keeps today's behavior: they stay on the live agent as local parts.
`Relink` keeps them linked, to the variant's package version, with the locked fields, locked values and
hash of their link, and reports them with the new part outcome `kept-linked`
(`NhAssistantPackagePartOutcomes.KeptLinked`). `POST admin/agents/{id}/promote` takes it as
`uncarriedParts` (`unlink`, `relink`); another value answers `400 uncarriedParts: invalid`, `relink`
without `asPackageVersion` `400 uncarriedParts: not-allowed`. No migration.

| Breaking change | Required action |
|---|---|
| A promotion's `Package.Parts` may report the new outcome `kept-linked`, though only when the caller sends `UncarriedParts = Relink`. | Code that maps every value of `NhAssistantPackagePartOutcomes` handles `kept-linked`, or keeps the default `Unlink`. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: read package parts over HTTP

`POST admin/agent-packages/read` (body `{ "agentIds": [...] }`, answering
`NhAssistantAgentPackageReadDto`) and `GET admin/agent-packages/{packageId}` (answering
`NhAssistantInstalledAgentPackageDto`) answer `INhAssistantAgentPackages.ReadAsync` and
`GetPackageAsync` for administrators, behind the administration policy and without an audit, so an
administration compares a shipped package with the host without an export. A package the host never
imported answers the new code `404 assistant-package-not-found`. The routes are
`NhAssistantAgentPackageRoutes.Read` and `NhAssistantAgentPackageRoutes.Package`.

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: the consultation chain across applications

Cooperating applications that consult each other's agents over MCP keep one consultation chain, so a
loop between them is refused as one host refuses it (SPM-366). `UseMcpChainApplication(applicationId)`
names the application in chains. `NhAssistantMcpContextBinding.IncludeChain` (off by default) adds
`chain` to `com.newheap/assistant-context`: the agents that asked, outermost first and each with its
`application`, and the calling turn's `consultationDepth`; the version stays 1 and the other members
keep their shape. With the chain a binding also binds a call from a consultation turn, without a
snapshot; a code-owned import names the binder with the new constant `NhAssistantMcpContext.BinderId`.
`ContinueMcpChain(new NhAssistantMcpChainCaller(applicationId, issuer))` reviews a caller by the issuer
claim (and optionally the client claim) of its principal: a consultation it starts over MCP is one
level deeper than the deeper of the caller's depth and the local one, counts the caller's agents as
askers and is refused with `assistant-consult-depth-exceeded` before it is recorded or the agent runs
when an agent of this application is already in the chain; a request from inside a consultation is
refused the same way. The chain of any other caller, and a malformed one, is ignored, never refused.
Two applications with an agent of the same id never refuse each other. The agent's budget,
`MaxConsultationsPerActorPerDay` and the call's deadline still bound every call. Startup fails for a
binding with the chain or a reviewed caller without the application's name, and for a reviewed caller
with that name. No migration.

## @newheap/platform-ai-chat: read package parts and keep uncarried parts linked

`NhAssistantAdminApiService.readAgentPackageParts(agentIds)` (`AgentPackageParts`) and
`getInstalledAgentPackage(packageId)` (`InstalledAgentPackage` with `PackageAgentLinks`) read an
agent's parts and an installed package without an export; the code `assistant-package-not-found` is
translated in English and Dutch. `ShadowVariantPromotionRequest.uncarriedParts`
(`ShadowVariantUncarriedParts`: `unlink` or `relink`) keeps the live agent's uncarried linked parts linked
to the package version, and `AgentPackagePartOutcome` has the outcome `kept-linked`. The mock API answers
both reads and the relink as the server does.

| Breaking change | Required action |
|---|---|
| `AgentPackagePartOutcome` has the new member `'kept-linked'`. | A `switch` or `Record<AgentPackagePartOutcome, ...>` over the outcomes handles `'kept-linked'`. |

## NewHeap.Platform.AI.AspNet.Common and NewHeap.Platform.AI.Test: signing MCP clients in through the host's authorization server

`WithNewHeapPlatformAITools(exposure => exposure.UseProtectedResource(...))` makes the MCP exposure an
OAuth protected resource of the host's authorization servers, so any MCP client that follows the MCP
authorization flow finds the authorization server from the endpoint and signs in.
`NhAiMcpProtectedResourceOptions` takes the public URL of the MCP endpoint (`Resource`), the issuers
(`AuthorizationServers`) and the scopes (`ScopesSupported`); map the endpoint with
`RequireAuthorization(NhAiMcpProtectedResourceDefaults.AuthorizationPolicy)`. The exposure then serves
the protected resource metadata (RFC 9728) at the resource's well-known address and at the root,
answers an MCP request without a valid token with `401` and
`WWW-Authenticate: Bearer resource_metadata="...", scope="..."` (with `error="invalid_token"` when a
token was sent), and accepts only access tokens whose audience is the resource (RFC 8707), validated
with the keys of the issuers' metadata. Behind a reverse proxy the resource is the public URL; the
metadata follows its path and the challenge names its public address. `AcceptHostSchemes` keeps the
host's own sign-in at the same endpoint, `OnTokenValidated` maps the token's person to the
application's account or refuses it, and `INhAiCallerCredentialAccessor` returns no token for such a
request, so the token is never passed on. The authorization server, the registration of clients and
the consent stay the host's. Without the option nothing changes.
`NhAiTestAuthorizationServer.IssueAccessToken(subject, resource, scope, issuedAt, lifetime)` issues a
token as of a given time, for example an expired one.

## NewHeap.Platform.AI.Common, NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: compositions and their step runtime

`NhAssistantBuilder.AddComposition<TInput>(id, composition => ...)` declares a known chain of registered
governed tools as one tool (TC2): `Step(name, toolId, arguments)` calls a tool by id, a later step reads
earlier results through `NhAssistantCompositionContext<TInput>.Result`, and `Returns` declares what reaches
the model. The composition's descriptor is marked `NhAiCompositionFeature.Compositions`; its effect is the
strongest of its steps and its classification the highest, so one that only reads runs at once and, with
`ExposeOverMcp`, is read-only over an MCP exposure that allows compositions (TC7). A turn offers it where
code allows the agent compositions and the application's switch has them on, and only with every tool
its steps call. Every step runs through the shared invoker with the caller's context and gets the
decision the same call gets on its own (TC5): a `Deferred` proposal lets the chain continue, an `Inline`
approval stops it and the turn waits, after which the model reads its own call, where the chain
stopped, and the decided step; a shadow run proposes every change; a failing or refused step stops the
chain and returns what succeeded with that step's code. Compositions never call another composition,
the script tool, a consultation or a destructive tool.

The step runtime is public: `INhAssistantToolSteps.BeginAsync(NhAssistantToolStepRequest)` opens an
`NhAssistantToolStepSession` from inside a composition or script call, with `MaxSteps`,
`MaxMutationSteps`, `Hash` and a `Tools` filter; `RunAsync` runs one step and returns an
`NhAssistantToolStep` with its `NhAssistantToolStepStatus`, and `CompleteAsync` audits the call and returns
its `NhAssistantToolStepActivity`. Refusals use `NhAssistantToolStepCodes`. In a turn each step is a tool
invocation of the turn whose call id names the call that ran it, without a message part of its own; the
first step uses the call's count of the turn's tool calls and every further step counts as a call.
`NhAiToolCallOrigin` (`NhAiToolCallOriginKind`, the composition's or script tool's id, the script's hash,
the parent invocation and the step index) is on `NhAiInvocationContext.Origin`, `NhAiAuditRecord.Origin`
and, at the proposal stage, `NhAiProposalGuardContext.Origin`; it takes no part in a proposal hash or
approval evidence.

`GET admin/agents/{id}/compositions/activity` (`NhAssistantCompositionActivityDto`) shows, content-free, each
composition and script call of an agent with the tool calls it ran in order, the model rounds it saved
and the size of the result returned to the model, and the use per tool (TC1). It reads the stored tool
invocations; no table or column is added.

| Breaking change | Required action |
|---|---|
| `NhAiInvocationContext`, `NhAiAuditRecord` and `NhAiProposalGuardContext` record equality now also compares `Origin`, which the step runtime fills for a step of a composition or script. | Compare the members you need instead of whole records in a cache key or a test; read `Origin` to treat a step differently, never to relax a rule. |

## @newheap/platform-ai-chat: the composition activity of an agent

The agent page's Activity in `@newheap/platform-ai-chat/admin` offers Compositions next to Runs and
Feedback (`nh-assistant-agent-composition-activity`): per tool the calls, tool calls, rounds saved and
result sizes, and per call its tool calls in order with how each ended, in English and Dutch.
`NhAssistantAdminApiService.getAgentCompositionActivity(agentId, filter)` reads it
(`AgentCompositionActivity`). The step codes `assistant-step-*` and
`assistant-composition-step-result-unavailable` are translated, and the mock API answers the activity.
An agent that does not run on its own now shows the Activity's choice of Feedback and Compositions too.

| Breaking change | Required action |
|---|---|
| The Activity of an agent that works in the chat only shows its segments (Feedback, Compositions) where it showed none. | A host test that expected no `[data-activity]` button for such an agent expects `feedback` and `compositions`. |

## NewHeap.Platform.AI.Chat.AspNet: discovery reads the challenge of a stateless MCP server

OAuth discovery of an MCP server (`POST admin/mcp-servers/discover`, and saving a server with
`oauth.confirmAuthorization`) now probes the endpoint with an MCP `initialize` `POST` without a
credential, the request an MCP client starts with, instead of a `GET`. A stateless Streamable HTTP
server, which answers `GET` with `405`, now shows the scopes and the metadata address of its challenge
(`challengedScopes`) rather than only the scopes of its metadata. A `GET` follows only when the `POST`
answers `405`, as an HTTP+SSE endpoint does; without a challenge the well-known metadata addresses
decide, as before. The probe reads only the status and the headers, and a session that a server
without authorization opens for it is ended with `DELETE`. No public API changes.

## NewHeap.Platform.AI.Common, NewHeap.Platform.AI.AspNet.Common, NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.AspNet, NewHeap.Platform.AI.Chat.SqlServer and NewHeap.Platform.AI.Chat.PostgreSql: a switch of compositions and scripts the application owns

A governed tool says it is a composition or the script tool with `NhAiToolDescriptor.CompositionFeature`
(`NhAiCompositionFeature.Compositions` or `Scripts`; `None` for every other tool, which nothing here
changes). Such a tool is offered only where code allows its feature and the application's switch has it
on, decided once per feature at the start of each turn, run step and MCP request:

- `INhAssistantCompositionSwitch` answers with `NhAssistantCompositionSwitchContext`: the feature, the
  channel (`NhAssistantCompositionChannel`: `Chat`, `Run`, `Consultation`, `HandOff`, `Mcp`), the tenant,
  the agent and the person. The default reads `NewHeap:AI:Assistant:Compositions` and
  `NewHeap:AI:Assistant:Scripts` (`NhAssistantOptions`), both off; `UseCompositionSwitch<TSwitch>()`
  replaces it with the application's own, for example a setting in its own database.
- The ceiling in code: `ConfigureAgentContext(agentId, c => c.AllowCompositions().AllowScripts())` for an
  agent in code, `UseCompositionsAsData(c => c.AllowCompositions().AllowScripts())` for agents created in
  the administration, and `WithNewHeapPlatformAITools(mcp => mcp.AllowCompositions().AllowScripts())` for
  the MCP exposure.
- Within `UseCompositionsAsData` an agent created in the administration has its own `compositions` and
  `scripts`, on the agent input and DTO, `INhAssistantAgentConfiguration.CreateAgentAsync`,
  `INhAssistantAgentDefinitions.UpdateAsync` and agent packages (written only when on, lockable as
  `compositions` and `scripts`), and taken over by a promoted shadow variant. Outside the ceiling a save
  answers `compositions: not-allowed` or `scripts: not-allowed`. The run catalog reports the ceiling as
  `compositionCeiling`. The migration `AgentCompositions` adds `UsesCompositions` and `UsesScripts` to
  `AssistantAgent`; hosts with `RunMigrations` apply it at start, others with their deployment step.
- While a feature is off its tools are not offered or listed over MCP, and a call that still arrives is
  refused with `assistant-composition-off` or `assistant-scripting-off` (`NhAiCompositionFeatures`,
  `NhAssistantErrorCodes.CompositionOff`, `ScriptingOff`); the MCP exposure leaves out a feature it does
  not allow (`ai-tool-not-authorized`).
- `INhAssistantCompositionStatus` and `GET admin/agents/{id}/compositions` (administration policy) return
  per feature and channel what the assistant does for an agent and why (`NhAssistantCompositionReasons`:
  `on`, `not-installed`, `agent-not-allowed`, `not-allowed-for-data-agents`, `off-for-agent`,
  `off-by-application`).

Without these calls nothing changes: no composition or script tool exists yet, and both features are off.

## @newheap/platform-ai-chat: what the assistant does with compositions and scripts

`NhAssistantAdminApiService.getAgentCompositionStatus(agentId)` reads `GET admin/agents/{id}/compositions`
(`AgentCompositionStatus`), so an application's own settings screen shows what the assistant does next to
its checkbox. The overview of an agent's page in the administration shows it per feature and channel with
its reason, and an agent created in the administration switches compositions and scripts on in its tools
within the run catalog's `compositionCeiling` (`AdminAgentInput.compositions`, `scripts`). The mock API
answers the state from `admin.compositions` and `admin.compositionsAsData` of its scenario. English and
Dutch texts include `assistant-composition-off` and `assistant-scripting-off`.

## NewHeap.Platform.AI.Scripting (new package): the script tool

The new package `NewHeap.Platform.AI.Scripting` adds the script tool `run_script` (SPM-373, SPM-374):
the model writes a short JavaScript program in which each tool it is offered in that turn is an async
function of `tools`, the program runs on the server and only its return value goes back to the model.
`services.AddNewHeapPlatformAIScripting(scripting => ...)` registers it; it is offered only where code
allows scripts (`ConfigureAgentContext(agentId, context => context.AllowScripts())`,
`WithNewHeapPlatformAITools(mcp => mcp.AllowScripts())`) and the application's composition switch has
them on. Every call runs as a step of the call's step session (`INhAssistantToolSteps`) and gets exactly
the decision the same call gets on its own: a deferred proposal returns the "proposal recorded" result
and the script goes on, an inline approval stops the script with `ai-script-waiting-for-approval` and
the turn waits. Each script gets a fresh Jint engine in strict mode without .NET access, modules,
`require`, `eval` or timers, within `NhAiScriptLimits`: statements, time, memory, recursion, tool calls
(at most 64), `MaxMutationsPerScript` (0, the default, makes scripts read-only) and result size, per
agent (`ConfigureAgent`), for MCP (`ConfigureMcp`) or by default (`ConfigureDefaults`); a destructive tool
runs in a script only after `AllowDestructive(toolId)`. The description lists the tools as
TypeScript-style signatures generated from their JSON schemas, and a call that does not match stops with
`ai-script-call-invalid`, the exact signature and the field error. The result lists every call in order
with its outcome; the stop codes are in `NhAiScriptCodes`. It depends on Jint 4.17.0 (BSD-2-Clause) and
Acornima 1.7.0 (BSD-3-Clause), which no other package references. The script is the call's argument,
which the activity already stores: no migration.

## NewHeap.Platform.AI.Mcp and NewHeap.Platform.AI.Chat: what the script tool builds on

`INhAiMcpToolContributor` (`AddNewHeapPlatformAIMcpToolContributor<T>`) adds tools to the MCP export of a
request over exactly the governed tools that caller sees; the script tool uses it to list the caller's
own tools. `NhAssistantToolStepTurn.Current(caller)` names the agent and the tools a step session would
offer in the current turn, before the call. Without the package nothing changes.

## NewHeap.Platform.AI.Chat: the tools a rule turn proposes with

A rule turn selects its own tools: the agent's tools the run may discover, with the tools that change
state from `Propose` autonomy on, because every effect of a rule turn is a proposal, and the
compositions and script tool the turn offers. It no longer takes the model's allow-list, which offers
those tools only with `Execute` and falls back to the agent's selectors when it selects nothing. The
rules of a `Propose` or `Simulate` agent now propose with each of its tools that change state, also
when it has read tools, compositions or the script tool; before, they could only through that fallback,
which a withheld composition or script tool switches off. The model's tools do not change.

| Breaking change | Required action |
|---|---|
| The rules of an `Observe` or `Explain` agent are refused (`assistant-proposal-refused`), also when its selectors name only tools that change state, which they proposed with before. | Give an agent whose rules propose `Propose` autonomy, which keeps its model from changing anything in its own turns and runs. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: an approval policy chosen per proposal and who may approve it

`INhAssistantApprovalPolicySelector`, registered with `NhAssistantBuilder.UseApprovalPolicySelector<TSelector>()`
(or with a time limit of at most 30 seconds; two by default), chooses the approval policy of each proposal after
code resolved it: it receives `NhAssistantApprovalPolicySelection` with the agent, tool, subject, arguments,
`ResolvedPolicyId`, `Source` (`NhAssistantApprovalPolicySource`), skill, trigger, initiator, tenant, run and the
`Origin` of a composition's or script's step, and returns a registered policy id or `null` for the resolved one.
It is asked for runs and rule runs, a person's messages in an agent's thread, consultations (also when the asker
leaves a proposal for the subject's approvers) and the steps of compositions and scripts in those turns. The
proposal keeps the chosen policy in its policy copy, so the decision checks it. An unknown id refuses the proposal
with `assistant-approval-policy-unknown` (409), a selector that throws with
`assistant-approval-policy-selector-failed` and one past its time limit with
`assistant-approval-policy-selector-timed-out` (both 503); nothing is recorded (SPM-356).
`INhAssistantApproverDirectory` lists (`ListAsync`), counts (`CountAsync`) and, for a proposal under its policy copy
(`ListForProposalAsync`), lists the people who may approve: the subject's members and the initiator, checked as a
decision checks them, in `NhAssistantApproverList(Approvers, Complete)` with `NhAssistantApprover` and
`NhAssistantApproverCount`. It is a server API without an endpoint (SPM-357). Without a selector nothing changes.

## @newheap/platform-ai-chat: texts of the approval policy selector's codes

`NH_ASSISTANT_SERVER_ERROR_CODES` names `assistant-approval-policy-unknown`,
`assistant-approval-policy-selector-failed` and `assistant-approval-policy-selector-timed-out`, with English and
Dutch texts for a tool call's result and for an error, and the mock API answers them with the server's statuses
(409 and 503).

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: lists and uploads for an application's channel

`INhAssistantConversations` lists a person's chats (`ListAsync` with `NhAssistantConversationPage`), lists
the agents the person may chat with and what they may attach to each (`ListAgentsAsync`) and uploads a file
to a chat (`UploadAsync` with `NhAssistantConversationUpload`), for the application's own channels, with
the checks and codes of `GET conversations`, `GET agents` and `POST conversations/{id}/files`: the
endpoints and the service share one implementation of the participant role, the agent's policy, the
agent's rules, the media type against the content, the size, the scanner and the file source. The upload
takes a readable, seekable stream, reads it again for each step from where the caller left it, leaves it
open for the caller and never logs the file's name; its `NhAiFileReference` goes into
`NhAssistantConversationMessage.Files` of the person's next message (SPM-358).

| Breaking change | Required action |
|---|---|
| `INhAssistantConversations` has the new members `ListAsync`, `ListAgentsAsync` and `UploadAsync`. | A class that implements or mocks the interface implements the members, or delegates to the registered service. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: uploads go with their conversation

`UseFileRetention<T>()` registers the application's `INhAssistantFileRetention`. The assistant calls
`ForgetConversationAsync` with `NhAssistantForgottenConversation` after the owner's archive of a chat
committed and after `INhAiSubjectRetention.ForgetSubjectAsync` deleted each agent's thread
(`NhAssistantFileRetentionReasons.Archived`, `SubjectForgotten`). The call is best effort: a failed result
or an exception is logged as a warning with the conversation and its code or exception type, never undoes
the archive or the deletion and is not retried; make it idempotent. Without it nothing changes.
`GET agents` and `GET status` now offer uploads only for an agent whose model profile, or a fallback,
permits in the execution region the classification a message with an upload carries (the higher of the
agent's own and `NhAssistantUploadOptions.DefaultClassification`), and `POST conversations/{id}/files`
refuses the others with `409 assistant-files-not-accepted`; before, the upload was offered and accepted and
the turn refused the file with `assistant-classification-not-permitted` (SPM-359). No migration.

| Breaking change | Required action |
|---|---|
| An agent whose profile chain does not permit the uploads' classification (`Confidential` by default) now has `uploads: null` in `GET agents` and `GET status`, the chat panel offers it no attach control and an upload to its conversations answers `409 assistant-files-not-accepted`. | Give the agent's profile a fallback that permits the classification (`WithFallbackProfiles`), or lower `DefaultClassification` in `ConfigureUploads` to what the profile permits; check that the profile also permits the execution region. |

## NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.AspNet, NewHeap.Platform.AI.Chat.SqlServer and NewHeap.Platform.AI.Chat.PostgreSql: requests that answer where they were asked, and requests only

`NhAssistantConsultationBuilder.AnswerRequests(NhAssistantRequestAnswerRoute.AskingConversation)` (opt-in;
`AgentThread` stays the default) lets a request through another assistant answer in the chat it was made
in. The run keeps the asking chat and turn (`AssistantRun.AskingConversationId` and `AskingTurnId`), and the
completed run posts its answer once into that chat as an application message readable by the person who
asked, untrusted, titled in their culture, with the subject type's `OpenAt` link and one idempotency key per
run; the `RequestAnswered` notification stays as it was. A request of an MCP client, of an agent run or of a
consultation's turn has no asking conversation, and a shadow run, a run that read restricted results, a
consultation classified `Restricted` and a chat the person may no longer open post nothing.
`NhAssistantConsultationCeilingBuilder.AnswerRequests` does the same for consultations defined as data,
reported as `answerRequestsInAskingConversation` of the ceiling, code and data consultation DTOs.

`NhAssistantConsultationBuilder.RequestsOnly()` generates only `assistant.request.{agentId}.{subjectType}`,
with the consultation's `Description` for the asking model and no consultation proposal routes; startup
refuses it together with `MinimumRole`, `Proposals`, `Limits`, `Tools`, `History`, `Instructions` or
`AllowRequests(false)`, and refuses `AnswerRequests` without requests. As data,
`NhAssistantDataConsultationRequest.RequestsOnly` (`requestsOnly`) needs `allowRequests` and leaves out the
settings of a consultation's own turn (field errors `allowRequests: required` and `{field}: not-allowed`);
`NhAssistantCodeConsultationDto` and `NhAssistantDataConsultationDto` report `requestsOnly` with an empty
`consultToolId`, and `NhAssistantPackageConsultation.RequestsOnly` carries it in agent packages, written only
when it is set and lockable as `requestsOnly`. A host before this version refuses a package with it.

The migration `RequestAnswersAndRequestsOnly` adds `AskingConversationId` and `AskingTurnId` to
`AssistantRun` and `RequestsOnly` to `AssistantAgentConsultationDefinition` on SQL Server and PostgreSQL;
hosts must apply the migration: hosts with `RunMigrations` apply it at start, others with their deployment
step. Without the new calls nothing else changes.

## @newheap/platform-ai-chat: requests only and the answer route in the administration models

`CodeConsultation` and `DataConsultation` gain `requestsOnly` and `answerRequestsInAskingConversation`,
`DataConsultationRequest` gains `requestsOnly`, `ConsultationCeiling` gains
`answerRequestsInAskingConversation`, and `PackageConsultation` gains `requestsOnly`; each is optional, so
older servers keep working. `NH_ASSISTANT_PACKAGE_LOCK_FIELDS.consultation` names `requestsOnly`. The mock
API saves and checks requests-only consultations as the server does, lists only their request tool and
answers the ceiling's `answerRequestsInAskingConversation` from `admin.consultationsAsData`.

## NewHeap.Platform.DatabaseRead.Tool: MySQL and MariaDB

A profile's `provider` may be `mysql` or `mariadb` (MySqlConnector). The tool checks that the server is
that flavor, MySQL 8.0 or later or MariaDB 10.11 or later, and otherwise stops with
`provider-flavor-mismatch` or `provider-version-not-supported` (exit code 3). The principal is verified deny
by default from `SHOW GRANTS` with every role it can use active, mandatory roles on MySQL and the grants to
`PUBLIC` on MariaDB included: only `USAGE` on `*.*` and SELECT on schemas, tables and columns outside `mysql`,
`sys` and `performance_schema` pass, and `verificationCheck` names the first of `global-privilege`,
`dynamic-privilege`, `proxy-privilege`, `grant-option`, `role-admin-option`, `system-schema-select`,
`schema-privilege-not-allowlisted`, `table-privilege-not-allowlisted`, `column-privilege-not-allowlisted`,
`routine-privilege`, `definer-view` and `grant-not-recognized`. The transaction starts with
`START TRANSACTION READ ONLY` and the flavor's statement limit (`max_execution_time` or
`max_statement_time`) and lock limits; the query policy also refuses `#` comments, backslashes in quoted
values (`backslash-escape-not-allowed`), `FOR SHARE` (`locking-read-not-allowed`) and the functions that
sleep, lock, read files or move sequences. Schema evidence comes from `information_schema`; relationships
report `isValidated` false. Existing SQL Server and PostgreSQL profiles are unchanged (SPM-367). The NewHeap
EF libraries still support SQL Server and PostgreSQL only.

## NewHeap.Platform.DatabaseRead.Tool: inline table-valued functions on SQL Server

A `sql-server` profile may set `"sqlServerFunctionPolicy": "inline-table-valued-select"`; the default `strict`
is unchanged and another provider rejects the property with `invalid-profile-value`. The principal may then
hold SELECT on an inline table-valued function that is schema-bound, has no `EXECUTE AS` and depends only on
tables and views of the same database that it may select directly; grant it `VIEW DEFINITION` on the function
too, so the tool can read the function's dependencies. Other functions keep `function-select`, and an inline
function that does not qualify is rejected with `inline-function-not-schema-bound`,
`inline-function-execute-as`, `inline-function-definition-not-visible`,
`inline-function-dependency-not-allowed` or `inline-function-dependency-not-selectable` (SPM-368). PostgreSQL
keeps its strict function policy.

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: agent work off while the administration serves

`NhAssistantOptions.AgentWork` (`NewHeap:AI:Assistant:AgentWork`, true by default, counting only while `Enabled` is
on) switches agent work off on a host while the endpoints, the administration and people's chats keep serving. It is
read for every piece of work, so a reloaded configuration applies to the next one. While it is off, events,
schedules, webhooks, starts by hand, requests, replays, hand-offs and follow-ups start no run and are refused with
`assistant-agent-work-off` (409) before any of their lambdas runs; a queued run ends with that code without claiming
its inputs; consultations and `INhAssistantConversationMessages.PostAsync` (and so a request's answer in the asking
chat) are refused with it; approved proposals of agent threads are held as for a stopped agent and queued by the
maintenance job once agent work is on; resuming a paused agent or skill is refused with it. People keep chatting,
deciding and being reminded, open proposals are still revalidated, the report of an execution the application
made is still posted, and `INhAssistantProposals.ProposeForAsync` still records a proposal for a person, whose
execution once approved waits likewise. `GET status` reports `agentWork`, and `NhAssistantAgentState.AgentWork` and
`NhAssistantAdminAgentDto.AgentWork` say it for every agent, whose `Active` is false meanwhile (SPM-365). With the
default nothing changes; a host whose assistant is disabled no longer queues an orphaned run again only to cancel it,
and keeps it queued until it is enabled.

## @newheap/platform-ai-chat: agent work off in the agent list

`AssistantStatus`, `AdminAgent` and `AgentState` gain the optional `agentWork`. While it is `false`, the agent list of
`NhAssistantAdminAgentsComponent` shows a banner above the agents, in English and Dutch.
`NH_ASSISTANT_SERVER_ERROR_CODES` names `assistant-agent-work-off` with English and Dutch texts for an error and for
a tool call's result. The mock API takes `agentWork` in its scenario and `NhAssistantMockBackend.setAgentWork`,
reports it on `GET status` and in the administration, and refuses to resume a paused agent or skill with
`409 assistant-agent-work-off`, as the server does.

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: a deferred proposal for a person without a run

`INhAssistantProposals.ProposeForAsync(NhAssistantProposalRequest)` records an agent's deferred proposal for a
person, for the application's own code, without starting a run: the person as for the decision service
(`NhAssistantDecidingPerson`), the agent, the tool, its arguments, the subject, an optional registered `PolicyId`,
`Presentations` per culture that the application prepared and an `IdempotencyKey`. It takes the governed path of a
run's proposal: the start rule for the person on the subject, the tool's contract, the person's own rights (never
the agent's grants), the named or resolved policy and the application's selector with the new
`NhAssistantApprovalPolicySource.Application`, the proposal guards and keys, an open duplicate and the rejection
cooldown. It is always deferred, waits in the agent's thread at the subject with the person as initiator, so four
eyes keep that person out, and executes with the person's rights only. `NhAssistantProposalResult` reports
`Created`, `AlreadyOpen`, `RecentlyRejected` or `NotNeeded`, and `Repeated` for a key that recorded it before.
A prepared presentation over `NhAssistantPresentationLimits` is refused with `assistant-proposal-presentation-invalid`
(400; field errors with the new rules `culture-invalid`, `too-many-cultures` and `not-presented`), never dropped,
and a key that recorded another proposal with `assistant-proposal-idempotency-conflict` (409). The content-free
audit records the new kind `AgentProposalRecordedForPerson` next to `ApprovalRequested` (SPM-364). No migration;
without the new call nothing changes.

## NewHeap.Platform.AI.Common, NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.AspNet, NewHeap.Platform.AI.Chat.SqlServer and NewHeap.Platform.AI.Chat.PostgreSql: a subject for a person's chat

A person's chat may be about one subject (FB25): `subjectType` and `subjectId` of `POST conversations`, or
`NhAssistantNewConversation.Subject` of `INhAssistantConversations.CreateAsync`. Creating it checks the
subject with its access resolver (at least `Reader`), the person's tenant and what the agent works on
(`assistant-subject-invalid`, `assistant-subject-type-unknown`, `assistant-subject-not-found`,
`assistant-subject-access-denied`, `assistant-agent-does-not-work-on`), and every turn, read, invitation,
live update, notification and application message checks the person's role again, the owner's too, so
revoked access takes effect at once; deleting, leaving and stopping a turn stay possible. The chat keeps
the subject (`subject` of `NhAssistantConversationDto` and `NhAssistantConversationSummaryDto`,
`NhAssistantConversationSubjectDto`) and offers it to tools, turn context providers and retrieval
(`NhAiInvocationContext.ConversationSubject`), proposal guards at the proposal stage
(`NhAiProposalGuardContext.ConversationSubject`), the file source (`NhAssistantFileActor.ConversationSubject`)
and the briefing (`NhAssistantBriefingPurposes.Chat`, after a section that names the subject); it never
becomes the agent's thread about the subject. `RequireChatSubject()` offers an agent only for chats about
a subject: `GET agents` and the status leave it out, `GET agents?subjectType=&subjectId=` and
`INhAssistantConversations.ListAgentsAsync(person, subject)` list the agents that work on a subject, and a
chat with it without a subject answers `400 assistant-subject-required`; startup refuses it on a chat-only
or hidden agent. `INhAiSubjectRetention.ForgetSubjectAsync` also deletes the chats about the subject in its
scope and lets the file retention forget their uploads (SPM-363).

The migration `ChatSubject` adds `SubjectType`, `SubjectId`, `SubjectDisplayName` and `SubjectScopeKey`
with an index on `SubjectType` and `SubjectId` to `AssistantConversation` on SQL Server and PostgreSQL;
hosts must apply the migration: hosts with `RunMigrations` apply it at start, others with their deployment
step. Chats without a subject work as before.

| Breaking change | Required action |
|---|---|
| `INhAssistantConversations` has the new member `ListAgentsAsync(NhAssistantDecidingPerson, NhAiSubjectRef, NhAssistantConversationOptions?, CancellationToken)`. | A class that implements or mocks the interface implements the member, or delegates to the registered service. |

## @newheap/platform-ai-chat: chats about a subject

`NhAssistantStore.setChatSubject({ subjectType, subjectId, subjectDisplayName })` (`NhAssistantChatSubject`)
makes the next new chat a chat about a subject: it loads the agents of such a chat
(`NhAssistantApiService.agents(subject)`, `GET agents?subjectType=&subjectId=`), chooses one of them and
leaves the open conversation, and the next message creates the chat with `subjectType` and `subjectId`
(`CreateConversationRequest`); it resolves `false` and shows the server's refusal when the person may not
use a chat about the subject, and `null` clears it. `newChatSubject()`, `chatSubject()` and `chatAgents()`
(which the panel's agent picker now binds; it equals the status's agents without a subject) describe it,
`store.agents()` also knows the agents offered only for chats about a subject, and the panel shows what a
chat is about ("About {subject}", English and Dutch). `Conversation.subject` and `ConversationSummary.subject`
(`ConversationSubject`, `ConversationSubjectRef`) are optional, so older servers keep working;
`assistant-subject-required` has English and Dutch texts. The mock API checks and keeps a chat's subject and
checks the subject of `GET agents?subjectType=&subjectId=`.

## NewHeap.Platform.AI.Chat: a shadow variant in code stays out of the chat

A shadow variant in code (`ShadowOf`) is no longer offered in the chat, as a variant created as data was
not: `GET agents`, `GET status` and the agents of a chat about a subject leave it out, and a new chat with
it is refused with `assistant-agent-not-found` (404). Its triggers, shadow threads, evaluation and replays
work as before. A chat with a variant broke what shadow mode guarantees: a person saw what it answered.

| Breaking change | Required action |
|---|---|
| A variant in code (`ShadowOf`) is no longer in the chat's agent list, and `POST conversations` with it answers 404 `assistant-agent-not-found`. | Chat with the live agent; to try a variant's instructions in a chat, register them as an agent of its own without `ShadowOf`. |
