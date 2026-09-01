# NewHeap database read tool

`newheap-db` inspects selectable database schema and executes parameterized
diagnostic queries through the same appsettings, environment and
secret-substitution flow as a NewHeap application.
The connection string is selected by a checked-in profile and is never accepted
in the JSON request, command-line arguments or output.

The tool is intended for incident investigation and application debugging. It
is not a reporting, migration, repair or administration tool.

## Install

Pin the tool in the consumer repository so every developer and agent runs the
same version:

```text
dotnet new tool-manifest
dotnet tool install NewHeap.Platform.DatabaseRead.Tool
dotnet tool restore
```

## Profile catalog

Create `.newheap/database-read.json` at the consumer root. Paths are relative to
that root and may not escape it.

```json
{
  "schemaVersion": 1,
  "profiles": {
    "staging": {
      "provider": "postgresql",
      "configurationPath": "src/Back-end/Applications/Example.Api",
      "environment": "Staging",
      "connectionStringName": "NewHeapDiagnosticsReadOnly",
      "maximumRows": 200,
      "maximumTimeoutSeconds": 30,
      "maximumLockTimeoutMilliseconds": 5000,
      "maximumOutputBytes": 1048576,
      "maximumCellBytes": 16384,
      "maximumSqlBytes": 32768
    }
  }
}
```

When the catalog contains exactly one profile, `newheap-db` selects it
automatically. Use `--profile <name>` or the JSON `profile` property when a
catalog contains multiple profiles. The selected connection string must use a dedicated read-only database
principal. `ApplicationIntent=ReadOnly`, SQL validation and transaction rollback
are only additional safeguards. The `query` and `schema` commands refuse
principals with detected write, DDL or elevated permissions.

The profile owns the operational limits for its consumer. NewHeap supplies
defaults and a generous hard safety ceiling, while each application selects the
row, timeout, output, cell and SQL bounds appropriate for its environment.

Keep the connection string in the normal substitution path. For example, the
application appsettings can contain:

```json
{
  "ConnectionStrings": {
    "NewHeapDiagnosticsReadOnly": "${Secrets:ConnectionStrings:NewHeapDiagnosticsReadOnly}"
  }
}
```

The matching local or hosted `secrets.json` supplies the value. Do not put it in
the profile catalog.

## JSON request

Send one request through standard input or pass a request file with
`--request-file <path>`. Data values are separate parameters; do not concatenate
them into SQL.

```json
{
  "schemaVersion": 1,
  "profile": "staging",
  "sql": "SELECT Id, Name FROM Projects WHERE Id = @projectId LIMIT 100",
  "parameters": [
    {
      "name": "projectId",
      "type": "uuid",
      "value": "9894826e-92bd-4483-b842-74979cd399ee"
    }
  ],
  "limits": {
    "maximumRows": 100,
    "timeoutSeconds": 15
  },
  "reason": "Investigate a project missing from the API response"
}
```

Supported parameter types are `string`, `boolean`, `int32`, `int64`, `decimal`,
`double`, `uuid`, `date-time`, `date`, and `binary-base64`. Use ISO 8601 for
`date-time`, `yyyy-MM-dd` for `date`, and strings for `int64` or `decimal` when
JSON number precision would be unsafe. A JSON `null` is accepted for every
type. SQL identifiers are not data parameters; keep table and column names fixed
in reviewed request files.

Use `validate` only for CI, request-template checks or another deliberate dry
run without a database connection:

```text
newheap-db validate --request-file request.json
```

For an interactive diagnostic, call `query` directly. It performs the same
request validation before it verifies the database principal and executes:

```text
newheap-db query --request-file request.json
```

Standard input remains available for streaming callers. `--request-file` is the
portable CLI choice when a request is already on disk or when a shell wrapper
would otherwise put serialized JSON inside a Windows process argument. Pass only
the path on the command line. Never inline the JSON in `powershell -Command`,
`cmd /c`, `dotnet run` arguments or another process-launch string. Transport size
must not change the diagnostic predicate or candidate set.

For interactive discovery, inspect the schema visible to the same read-only
principal without a JSON schema request:

```text
newheap-db schema --search project --schema-name public --describe-if-single
```

