# v-next

<!-- AI assistant and API bridge lanes: each lane writes only below its own heading. The coordinator regroups these sections into the per-package format before merge. -->

## Lane A

### NewHeap.Platform.AI.Common, NewHeap.Platform.AI.Mcp, NewHeap.Platform.AI.AspNet.Common

| Change | Required action |
| --- | --- |
| AI.Common: attested runtime catalogs (`INhAiAttestedToolCatalog`, `NhAiToolCatalogAttestation.Validate`) may enter the MCP export path; `WithNewHeapPlatformAITools` validates them at startup. | No action for existing consumers. |
| AI.AspNet.Common: `AddNewHeapPlatformAIAspNet` registers `INhAiCallerCredentialAccessor` (saved `access_token`, then the `Authorization: Bearer` header) for same-user delegated calls; the token never enters `NhAiInvocationContext`. | No action; register your own implementation before the call to replace it. |

### NewHeap.Platform.AI.AspNet.Mvc (new package)

| Change | Required action |
| --- | --- |
| New `AddNewHeapPlatformAIMvcBridge` publishes policy-protected MVC actions as governed, attested tools that run through `INhAiToolInvoker` and the API's own HTTP pipeline as the calling user. | Opt in per API; register `AddNewHeapPlatformAIAspNet`, a budget and an idempotency manager, then configure your own discovery policy with `UseInnerDiscoveryPolicy` instead of `UseDiscoveryPolicy`. |
| The bridge never publishes DELETE, anonymous, upload or policy-less actions; `IncludeDeleteActions(true)` fails at startup in v1. | Expose deletions through a curated tool with a verifier. |

## Lane B

## Lane C
