# v-next

## NewHeap.Platform.AI.Chat: compositions next to a scoped tool catalog

A host with a composition (`AddComposition`) and a tool catalog registered as a scoped service failed to
start where it validates scopes, as Development does: "Cannot resolve scoped service ... from root
provider" (FB60). The composition catalog, a singleton, read the steps' tools from the root provider.
It now reads them once, at startup, from the catalogs of a scope of its own, which it disposes; only the
descriptors outlive it. A step still runs with the tool of the turn's own services. A step's tool must not
depend on the request: a step calls only a tool that a scope without a request has too, and a catalog
whose descriptors change while the host runs implements `INhAiChangingToolCatalog`, which compositions
leave out. Nothing changes for a host without scoped catalogs. SPM-372 shows it in the sample.

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: the newest skill candidate wins

`INhAssistantInstructionAssetUsage.ProposeCandidatesAsync` compared a proposed asset version only with the
live skill, and the store discarded the skill's candidate unconditionally, so proposing version 2 after
version 3, with version 1 live, replaced the candidate of version 3 with version 2; two proposals at once
could end the same way (FB62). The skill now keeps its newest candidate. The store decides in the save's
transaction (compare-and-set): a candidate of an older version of the asset, or one that names no asset
or another one, is discarded as before, while a candidate of a newer version stays. Such a proposal
answers the new outcome `NhAssistantInstructionAssetCandidateOutcomes.NewerProposed`
(`newer-proposed`) with the candidate's version, and saves and audits nothing for that skill. Of two
proposals at once, the newer version stays. An administrator's own save of a skill still replaces its
candidate. An application that replaced `INhAssistantAgentConfiguration`, for example with a decorator,
still receives every save of a proposal; there the check before the save answers `newer-proposed`, but
two proposals at once are not decided by the store, so run them one at a time. SPM-351 shows it in the
sample.

| Breaking change | Required action |
|---|---|
| `NhAssistantInstructionAssetCandidateDto.Outcome` can be `newer-proposed`, where a proposal of an older asset version used to replace a newer candidate and answer `proposed`. | Handle `newer-proposed` where you switch on the outcome, as nothing saved for that skill; it is not a failure. |
