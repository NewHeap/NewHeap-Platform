# NewHeap.Platform.AI.Generators

Compile-time generator for NewHeap AI tool catalogs. Reference this package as
an analyzer, annotate an application-owned class with `NhAiToolSet`, and
annotate supported methods with `NhAiTool` plus `Description`.

The generator emits a local `INhAiToolCatalog`, canonical input/output schemas,
SHA-256 contract and catalog hashes, and a deterministic manifest. It reports
`NHAI` diagnostics for invalid IDs or versions, signatures, descriptions,
duplicate contracts, missing tool-set metadata, and remote exposure without an
authorization boundary. The compiler-hosted generator deliberately has no
runtime dependency on `NewHeap.Platform.AI.Common`.

Generated tools never pass the MVC authorization filter, so the descriptor's
policy list is the boundary the tool gate enforces. The generator reads every
`[Authorize]` declaration on the method, the methods it overrides, the tool set
and its base types, with the policy from the named `Policy` argument or the
constructor argument. `Roles`, `AuthenticationSchemes` and attribute classes
other than `AuthorizeAttribute` itself report `NHAI018`; express them as a named
policy.

Each generated function returns the shared `NhAiToolResult` shape: the output as
`data` on success, otherwise the stable failure `code` and `message`. Only `data`
follows the tool set's serializer options.

Trimmed Native AOT applications must provide an application-owned
`JsonSerializerContext` with metadata for each tool input and output type, then
set `NhAiToolSet.JsonSerializerContextType` to that context. The context no longer
needs `TaskResult<TOutput>`; existing contexts that declare it keep working. The
generated function factory uses its serializer options instead of depending on
reflection metadata that trimming may remove.
