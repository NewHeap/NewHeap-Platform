# v-next

<!-- AI assistant and API bridge lanes: each lane writes only below its own heading. The coordinator regroups these sections into the per-package format before merge. -->

## Lane A

### NewHeap.Platform.AI.Common, NewHeap.Platform.AI.Mcp, NewHeap.Platform.AI.AspNet.Common

| Change | Required action |
| --- | --- |
| AI.Common: attested runtime catalogs (`INhAiAttestedToolCatalog`, `NhAiToolCatalogAttestation.Validate`) may enter the MCP export path; `WithNewHeapPlatformAITools` validates them at startup. | No action for existing consumers. |

## Lane B

## Lane C
