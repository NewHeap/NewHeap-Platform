# v-next

## NewHeap.Platform.AI.Generators and NewHeap.Platform.AI.Scripting: DateOnly and TimeOnly in tool schemas

The tool generator described a `DateOnly` and a `TimeOnly` as objects of their public properties, all
required (`{ day; dayNumber; dayOfWeek; dayOfYear; month; year }`), while System.Text.Json reads and
writes them as `"2026-11-01"` and `"14:30:00"`. The model was shown a shape the tool could not bind, and
a script's call check refused every date the tool accepts. A generated schema now has
`{"type":"string","format":"date"}` for a `DateOnly` and `{"type":"string","format":"time"}` for a
`TimeOnly`, nullable, in lists and in nested types included, as the MVC bridge and composed tools already
had. A script sees them as `string /* date */` and `string /* time */`, and its call check accepts only
`yyyy-MM-dd` for a `date` and `HH:mm[:ss[.fffffff]]` without an offset for a `time`; any other string
stops the script with `ai-script-call-invalid` at that field before the tool runs. SPM-375 shows it in the
sample.

| Breaking change | Required action |
|---|---|
| A generated tool with a `DateOnly` or `TimeOnly` in its input or output has a new schema, schema hash and contract hash after the rebuild, so a proposal of that tool recorded before the deployment no longer runs when it is approved (`ai-tool-approval-invalid` or `assistant-approval-invalid`). | Decide the open proposals of such tools before you deploy, or let the model propose them again afterwards; update a manifest or contract hash the application pins itself. |
| An application that imports such a tool over MCP sees it disabled as `schema-changed` once the exporting application is deployed, because its reviewed schema changed. | An administrator reviews the tool again and activates it in the assistant's MCP administration. |
| A script's call check now refuses a `date` or `time` string in another form, also for an imported MCP tool whose schema declares these formats; a `time` with an offset such as `14:30:00Z` is refused. | Pass `yyyy-MM-dd` and `HH:mm[:ss[.fffffff]]`; a script that passed another form stops at that field and the model corrects it. |
