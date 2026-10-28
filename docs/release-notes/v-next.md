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
