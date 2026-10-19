# SampleProjectManagement

SampleProjectManagement is the executable consumer reference for NewHeap
Platform. The project is deliberately structured as a standalone product
repository: source code lives under `src`, maintenance instructions under
`skills` and `docs`, and the root contains only repository-level configuration
and verification entry points.

## Repository structure

```text
SampleProjectManagement/
|-- src/
|   |-- Back-end/
|   |   |-- SampleProjectManagement.slnx
|   |   |-- Directory.Build.props
|   |   |-- Directory.Packages.props
|   |   |-- Applications/     API and HTTP composition
|   |   |-- Libraries/        domain logic, DAL, and sample migrations
|   |   |-- Orchestration/    Aspire AppHost and service defaults
|   |   `-- Tests/            executable contract and behavior tests
|   `-- Front-end/            management, workspace, and shared Angular code
|-- docs/                     canonical case catalog and generated guidance
|-- skills/                   sample-specific workflow for coding agents
|-- tools/                    structure and contract validation
`-- AGENTS.md                 authoritative local AI instructions
```

The main implementation paths are:

- `src/Back-end/Applications/SampleProjectManagement.Api`: controllers, Scalar and OpenAPI, and composition;
- `src/Back-end/Libraries/SampleProjectManagement.Core`: models, services, and mappings;
- `src/Back-end/Libraries/SampleProjectManagement.DAL`: entities, DbContext, and consumer-owned migrations;
- `src/Back-end/Orchestration/SampleProjectManagement.AppHost`: PostgreSQL, RabbitMQ, the API, the proxy demo, and both frontends;
- `src/Front-end/projects/management`: management interface and interactive sample catalog;
- `src/Front-end/projects/workspace`: day-to-day project workspace;
- `src/Front-end/projects/sample-project-management-common`: genuinely shared Angular code.

## Run the sample

Set `SampleProjectManagement.AppHost` as the startup project and start Aspire.
The AppHost starts PostgreSQL, RabbitMQ, the API, the proxy demo, and both Angular applications.
Scalar is available from the API at `/scalar`; the OpenAPI document is at
`/openapi/v1.json`.

Open the `sample-project-management-proxy` resource URL in the Aspire dashboard
to reach `/newheap-proxy`. Sign in with `administrator` / `NewHeap123!`.
Aspire assigns the port; the demo uses its own persistent SQLite file and has no
dependency on the API or database containers. See [proxy administration](docs/proxy-administration.md).

The PostgreSQL container deliberately has no data volume. The API uses
`MigrateAsync` to create the database and apply migrations. Only in Development
may `Database:ResetOnStartup` remove the temporary database.

The Development API creates five local accounts. They all use the password
`Sample123!`:

| Account | Scope |
|---|---|
| `sample@example.test` | application-wide manager rights and Sample North project rights plus the assistant and its administration |
| `viewer@example.test` | application-wide read-only access |
| `division-editor@example.test` | project rights within the active division |
| `project-editor@example.test` | confidential access to Authorization Alpha only |
| `project-lead@example.test` | application-wide and Sample North project rights plus the assistant; approver of Authorization Alpha |

## Secrets

Set `SAMPLE_PROJECT_MANAGEMENT_APP_SECRETS_ROOT` to a local directory, for
example `C:\NewHeapAppSecrets`. Then copy
[secrets.template.json](secrets.template.json) to
`C:\NewHeapAppSecrets\SampleProjectManagement\secrets.json`. Configure at least
the RabbitMQ values and
`NewHeap.PlatformAspNetCommon.Authorization.JWT.Token.Key`. Generate a unique
64-byte Base64 JWT key for every installation; the API refuses to start without a key of
at least 32 bytes, and warns at startup about every other `${Secrets:…}` reference whose
secret is missing. Set
`SampleProjectManagement.ReviewBoard.WebhookSecret` (at least 32 characters) to
let the review board call the intake reviewer's webhook trigger; without it the
webhook refuses every call.

## A real model for the assistant

By default a deterministic model drives the assistant and its agents. It
recognizes a few fixed phrasings to demonstrate the governed flows, and every
test uses it. To try agents and skills whose model decides, with instructions of
your own, connect an OpenAI-compatible chat model through
`SampleProjectManagement.Assistant.Model` in the local `secrets.json`:

| Key | Value |
|---|---|
| `Provider` | empty for the deterministic model, or `openai` for an OpenAI-compatible endpoint |
| `ApiKey` | the endpoint's API key; required with `openai` |
| `Model` | the model or deployment name; required with `openai` |
| `Endpoint` | optional; empty for OpenAI itself, or the https URL of a compatible endpoint such as an Azure OpenAI `/openai/v1/` endpoint; plain http only for a loopback server in Development |
| `Region` | optional execution region the assistant sends; `external` by default |
| `MaxInputTokens`, `MaxOutputTokens`, `TimeoutSeconds` | optional limits per model call; 32000, 2000 and 60 by default |

The API validates these settings at startup and stops with a message that names
the missing or invalid setting; it never logs the key. Environment variables such
as `SampleProjectManagement__Assistant__Model__ApiKey` override the secrets file.
The model then serves the project assistant, every agent and skill whose model
decides, and the planner's triage; rule-based skills still call no model. Leave
`Provider` empty to return to the deterministic model.

With a real model, messages, the agents' instructions and the project data that
tools read leave your machine for the endpoint. Use test data only.

## Read-only database diagnostics

The backend contains a checked-in
`.newheap/database-read.json` profile and a parameterized
`Tooling/DatabaseRead/requests/project-by-id.json` request. For direct agent
diagnostics, the profile, `Tooling/DatabaseRead/README.md`, and checked-in request
files are the routing and JSON contract; application code and package internals
are not discovery prerequisites. From the sample root, one bounded tracked-file
search finds the nested catalog under `src/Back-end`; that catalog root, not an
ancestor tool-manifest directory, becomes the diagnostic working directory. Its
only `sample-development` profile governs provider, configuration path,
connection-string name and ceilings; Development is its default runtime
environment. If the user explicitly requests Staging or Production, pass that
value with `--environment` to schema and query instead of requiring another
profile solely for the environment. Let `newheap-db` resolve the governed
connection-string name. Create a dedicated
PostgreSQL login that has only `CONNECT`, schema `USAGE`, and `SELECT` on the
approved diagnostic tables or views. Put that login's connection string in
`ConnectionStrings.NewHeapDiagnosticsReadOnly` in the local `secrets.json`.
Never reuse the application owner credential.

Restore the repository-pinned `NewHeap.Platform.DatabaseRead.Tool`, then run
this from `src/Back-end` in PowerShell:

```powershell
dotnet tool restore
dotnet tool run newheap-db schema --search Projects --schema-name public --describe-if-single
dotnet tool run newheap-db query --request-file Tooling/DatabaseRead/requests/project-by-id.json
```

For an explicitly requested Production diagnostic, use the same profile with
`--environment Production` on both execution commands. The tool resolves the
environment-specific configuration and still refuses any principal with write,
DDL or elevated permissions.

The commands return one JSON document on standard output and also retain standard
input as a supported request transport. The only profile is selected automatically.
Both execution commands validate internally and refuse a principal when write,
DDL, or elevated permissions are detected; do not add a separate `validate` call
to an interactive diagnostic. `validate` remains available for CI and dry-run
request checks without opening the database.

## Maintain the samples

The canonical machine-readable source is
[docs/cases/sample-case-registry.json](docs/cases/sample-case-registry.json).
The plan, status, Angular catalog, central consumer guide, and consumer skill are
generated from it and from `guidance/rules`. Do not edit generated files by hand.

Run these commands from this directory:

```text
npm run generate:samples
npm run verify:samples
npm run test:backend
npm run build:management
npm run build:workspace
```

The local [maintenance skill](skills/sample-project-management-development/SKILL.md)
describes the complete workflow for agents. `npm run validate:structure` ensures
that source code, solution paths, evidence, and agent files remain in this layout.

## Architecture rules

- Keep persistence in DAL, concrete domain behavior in Core, and HTTP composition in the API.
- Let mutation services own the outer transaction scope and commit exactly once.
- Keep controllers thin and give every action complete Scalar, OpenAPI, and authorization metadata.
- Keep consumer entities and migrations out of reusable NewHeap libraries.
- Demonstrate SQL Server and PostgreSQL separately when behavior is provider-dependent.
- In Angular, use the NewHeap lifecycle extension points and recommended fluent APIs.
- Make every registered sample executable, explained, and proportionally tested.
- Keep documentation, AI instructions, and case metadata in English. Every UI must include complete English translations; additional languages may remain.

See [docs/sample-catalog.md](docs/sample-catalog.md) for the narrative guide,
[docs/library-sample-plan.md](docs/library-sample-plan.md) for every case, and
[docs/newheap-surface-to-case-matrix.md](docs/newheap-surface-to-case-matrix.md)
for the concept-to-case mapping.
