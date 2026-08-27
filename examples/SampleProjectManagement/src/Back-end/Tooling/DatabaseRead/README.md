# Governed database diagnostics MCP

`SampleProjectManagement.DatabaseRead.Mcp` is the executable consumer example
for exposing `newheap-db` to an agent. It publishes exactly two stdio MCP tools:

- `sample_database_schema_v1` searches or describes selectable live schema;
- `sample_database_query_v1` validates and executes parameterized read-only SQL.

The server, not the MCP caller, selects the checked-in profile and fixes the
authorization capability, eight-call budget, 1,000-row ceiling and 30-second
database timeout. The database credential still provides the security boundary
and must be a dedicated principal with only the required `CONNECT`, schema usage
and `SELECT` grants.

From `src/Back-end`, after providing the `NewHeapDiagnosticsReadOnly` secret for
the running sample database, start the stdio server with:

```powershell
dotnet run --project Applications/SampleProjectManagement.DatabaseRead.Mcp -- `
  --profiles .newheap/database-read.json `
  --profile sample-development
```

Configure an MCP client with that command and arguments. Inspect the repository
first to establish domain meaning and expected EF mappings. Call the schema tool
when physical identifiers are not already proven, then construct a query with
typed parameters, only the required columns and a PostgreSQL `LIMIT`. Do not let
model input choose the profile, credential, row limit, timeout or authorization
scope.

`DatabaseReadMcpSamplesTests` is the reproducible smoke test. It starts a real
PostgreSQL container, creates a SELECT-only login, lists the two MCP tools through
the official in-memory transport, describes `public."Projects"`, and executes a
typed UUID query that returns the seeded project.
