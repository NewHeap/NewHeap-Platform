# NewHeap database read tool

`newheap-db` inspects selectable database schema and executes parameterized
diagnostic queries through the same appsettings, environment and
secret-substitution flow as a NewHeap application.
The connection-string name is selected by a checked-in profile and the value is
never accepted in the JSON request, command-line arguments or output. The
profile's environment is the default runtime environment; an explicitly requested
diagnostic scope can override only that environment with `--environment`.

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
catalog contains multiple profiles. The profile environment is a default, not an
environment allowlist. When the user explicitly requests another runtime
environment, pass `--environment <name>` to both schema and query commands. The
selected profile still governs the provider, configuration path,
connection-string name and operational ceilings, so the override cannot select
an arbitrary credential or configuration location.

The resolved connection string must use a dedicated read-only database
principal. `ApplicationIntent=ReadOnly`, SQL validation and transaction rollback
are only additional safeguards. The `query` and `schema` commands refuse
principals with detected write, DDL or elevated permissions, including in
Production.

A rejected principal returns `read-only-principal-not-verified` with an
optional `verificationCheck` that names the first rejected check, with stage
`readonly-verification`. The field never names the principal or an object.

On SQL Server the function policy is strict: SELECT or EXECUTE on any
user-defined function rejects the principal with `function-select` or
`function-execute`, because the tool does not verify a function's dependencies
or side effects. Grant the diagnostic principal table, view or column
permissions instead.

SQL Server verification checks the effective permissions of the login and the
database user, including those held through roles, ownership and implication.
After the role and permission checks above it rejects `VIEW SERVER STATE`
(also through `##MS_ServerStateReader##`), `ALTER ANY CONNECTION`,
`IMPERSONATE ANY LOGIN`, `ALTER ANY SCHEMA`, IMPERSONATE on a login or a user,
UPDATE on a sequence, ALTER or TAKE OWNERSHIP on a schema or an object and
column-level UPDATE, with `server-permission-view-server-state`,
`server-permission-alter-any-connection`,
`server-permission-impersonate-any-login`,
`database-permission-alter-any-schema`, `login-impersonate`,
`user-impersonate`, `sequence-update`, `schema-permission-alter`,
`schema-permission-take-ownership`, `object-permission-alter`,
`object-permission-take-ownership` and `column-update`. It then denies by
default: on the server, the database, a schema, an object, a column, a login,
a server role, a database principal or another database securable, every
permission the server knows other than connecting, SELECT, metadata visibility
(`VIEW DEFINITION`, `VIEW CHANGE TRACKING`, `SHOWPLAN`) and the column-key
definitions that every database grants to public rejects the principal with
`server-permission-not-allowlisted`, `database-permission-not-allowlisted`,
`schema-permission-not-allowlisted`, `object-permission-not-allowlisted`,
`column-permission-not-allowlisted`,
`server-principal-permission-not-allowlisted`,
`database-principal-permission-not-allowlisted` or
`database-securable-permission-not-allowlisted`. Examples are
`VIEW DATABASE STATE`, `UNMASK`, `REFERENCES` and ALTER on a role. The empty
schemas of fixed database roles are skipped, so `db_datareader` membership
still passes.

The verification reads catalog rows that a concurrent change of logins, users
or grants locks. On SQL Server it runs with `DEADLOCK_PRIORITY LOW`, so in a
deadlock the diagnostic request and not the other session's change is the
victim, and with the profile's lock timeout. A verification that was the
deadlock victim or timed out on a lock runs again, a few times at most and only
while the request's timeout leaves time for it. When the conflict lasts longer,
the request fails with the `deadlock` or `lock-timeout` classification at stage
`readonly-verification`; a failed verification never passes the principal.

On PostgreSQL the verification covers the connected role and every role it is
a member of, also a membership without inheritance, because the principal can
switch to such a role. It denies by default. Apart from the privileges the
server granted when it was initialized, the principal may hold SELECT on
tables, views, sequences and columns and nothing else. It may be a member of
`pg_read_all_data`, but of no other predefined role, such as `pg_monitor`,
`pg_read_all_stats`, `pg_read_all_settings`, `pg_signal_backend` or the
server-file roles. It may not own the database or any schema, relation,
function or type, may not execute a function outside `pg_catalog` and
`information_schema`, and may not execute a `pg_catalog` function that the
server does not grant to PUBLIC, such as `pg_stat_reset` or `pg_ls_logdir`.
The checks, in order, are `role-attribute-superuser`,
`role-attribute-createrole`, `role-attribute-createdb`,
`role-attribute-replication`, `role-attribute-bypassrls`, `database-owner`,
`predefined-role-membership`, `database-create`, `schema-create`,
`object-owner`, `relation-write` (also through views), `column-write`,
`sequence-usage`, `function-execute` and `catalog-function-execute`.

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

