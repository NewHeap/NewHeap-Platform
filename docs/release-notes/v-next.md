# v-next

## NewHeap.Platform.AI.Chat.AspNet: request delegates generated at build time

The library builds with ASP.NET Core's Request Delegate Generator (`EnableRequestDelegateGenerator`), so
the request delegates of the assistant's 139 minimal API endpoints are written when the package is built.
A host that calls `MapNewHeapAssistant` no longer compiles them with `RequestDelegateFactory` while it
starts. Applications change nothing.

Measured with `AssistantRequestDelegateTests.Measure_mapping_and_building_the_assistant_endpoints`
(`NH_MEASURE_ENDPOINTS=1`, Release, Windows 11, Intel Core Ultra 9 275HX, .NET 10.0.12): the time of
`MapNewHeapAssistant` plus the first read of every endpoint, for 20 hosts in one process after three
warm-up hosts, and for the first host of the process. The ranges cover four runs before and five after;
the first host was recorded in two and four of them.

| Per host | Before | After |
|---|---:|---:|
| Wall-clock time, median | 225–231 ms | 6.5–7.0 ms |
| Process CPU time, mean | 248–270 ms | 6–11 ms |
| First host of the process, wall-clock time | 270–271 ms | 91–95 ms |

Routes, endpoint names, summaries, `Produces` metadata, tags, filters, authorization, the binding of
route and query values (a malformed value still answers 400) and the 404 of a disabled assistant are
unchanged; the sample's OpenAPI document is the same. The endpoints' metadata now also holds the
generator's `GeneratedCodeAttribute`, and the generator's `IParameterBindingMetadata` reports
`HasTryParse` false for `string` parameters, which need no parsing.

| Breaking change | Required action |
|---|---|
| None. | None. |

## NewHeap.Platform.AspNet.Common: background-operation errors during a cancellation and the hub path

The runner's failure path now checks the platform's stop signals before it records an error that a
handler threw outside a step, as the recording of a failed step already does:

- While the attempt's cancellation is requested, the error no longer fails the operation. It ends
  `Cancelled` (`cancelled`, `background-operation.cancelled`), or `TimedOut` (`soft-timeout`) when
  the soft time-out fired first, and nothing retries the error, which is logged as a warning with the
  operation and attempt. This covers a client library that reports a cancelled call as an error of
  its own as well as an unrelated error after the owner cancelled.
- A lost fence and contention for the operation lock that the heartbeat recorded still win: the
  stopped attempt records nothing, or the operation is rescheduled as busy without spending a retry.
- Without such a signal the error fails the attempt as `handler-failed` with the operation's retry
  rules, as before.

`NhBackgroundOperationsOptions.HubPath` no longer has to lie below `/hub`. Since the query token
follows the configured hub paths, any absolute application path below the root works, such as
`/hubs/background-operations`, except a path on the operations' own API under
`/background-operations`. The default stays `/hub/background-operations`.

| Breaking change | Required action |
|---|---|
| An error that a handler throws outside a step while its cancellation is requested ends the operation `Cancelled`, or `TimedOut` after a fired soft time-out, without a retry, instead of `handler-failed` with a scheduled retry or a terminal failure; it is logged as a warning without a diagnostic correlation id. | None, unless monitoring, a notification policy or a test expected `handler-failed` for such an error; expect `Cancelled`. |
| An error that a handler throws outside a step after the heartbeat stopped the attempt for lock contention reschedules the operation as `concurrency-busy` without spending a retry, instead of recording `handler-failed`. | None. |
| None for `HubPath`: a path outside `/hub` is now accepted, while a relative, empty or root path and a path under `/background-operations` are still refused. | None. A host that moves the hub points the Angular client's `hubUrlSuffix` at the same path. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: chat-only agents

