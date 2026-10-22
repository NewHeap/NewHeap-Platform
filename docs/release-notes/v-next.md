# v-next

## NewHeap.Platform.AI.Chat.AspNet: assistant endpoints built once at host start

`MapNewHeapAssistant` built every assistant endpoint twice when a host started: the authorization
middleware's policy cache reads the application-wide endpoints while the pipeline is built, and the
routing middleware reads them again through a composite of its own on the first request, while a
route group creates its minimal API endpoints anew on every read. The assistant's route groups now
build their endpoints once and give every reader the same endpoints. The first request no longer
pays for a second build (about 180 ms for the 110 endpoints in a Debug test host).

| Breaking change | Required action |
|---|---|
| Routing, the authorization policy cache, link generation and API descriptions share the assistant endpoints built on the first read; conventions added to the group that `MapNewHeapAssistant` returns after the host started no longer apply. | None. Configure that group before the host starts. |

## NewHeap.Platform.AspNet.Common: authentication endpoints built once at host start

The endpoints that `UseNhAuthentication` maps are now built once, like the assistant's, and
routing, the authorization policy cache, link generation and API descriptions share them. Before,
every host start built them a second time on the first request.

| Breaking change | Required action |
|---|---|
| Conventions added to the authentication endpoints after the host started no longer apply. | None. Add route metadata in `IConfigurableAuthenticationEndpoint.Configure`. |

## NewHeap.Platform.Media.Http: media endpoints built once at host start

`MapNhMediaEndpoints` now builds the media endpoints once and gives routing, the authorization
policy cache, link generation and API descriptions the same endpoints, instead of building them
again on the first request.

| Breaking change | Required action |
|---|---|
| Conventions added to the group that `MapNhMediaEndpoints` returns after the host started no longer apply. | None. Configure the group before the host starts. |

## NewHeap.Platform.AspNet.Proxy: management API endpoints built once at host start

`MapProxyEndpoints` now builds the endpoints of `/newheap-proxy/api` once; the branch's routing and
the application-wide endpoints share them instead of each building their own.

## NewHeap.Platform.AI.Common, NewHeap.Platform.AI.AspNet.Common and NewHeap.Platform.AI.AspNet.Mvc: tool catalogs validated after the endpoints are registered

Fix. The startup validation of the tool catalogs (the platform's catalog checks, the API bridge
validator and the MCP export check) ran as hosted services. A web application in the minimal
hosting model starts its hosted services before it builds its pipeline, so reading the API
bridge catalog built ApiExplorer's descriptions while the application's endpoints were not yet
registered. ApiExplorer keeps those descriptions for the life of the process, so ApiExplorer and
OpenAPI documents missed every minimal API endpoint, and the application-wide endpoint data source
was initialized empty and built all endpoints again for every data source added afterwards. A
host with a server now runs these checks once, after its application pipeline is configured and
before the server starts; a host without a server still runs them while its hosted services
start. An invalid catalog fails the host's start with the same exception as before.

| Breaking change | Required action |
|---|---|
| In a web host the AI tool catalog checks run while the web host service builds the pipeline instead of at their own place among the hosted services. A host that captures startup errors (`CaptureStartupErrors`, as under IIS) serves its startup error page for an invalid catalog instead of stopping. | None. Do not read the API bridge catalog or ApiExplorer from your own hosted services before the pipeline is built. |

## NewHeap.Platform.AI.AspNet.Common and NewHeap.Platform.AI.AspNet.Mvc: delegated reads under UseMvc

Fix. The delegated read restriction refuses a token whose target is unknown when it is
authenticated. `UseNewHeapPlatformAspNetCommon` serves controllers through the old `UseMvc`
routing, where a controller request has no endpoint, so in such a host every gateway read of a
headless agent run was refused and returned `api-bridge-unauthenticated`. When the gateway is
enabled, the API bridge now adds a global MVC authorization filter that runs first and
authenticates the delegated read scheme with the action that MVC executes as the target, in both
routing modes. A delegated read principal still reaches only the `GET` gateway action its token
names, with its invocation header, and a request whose target is not known is still refused. A
scheme that middleware authenticates before MVC, such as the default authentication scheme under
`UseMvc`, stays refused there; such a host routes its controllers with `UseEndpointRoutingOnly()`.

| Breaking change | Required action |
|---|---|
| With the gateway enabled, MVC authenticates the delegated read scheme before its other authorization filters for every `GET` request with the `X-NewHeap-AI-Invocation` header, and answers `401` when a delegated read principal reaches another action than the one its token names. | None. |

## NewHeap.Platform.AspNet.Common: controllers with endpoint routing only

New option, no breaking change. `NewHeapAspNetCommonOptionsBuilder.UseEndpointRoutingOnly()`
(`NewHeapAspNetCommonOptions.UseEndpointRoutingOnly`) routes controllers with endpoint routing only:
MVC keeps `EnableEndpointRouting` on, and `UseNewHeapPlatformAspNetCommon` routes before CORS and
authentication, maps controllers with `MapControllers` and no longer adds `UseMvc`. Controller
requests then carry endpoint metadata, so the authorization middleware, `[EnableCors]` and
`[DisableCors]` and other endpoint-metadata middleware apply to controllers, and the host builds one
route table instead of two. Map conventional routes in the `UseEndpoints` callback with
`MapControllerRoute`; combining the option with `UseMvc(Action<IRouteBuilder>)`, or turning
`EnableEndpointRouting` off in `ConfigureMvc`, fails at startup. The default is unchanged. New
applications opt in; an existing application first checks authorization handlers that read
`AuthorizationFilterContext`, filter-based authorization checks, generated links, Razor Pages and URL
rewriting in `UseMiddlewares` (guidance rule `nh-backend-platform-pipeline`).

