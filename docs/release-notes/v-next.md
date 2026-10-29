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