`run.ChatOnly()` on `ConfigureAgent` gives an agent that only works in people's chats its chat
settings without run settings: its skills in chat (`Skill` with `SkillsInChat`), `AllowQuestions`,
`MaxConsultationsPerTurn` for the consultations it starts, `MaxTokensPerRun`, `AcceptsFiles`,
`DataClassification`, `UseApprovalPolicy` and `Metadata`. It works on no subject: no object's page and
no subject's list of agents shows it, `POST threads/start`, `POST threads/open`, run now,
`INhAssistantAgentTriggers.StartAsync` and a hand-off are refused with
`assistant-agent-does-not-work-on`, as are a data trigger, its shadow switch and a board; nothing
triggers or consults it, a replay never runs into or from it, run maintenance leaves it out and it
needs no background operations. The host refuses to start when the agent also uses a setting that only
applies to runs on subjects (such as `WorksOn`, `AcceptHandOffs`, `Shadow`, `HideFromChat`,
`AllowConsultation`, `Grant` or `Retry`; the exception names them), when a trigger starts it, another
agent is its shadow variant or accepts hand-offs from it, or `UseHandOffsAsData` or
`UseShadowVariantsAsData` names it. `NhAssistantAgentRunOptions.ChatOnly` reports it, and the
administration shows it as `ChatOnly` of `NhAssistantAdminAgentDto`, `NhAssistantCodeRunSettingsDto`
and `NhAssistantAgentInEffectDto`.

| Breaking change | Required action |
|---|---|
| `NhAssistantAdminAgentDto` (`GET admin/agents`), `NhAssistantCodeRunSettingsDto` and `NhAssistantAgentInEffectDto` gain `ChatOnly` (`chatOnly` in JSON). | None, unless a client or test compares the full set of properties; add `chatOnly`. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: promote a variant as the package's next version

`PromoteShadowVariantAsync` with `NhAssistantShadowVariantPromotionRequest { AsPackageVersion = true }`
(`asPackageVersion` of `POST admin/agents/{id}/promote`) promotes a shadow variant whose every part is
linked to one agent package as that package's next version of its live agent. In one transaction it
applies the variant's parts with the import's own rules (its definition, run settings, skills,
triggers, consultations, hand-offs and metadata, with the package's locks and hashes; the live agent
keeps its display name, chat offer and technical account, an agent in code its run settings in code),
links the live agent's parts to the variant's package id and version, takes the live agent out of the
package it belonged to and deletes the variant with its links. A part without a link refuses with
`assistant-package-not-linked` and a part the import refuses refuses everything with
`assistant-package-refused` (`parts.{part}.{field}` names it). One `AdminAgentShadowVariantPromoted`
event with the result `promoted-as-package-version` and the package's id, version and hash records it;
`NhAssistantShadowVariantPromotionDto.Package` and the answer's `package` report every part's outcome.
`GET admin/agents/{id}/shadow-variants` names a data variant's `PackageId` and `PackageVersion`.
Without the option the promotion is unchanged.

| Breaking change | Required action |
|---|---|
| `NhAssistantShadowVariantDto` gains `PackageId` and `PackageVersion`, `NhAssistantShadowVariantPromotionInputDto` gains `AsPackageVersion` and `NhAssistantShadowVariantPromotedDto` gains `Package` (left out when null). | None, unless a client or test compares the full set of properties; add them. |
| `AdminAgentShadowVariantPromoted` can carry the result `promoted-as-package-version` (`NhAssistantShadowVariantPromotions`) with `PackageId`, `PackageVersion` and `PackageHash`. | None, unless an audit consumer expects only `promoted`; accept the new result. |

## @newheap/platform-ai-chat: chat-only agents and promotion as the package's next version

The agents page shows an agent in code that works in people's chats only (`AdminAgent.chatOnly`) with
a "Chat only" badge and a hint instead of what it works on, without "Run now", runs, run state changes
or shadow mode. The shadow variants of an agent name the package a data variant is linked to
(`ShadowVariant.packageId`, `packageVersion`), and the promotion's confirmation offers "As the
package's next version" for such a variant, which sends `asPackageVersion` (`ShadowVariantPromotionRequest`)
and reads `ShadowVariantPromotion.package`. English and Dutch texts. The mock API promotes a linked
variant as an import of its package version, names a variant's package, and lets a scenario declare a
chat-only code agent with `chatOnly: true`, whose starts and shadow mode it refuses.

| Breaking change | Required action |
|---|---|
| The mock API's shadow variants carry `packageId` and `packageVersion` (`null` without a package), as the server's do. | None, unless a test compares a variant with `toEqual`; add both properties. |