The command selects the catalog's only profile automatically. If the bounded
search has exactly one untruncated match, `--describe-if-single` returns the
matching summary and its columns, indexes and relationships through the same
connection and read-only transaction. If zero or multiple objects match, the
response contains only the bounded summaries so the caller can refine once.
Do not run `validate` immediately before `schema` or `query`; both execution
commands validate internally.

Checked-in schema requests remain useful for CI and repeatable diagnostics:

```json
{
  "schemaVersion": 1,
  "profile": "staging",
  "schema": {
    "operation": "search",
    "schemaName": "public",
    "searchTerm": "project"
  },
  "limits": {
    "maximumRows": 100,
    "timeoutSeconds": 15
  },
  "reason": "Find the deployed project objects before constructing a data query"
}
```

Use `operation: "search-and-describe"` to perform the same conditional exact
description as `--describe-if-single`. Use `operation: "describe"` with exact
`schemaName` and `objectName` values to
receive selectable columns, primary-key markers, indexes, a provider-quoted SQL
identifier and an evidence hash. Use `operation: "indexes"` with those same
exact identifiers for a smaller, focused response containing index uniqueness,
primary-key and partial-index markers, ordered key columns with
ascending/descending direction and included columns. Filter predicates themselves
remain hidden. This metadata is returned only when the configured read-only
principal can select the object and every reported index column. Execute any
schema request with:

```text
newheap-db schema --request-file schema-request.json
```

Schema search, description and index inspection return only objects and columns
visible to the configured principal. They never return view definitions, default
expressions, index filter predicates, stored routines or provider exception text.

When table size, predicate selectivity or ordering cost is uncertain, optionally
make at most one focused index request for the already-confirmed object. Use a
suitable leading key or compatible ordering when it is immediately clear.
Included columns can indicate projection coverage, but are not predicate keys. A
partial index is useful only when its predicate is already established by trusted
repository evidence. Do not spend follow-up calls comparing ambiguous indexes.
If no directly useful index is reported, continue with a parameterized,
provider-capped and timeout-bounded query; do not compensate with a broader scan,
larger row limit or longer timeout.

Standard output contains exactly one JSON response. Long and decimal values are
encoded as invariant strings to preserve precision. Rows are arrays paired with
column metadata, so duplicate column names remain unambiguous. Errors use stable
codes and never include provider exception text or a connection string.

```json
{
  "schemaVersion": 1,
  "ok": true,
  "operation": "query",
  "requestId": "64d51b5b783d44189018526fd071e79d",
  "target": {
    "profile": "staging",
    "provider": "postgresql",
    "environment": "Staging",
    "readOnlyVerified": true
  },
  "result": {
    "columns": [
      { "name": "Id", "providerType": "uuid", "allowsNull": false },
      { "name": "Name", "providerType": "text", "allowsNull": false }
    ],
    "rows": [
      ["9894826e-92bd-4483-b842-74979cd399ee", "Example"]
    ],
    "rowCount": 1,
    "truncated": false,
    "truncatedCellCount": 0
  },
  "timing": { "elapsedMilliseconds": 18 }
}
```

Database failures retain the stable `database-query-failed` code and may add an
allowlisted `classification`, `provider`, `providerCode` and `transient` value.
For example, PostgreSQL SQLSTATE `42P01` is returned as `object-not-found`, while
SQL Server error `207` is returned as `column-not-found`. Raw provider messages,
object names, connection values and stack traces are never copied into the error
contract.

Exit codes are `0` for success, `2` for an invalid request, `3` for an invalid
profile, `4` for a policy rejection, `5` for a database failure, and `130` for
cancellation. Exit code `1` is reserved for an unexpected tool failure.

## Safety boundary

- Use a dedicated database identity that cannot write, execute application
  procedures, create objects or administer the server.
- Prefer a read replica or masked diagnostic views for production data.
- Grant access only to schemas, views and columns approved for the developers or
  agents that will consume the output.
- Treat query results as potentially sensitive data even though configuration
  secrets are not displayed.
- Treat index metadata as query-design evidence, not as permission to run an
  otherwise broad scan or `EXPLAIN ANALYZE`.
- Do not use the tool for bulk exports, automated jobs or data repair.
