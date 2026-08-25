# NewHeap database read tool

`newheap-db` executes small, parameterized diagnostic queries through the same
appsettings, environment and secret-substitution flow as a NewHeap application.
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

The selected connection string must use a dedicated read-only database
principal. `ApplicationIntent=ReadOnly`, SQL validation and transaction rollback
are only additional safeguards. The `query` command refuses principals with
detected write, DDL or elevated permissions.

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

Send one request through standard input. Data values are separate parameters;
do not concatenate them into SQL.

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

Validate without a database connection:

```text
newheap-db validate < request.json
```

Execute after verifying the database principal:

```text
newheap-db query < request.json
```

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
- Do not use the tool for bulk exports, automated jobs or data repair.
