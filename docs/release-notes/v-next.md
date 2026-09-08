# v-next

## NewHeap.Platform.AI.Mcp

| Breaking change | Required action |
|---|---|
| The NewHeap MCP export path now accepts only source-generated `INhAiGeneratedToolCatalog` implementations and reserves their public export names. | Generate NewHeap catalogs with `NewHeap.Platform.AI.Generators` and register `WithNewHeapPlatformAITools()`. External libraries may retain SDK tool registration under distinct names; remove only duplicate NewHeap publication paths and name collisions. |
