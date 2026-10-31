# v-next

## NewHeap.Platform.DatabaseRead.Tool: session, catalog and MCP server

`newheap-db session` serves many `query` and `schema` requests in one process over JSON lines, so a caller pays for a
process start, a connection and a principal verification once. The first line on stdout, written before any profile or
connection, is what a caller detects the capability by: a tool without sessions answers `unknown-command`. A request
keeps the validator, read-only policy, limits, read-only transaction, response JSON and exit code of the one-shot
command. Requests reuse a pooled connection whose session state the driver resets between them. The principal is
verified on first use, on every new connection and after `--verification-interval-seconds` (default 60). A session ends
at the end of stdin, after `--maximum-idle-seconds` (default 300) or after `--maximum-lifetime-seconds` (default 3600).

The schema operation `catalog`, with an optional `schemaName`, describes every selectable object on one connection and
read-only transaction, in one-shot mode and in a session. Each object is what `describe` returns, with the same evidence
hash. A catalog that does not fit its row, output or time limit returns `truncated: true`.

`newheap-db mcp` is a stdio MCP server for an agent, with the read-only tools `database_schema_search`,
`database_schema_describe`, `database_schema_indexes`, `database_schema_catalog` and `database_query`. It gives an agent
structured calls instead of a request file per query. The profile and environment are fixed when the server starts, and
no tool argument reaches them. A refused or failed call is a tool error with the tool's safe error JSON. The package
grows by about 2 MB, for `ModelContextProtocol.Core` and `Microsoft.Extensions.AI.Abstractions`, which a one-shot command
does not load.

One-shot `validate`, `query` and `schema` keep their behavior, JSON and exit codes; only the text of
`invalid-schema-operation` now also names `catalog`.
