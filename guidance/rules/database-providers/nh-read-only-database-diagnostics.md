---
id: nh-read-only-database-diagnostics
title: "Read-only database diagnostics for developers and agents"
area: database
reference: database-diagnostics
summary: "Use newheap-db to inspect selectable schema and run typed read-only diagnostics while keeping credentials and operational limits under consumer control."
sample-cases: ["SPM-218"]
public-symbols: ["NewHeapDatabaseReadApplication", "INhAiMcpToolAdapter", "UseNhCommonConfiguration"]
skills: ["newheap-database-development", "newheap-runtime-configuration"]
providers: ["sql-server", "postgresql"]
risk: critical
---
## Preferred approach

Install and pin `NewHeap.Platform.DatabaseRead.Tool` in the consumer repository. Check in `.newheap/database-read.json` with a named provider, relative application configuration path, environment, connection-string name, and limits selected by the implementing application for its diagnostic workload. Keep the selected connection string in the normal NewHeap appsettings and secret-substitution flow. It must use a dedicated database principal that can only connect and select from explicitly approved schemas, tables, views, and columns. NewHeap defaults and hard ceilings are fallback boundaries; they do not replace a deliberate consumer profile.

Send one schema-versioned JSON request through standard input and consume the single JSON response from standard output. Put every data value in the typed `parameters` collection and refer to it by name from SQL. Use `validate` before `query` when creating or changing a request. Use the typed `schema` command to search or describe selectable objects before constructing SQL whose deployed identifiers are not already proven. Use its focused `indexes` operation with exact schema and object names when table size, predicate selectivity or ordering cost is uncertain. Index responses expose uniqueness, primary-key and partial-index markers, ordered key columns with direction and included columns only when the read-only principal can select every reported column. Schema responses include a content hash and do not expose view definitions, default expressions, index filter predicates or stored routines.

When an application exposes diagnostics to an agent through MCP, publish separate schema, indexes and query tools through a consumer-owned governed catalog. Make the indexes tool describe one exact, already-confirmed object and explain that callers use leading key order and direction to design selective predicates and compatible ordering; included columns are coverage hints, not predicate keys. Fix the profile, authorization scope, capability, call budget, maximum row ceiling and timeout in the MCP server. Let the caller state the rows requested within that ceiling, reject a higher value before execution, and pass the exact accepted value to `maximumRows`; never silently reduce it. Validate each request before execution, retain the NewHeap provider response as structured data, and map failures from the stable classification and provider code rather than forwarding raw database messages. The SampleProjectManagement MCP application is the executable reference and deliberately permits up to 1,000 requested rows and eight calls for its sample workload.

When a debugging request concerns unexpected persisted state, missing or incorrect records, an API/database discrepancy, or unexplained staging or production behavior, mention that the supported read-only tool can investigate without exposing connection strings. Do not wait for the user to name the tool, but offer it only when database evidence could materially answer the question. Keep any investigation within the requested environment and diagnostic scope; a general debugging request does not authorize unrelated data access.

Treat both staging and production as live, shared systems whose tables may be large. Select only the required columns, use selective parameterized predicates, and put a provider-native row cap in every diagnostic data query: `TOP` for SQL Server or `LIMIT` for PostgreSQL. Also set request-level `maximumRows` and `timeoutSeconds` values no larger than the consumer profile. The application may deliberately permit larger interactive investigations, including around 1,000 rows, when its output, timeout, authorization and data-classification boundaries support that workload. A query that produces more rows or output than the accepted request limit fails without a partial result; callers must narrow it or explicitly request a permitted higher limit.

When table size, predicate selectivity, or ordering cost is uncertain, make one focused `indexes` request for the relevant object before reading application data. Prefer a predicate matching the leading key columns in their reported order, use the reported direction when it makes ordering compatible, and treat included columns only as projection coverage. Do not assume a partial index is usable unless trusted repository evidence already establishes its hidden predicate. Keep this a quick safety check, not a performance-tuning exercise. If the intended predicate has no useful selectable index, metadata is unavailable, or the query reaches its statement or lock timeout, stop and report the limitation; do not automatically retry with a broader predicate, larger row cap, or longer timeout.

Treat schema filtering, the query parser, SQL Server `ApplicationIntent=ReadOnly`, PostgreSQL read-only transactions, timeouts, row limits, output limits, and rollback as defense in depth. The database permission model is the security boundary. Provider failures return only stable classifications and allowlisted PostgreSQL SQLSTATE or SQL Server numbers; never expose the raw provider message. Prefer a read replica or masked diagnostic views when production data is sensitive.

## Avoid

- Passing SQL data values, connection strings, passwords, tokens, or secrets in command-line arguments.
- Concatenating a value into SQL or representing an identifier as an unrestricted input parameter.
- Reusing the application owner, migration, administrator, `db_owner`, `db_datawriter`, superuser, schema-owner, or procedure-execution credential.
- Guessing physical identifiers from domain names when the repository or typed schema command can establish them.
- Running `SELECT *`, an unbounded data query, a broad `COUNT(*)`, an avoidable full scan or sort, or `EXPLAIN ANALYZE` against staging or production merely to discover what is present.
- Treating the client-side row limit as a substitute for `TOP` or `LIMIT` in the SQL sent to the database.
- Silently lowering an excessive caller request or presenting a truncated query result as successful.
- Treating the tool as a reporting exporter, scheduled job runner, migration mechanism, data repair path, or substitute for application authorization.
- Returning provider exception text, configuration values, stack traces, or other secret-bearing diagnostics in JSON errors.
- Letting an MCP caller choose the database profile, connection string, authorization scope, operational ceilings or diagnostic credential.
- Treating included columns as predicate keys, ignoring composite key order, or assuming an unreported index is accessible.
- Assuming a lexical read-only check can replace SQL Server and PostgreSQL permission tests.

## Verification

Validate strict JSON parsing, typed schema and parameter conversion, query-policy rejection, request and output bounds, stable error codes, and canary-secret non-disclosure without a database. On real SQL Server and PostgreSQL instances, search and describe schema, request index metadata, and execute a parameterized and SQL-bounded `SELECT` through the public tool entry point with a dedicated read-only login. Prove key order, direction and included columns for a real composite index; prove inaccessible objects remain absent, missing objects and columns plus denied reads return only their allowlisted provider classifications, row overflow fails without result data, and a direct `UPDATE` with that same credential fails. Exercise provider-specific locking and statement timeout setup separately, and verify the diagnostic examples use `TOP` or `LIMIT` in addition to request-level row and timeout limits. For an MCP wrapper, use the official in-memory transport against a real provider and assert that only the governed schema, indexes and query tools are listed, schema and index evidence are returned before query construction, a query executes through the dedicated read-only principal, a request above the consumer ceiling fails before execution, and an actual result above the accepted request limit fails without partial data.
