# Governed database diagnostics MCP

`SampleProjectManagement.DatabaseRead.Mcp` is the executable consumer example
for exposing `newheap-db` to an agent. It publishes exactly three stdio MCP tools:

- `sample_database_schema_v1` searches or describes selectable live schema;
  an exact description includes permission-filtered named outgoing and incoming
  relationships with ordered source/target column pairs and validation status;
- `sample_database_indexes_v1` returns positioned column or expression keys,
  direction, optional partial predicates, included columns and uniqueness for one
  confirmed object;
- `sample_database_query_v1` validates and executes parameterized read-only SQL.

The server, not the MCP caller, selects the checked-in profile and fixes the
authorization capability, sixteen-call budget, 1,000-row ceiling and 30-second
database timeout. Each query call must state `requestedRows` between 1 and 1,000.
An excessive request fails before execution, and an actual result above that
requested limit fails without returning partial data. The database credential
still provides the security boundary and must be a dedicated principal with only
the required `CONNECT`, schema usage and `SELECT` grants.

## Direct fast path for agents

For a question about persisted sample data, start in `src/Back-end` with the
repository-declared `sample-development` profile and the checked-in requests in
this directory. If the agent starts at the sample repository root, it first makes
one bounded tracked-file search for `**/.newheap/database-read.json`,
`**/Tooling/DatabaseRead/README.md`, and
`**/Tooling/DatabaseRead/requests/*.json`, then uses the directory containing the
only `.newheap` catalog as its working directory. The .NET tool manifest may be
in that directory or an ancestor; its location does not replace the catalog root.
Those files are the routing and JSON contract. Do not inspect controllers,
entities, EF configuration, appsettings, secrets or the tool's NuGet package
merely to rediscover the profile, provider, ceilings or request shape.

This catalog contains exactly one profile, so use `sample-development` without
asking for a profile selection. Its provider, configuration path,
`connectionStringName` and ceilings remain governed, while its `environment`
field is the default runtime environment. When the user explicitly requests
another environment, pass `--environment <name>` to both schema and query; do
not require a duplicate profile solely for Production or Staging. Let
`newheap-db` resolve `NewHeapDiagnosticsReadOnly` through the selected
environment's normal NewHeap configuration and secrets flow; do not read or
print the resolved connection string.

When deployed identifiers are not yet proven, use the direct schema route. It
needs no schema request file and automatically selects this catalog's only
profile. If the search has exactly one untruncated result,
`--describe-if-single` returns its columns, indexes and relationships in the
same connection and read-only transaction. Then execute the bounded query:

```powershell
dotnet tool run newheap-db schema --search Projects --schema-name public --describe-if-single
dotnet tool run newheap-db query --request-file Tooling/DatabaseRead/requests/project-by-id.json
```

For an explicitly requested Production diagnostic, use the same governed
profile and pass the runtime environment consistently:

```powershell
dotnet tool run newheap-db schema --environment Production --search Projects --schema-name public --describe-if-single
dotnet tool run newheap-db query --environment Production --request-file Tooling/DatabaseRead/requests/project-by-id.json
```

The tool still rejects the operation before reading data when the resolved
principal has write, DDL or elevated permissions.

Both execution commands validate internally. Do not run `validate` immediately
before either command; reserve it for CI or an intentional dry run. Refine the
schema search once only when it returns zero or multiple matches. Use no more
than one schema invocation and one query invocation for a simple count/latest
prompt, and stop as soon as the requested evidence is available.
`dotnet tool list --local` lists the manifest entry but does not prove the tool
was restored; run `dotnet tool restore` only when `newheap-db` reports that the
local command is unavailable.

From `src/Back-end`, after providing the `NewHeapDiagnosticsReadOnly` secret for
the running sample database, start the stdio server with:

```powershell
dotnet run --project Applications/SampleProjectManagement.DatabaseRead.Mcp -- `
  --profiles .newheap/database-read.json `
  --profile sample-development
```

Configure an MCP client with that command and arguments. Inspect the repository
first to establish domain meaning and expected EF mappings. Call the schema tool
when physical identifiers are not already proven, and use the exact description's
named relationships and ordered column pairs rather than guessing join columns.
Treat a relationship whose `isValidated` value is `false` as metadata without an
integrity guarantee; do not assume every existing row satisfies it.
When table size, predicate selectivity or ordering cost is uncertain, optionally
make at most one focused indexes call for the relevant already-confirmed object.
Use a suitable leading key or compatible ordering when it is immediately clear;
otherwise continue without spending more calls comparing indexes. A partial index
is usable only when the query predicate demonstrably implies its reported
predicate. An expression key helps only when the query uses a compatible
expression and ordering. Included columns are projection coverage, not predicate
keys. Then construct a query with typed parameters, only the required columns and
a PostgreSQL `LIMIT`. The model may
request a row count within the server-owned ceiling, but cannot choose the profile,
credential, timeout, authorization scope or a higher ceiling.

The checked-in `requests/project-schema.json`, `requests/project-indexes.json`
and `requests/project-by-id.json` files demonstrate repeatable JSON contracts
and can all be checked with `newheap-db validate --request-file <path>` in CI
without connecting to a database. The MCP route sends
structured input in memory. For direct CLI use, pass only a request-file path or
stream JSON through stdin; never place serialized JSON in a Windows process
argument or reduce the diagnostic candidate set merely to fit that limit.

`DatabaseReadMcpSamplesTests` is the reproducible smoke test. It starts a real
PostgreSQL container, creates a SELECT-only login, proves the Production prompt
“count the projects and return the latest by `CreationDateTime`” from a catalog
whose only profile defaults to Development with exactly one direct schema
invocation and one query invocation, lists the three MCP tools
through the official in-memory transport, describes `public."Projects"` and its
incoming/outgoing relationships, inspects its live composite, partial and expression
indexes, executes a typed UUID query, proves the sixteen-call per-invocation budget,
rejects a 5,000-row request before execution, and rejects an actual result above
`requestedRows` without exposing partial rows.