The SQL policy accepts one `SELECT` or read-only `WITH` statement without
comments. On PostgreSQL it refuses functions with session or server effects,
such as `pg_sleep` and its variants, the advisory-lock functions,
`pg_terminate_backend`, `pg_read_file`, `set_config` and `dblink`, and the
functions that run SQL text given as a string, such as `query_to_xml`,
`ts_stat` and `ts_rewrite`, with `function-not-allowed`, also
when the name is written as a double-quoted, schema-qualified or differently
cased identifier. A Unicode-escaped identifier (`U&"..."`) is refused with
`unicode-escaped-identifier-not-allowed`, and an escape string (`E'...'`) with
`escape-string-not-allowed`; pass such a value as a parameter. The tool
keeps `standard_conforming_strings` on in the diagnostic transaction, so a
backslash in an ordinary string literal stays an ordinary character. The policy
is a safeguard in front of the read-only principal, not a replacement for it.

Use `validate` only for CI, request-template checks or another deliberate dry
run without a database connection:

```text
newheap-db validate --request-file request.json
```

The validation response advertises the capability required by the executable
commands without using it during validation:

```json
{
  "requiredCapabilities": ["outbound-network"]
}
```

`query` and `schema` require outbound network access to the configured database.
When Codex invokes either command, its first attempt must use network-enabled or
escalated execution with a narrow reusable permission scoped only to
`newheap-db`, such as the `dotnet tool run newheap-db` prefix. Do not first probe
the restricted sandbox and do not broaden the permission to arbitrary `dotnet`
or shell commands.

For an interactive diagnostic, call `query` directly. It performs the same
request validation before it verifies the database principal and executes:

```text
newheap-db query --request-file request.json
```

For an explicitly requested Production diagnostic, retain the governed profile
and select the corresponding runtime configuration without adding a second
profile solely for the environment:

```text
newheap-db query --environment Production --request-file request.json
```

If the environment-specific configuration does not resolve the profile's
connection-string name, or if its principal is not read-only, the command fails
without executing the diagnostic query.

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

Pass the same explicit environment to schema discovery and the subsequent query:

```text
newheap-db schema --environment Production --search project --schema-name public --describe-if-single
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
allowlisted `classification`, `provider`, `providerCode`, `stage`, `transient`
and `retryHint` value. Stages distinguish `connection-open`,
`readonly-verification`, `query-execution` and `schema-execution`. For example,
PostgreSQL SQLSTATE `42P01` is returned as `object-not-found`, while SQL Server
error `207` is returned as `column-not-found`. Authentication and database
selection remain `authentication-failed` and `database-not-found`. A socket,
DNS or unknown provider error during connection open returns
`connection-failed`, preserves the first safe provider code when present, and
uses the `network-access-required` retry hint. That hint does not identify the
host policy responsible for the failure; a sandbox, firewall, DNS issue or
network ACL can produce the same response. A server certificate that the
diagnostic host does not trust returns the non-transient
`tls-certificate-untrusted` with the `certificate-authority-trust-required`
retry hint: trust the issuing certificate authority on the host instead of
retrying or turning certificate validation off. SQL Server keeps the SSPI code,
such as `-2146893019`, as `providerCode`. Raw provider messages, server and
object names, connection values and stack traces are never copied into the
error contract.

If the selected environment resolves the governed connection-string name to a
value that the configured provider cannot parse, the tool returns
`connection-configuration-invalid` with exit code `3`. It does not expose the
resolved value or continue to schema or query execution.

An unresolved `${Secrets:...}` reference returns `connection-string-unresolved`
with exit code `3` and names the process-local
`NewHeap__PlatformCommon__AppSecretsDirectoryPath` remediation. This supports a
Windows diagnostic against Production configuration that intentionally contains
a Linux secrets path without requiring a permanent application-settings change.

Exit codes are `0` for success, `2` for an invalid request, `3` for an invalid
profile, `4` for a policy rejection, `5` for a database failure, and `130` for
cancellation. Exit code `1` is reserved for an unexpected tool failure.

## Safety boundary

- Use a dedicated database identity that cannot write, execute application
  procedures, create objects or administer the server.
- Prefer a read replica or masked diagnostic views for production data.
- Grant access only to schemas, views and columns approved for the developers or
  agents that will consume the output.
- Keep the NewHeap platform's credential-bearing tables outside the SELECT
  allowlist: the refresh tokens (`NhUserAuthRefreshToken` by default),
  `AspNetUserTokens` (authenticator keys and recovery codes) and
  `NotificationDeliveries` (delivery payloads, including e-mailed sign-in and
  confirmation codes). Grant `AspNetUsers` only by column, without
  `PasswordHash` and `SecurityStamp`.
- On a database that holds those tables, grant SELECT per table, view or column.
  A database-wide read grant, such as `db_datareader`, database-level or
  schema-level SELECT, PostgreSQL `pg_read_all_data` or
  `GRANT SELECT ON ALL TABLES`, includes them. Principal verification accepts
  such grants because they are read-only, so it cannot catch this.
- Treat query results as potentially sensitive data even though configuration
  secrets are not displayed.
- Treat index metadata as query-design evidence, not as permission to run an
  otherwise broad scan or `EXPLAIN ANALYZE`.
- Do not use the tool for bulk exports, automated jobs or data repair.