## @newheap/platform-ai-chat: error codes and toNhAssistantError exported

Fix. The 0.15.0 notes name `NH_ASSISTANT_SERVER_ERROR_CODES` and `toNhAssistantError` for consumers,
but the main entry point did not export the first and exported the second only as the internal
`ɵtoNhAssistantError`. `@newheap/platform-ai-chat` now exports `NH_ASSISTANT_SERVER_ERROR_CODES`
(the failure codes of the back-end), `NH_ASSISTANT_ERROR_CODES` (those plus the client-side codes, the
keys a host with `translations: 'host'` needs under `nh-assistant.errors`) and `toNhAssistantError`,
which turns any failure into a code and translation key. `ɵtoNhAssistantError` remains as an alias.

| Breaking change | Required action |
|---|---|
| None; the exports are added. | Import `toNhAssistantError` instead of `ɵtoNhAssistantError`. |

## NewHeap.Platform.AI.Chat: the skill on the rule context

A rule handler registered once with `AddRuleHandler<THandler>(name, description)` can be named by
several data skills, or by the live and candidate versions of one, but could not tell which one it ran
for. `NhAssistantRuleContext` now carries `SkillId`, `SkillVersion` (the version of a skill defined as
data, `null` for a skill in code) and `SkillCandidate` (`true` in the run of a candidate version). All
three are `null` for the agent's own rules outside a skill.

| Breaking change | Required action |
|---|---|
| None; the properties are added. | Read the skill from the context instead of deriving it from the trigger or the inputs. |

## NewHeap.Platform.AI.Common, NewHeap.Platform.AI.Mcp and NewHeap.Platform.AI.Chat: whose work a budget reservation is

An application's budget manager could not tell an agent's work from a person's chat or an MCP
client's call. `NhAiBudgetRequest` now carries `AgentId`, `SkillId` and `RunId` where known, and
`Purpose`, one of `NhAiBudgetPurposes`: `chat`, `agent-run`, `consultation`, `proposal-execution`,
`mcp`, `ingestion` and `application`. The chat executor and the tool invoker fill them from the
invocation context (`agent-run` for a call that names an agent, `application` otherwise), ingestion
reserves as `ingestion`, and a call through the MCP export reserves as `mcp`.

The assistant still books the calls of its turns on its own daily ledger and gives the application's
manager only the calls outside turns. `NhAssistantBuilder.UseApplicationBudgetInTurns()` also
reserves every accepted call of a turn with the application's manager, with the turn's agent, skill,
run and purpose: `chat` for a person's turn, `agent-run` for a turn of an agent run,
`consultation` and `proposal-execution`. A refusal there ends the call as
`assistant-budget-exhausted`. Host start fails when a host calls it without a budget manager of its
own registered before `AddNewHeapAssistant`, because the default manager refuses every reservation.

| Breaking change | Required action |
|---|---|
| Budget requests from the chat executor, the tool invoker, ingestion and the MCP export carry the new properties, so two requests that were equal records before may differ now. | None, unless a budget manager compares whole requests; compare the counts and ids it needs. |

## NewHeap.Platform.AI.Common, NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: proposal guards at proposal time

Proposal guards checked a proposal only at revalidation, decision and execution, so rules and the
model proposed work that was already done, and those proposals waited until a later event or
decision closed them. `NhAiProposalGuardStages.Proposal` lets a guard check when a rule
(`NhAssistantRuleContext.ProposeAsync`) or the model proposes, before anything is recorded. Declare
it next to the other stages, for example
`AddProposalGuard<TGuard>(selectors, NhAiProposalGuardStages.All | NhAiProposalGuardStages.Proposal)`.
A stale result (`AlreadyDone`, `NotApplicable` or `Stale`) records no proposal, approval or
`ApprovalRequested` audit event: rules get `NhAssistantProposeOutcome.NotNeeded` with the guard's
reason code as `Code` and its explanation as the new `NhAssistantRuleProposal.Explanation`; the model
gets `assistant-proposal-not-needed` with `reason`, `kind` and `explanation` as its tool result, and
the tool call reads `not-executed`. A guard that throws at proposal time lets the proposal be
recorded, as a failing guard never closes one at the other stages. `GetKeysAsync` runs for every
guard of a tool, whatever its stages.

| Breaking change | Required action |
|---|---|
| `NhAssistantProposeOutcome` gains `NotNeeded`, and `NhAssistantErrorCodes` gains `ProposalNotNeeded` (`assistant-proposal-not-needed`, 400). | Handle `NotNeeded` where rules switch over the outcome; translate the code where the application shows assistant codes itself. |
| `AddProposalGuard` accepts `NhAiProposalGuardStages.Proposal`; `All` does not include it. | None for existing guards; add `Proposal` to the guards that should keep done work from being proposed. |

## @newheap/platform-ai-chat: proposals a guard found not needed

A tool call whose proposal a proposal guard found not needed reads `assistant-proposal-not-needed`.
`NH_ASSISTANT_SERVER_ERROR_CODES` includes it, and the bundled English and Dutch texts name it under
`nh-assistant.result-codes` and `nh-assistant.errors`.

| Breaking change | Required action |
|---|---|
| A new error code with texts under `nh-assistant.result-codes` and `nh-assistant.errors`. | Add both keys to translations a host provides itself (`translations: 'host'`). |
