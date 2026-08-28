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
and `requests/project-by-id.json` files demonstrate the corresponding direct
`newheap-db` contracts and can all be checked with `newheap-db validate
--request-file <path>` before connecting to a database. The MCP route sends
structured input in memory. For direct CLI use, pass only a request-file path or
stream JSON through stdin; never place serialized JSON in a Windows process
argument or reduce the diagnostic candidate set merely to fit that limit.

`DatabaseReadMcpSamplesTests` is the reproducible smoke test. It starts a real
PostgreSQL container, creates a SELECT-only login, lists the three MCP tools
through the official in-memory transport, describes `public."Projects"` and its
incoming/outgoing relationships, inspects its live composite, partial and expression
indexes, executes a typed UUID query, proves the sixteen-call per-invocation budget,
rejects a 5,000-row request before execution, and rejects an actual result above
`requestedRows` without exposing partial rows.
