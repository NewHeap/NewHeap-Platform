# v-next

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
