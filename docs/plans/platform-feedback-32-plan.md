# Platform Feedback on 32.0.0 Plan

A consuming application upgraded from NewHeap 8.0.0 to 32.0.0 in one step. The code compiled at once. The real
problems were behavior that changed silently, with nothing in the log or the console to say why:
- push notifications stopped for people the principal factory could not rebuild (the application uses actor
  ids of its own);
- toasts disappeared because the application had no toast container;
- a regional language such as `nl-NL` showed no Dutch texts of the application;
- the application had to build its own idempotent SQL script for the assistant schema and gather every
  behavior change between the two versions by hand.

Several other points of the report are intended and documented, and need no change:
- Escape closes the topmost modal (v19.0.0; content may refuse with `onModalEscape`);
- every segment of a filter path is checked against the view model (SPM-034);
- division checks ask for the division membership claim;
- open proposals are refused after an upgrade that changes a tool's contract.

The report's own plan asks for an automatic bridge from application events to agent triggers. NewHeap keeps
triggers explicit, as decided earlier, so it is not taken up.

It is a plan, not usage documentation. Once it is built, the guidance rules, the release notes and the sample
cases are authoritative.

## Status

Decided by Lars on 2026-11-03, all as advised. Built in train 36 by lane FX, next to the rule scripts of
`rule-scripts-and-agent-builder-plan.md`, and verified with it. The choices of the lane are FX1–FX6, for Lars to
review.

## Decisions

| # | Decision |
|---|---|
| FB88 | **A general regional fallback for the application's texts.** `NhTranslateBrowserLoader` loads `nl.json` when `nl-NL.json` (or `nl_BE.json`) does not exist, so a regional language gets the texts of its base language, as the libraries' own texts already do. A regional file that exists is used as it is. |
| FB89 | **A warning when a notification skips a person the principal factory cannot rebuild.** The warning is content-free and comes at most once per actor per hour. It hints that actor ids of the application's own need an `INhAiPrincipalFactory` of its own. The guidance says the same. |
| FB90 | **A development warning for a toast without a container.** In development mode, one console warning when a toast is shown while no toast container is on the page, naming what fixes it. The guidance gains a short migration paragraph from `ngx-toastr` to `NhToastrService`. |
| FB91 | **One overview of the breaking changes between two versions.** A command gathers every `Breaking change | Required action` row of the release notes between two versions, per package, with its required action. |
| FB92 | **An idempotent SQL script per release for the assistant schema**, for SQL Server and PostgreSQL, generated with the EF tools from an empty database and published with the release, for applications that run migrations themselves. |

## Rules for all of it

- Compatible and opt-in where behavior changes; a breaking change gets a `Breaking change | Required action` row.
- Tests for each change, guidance where usage changes, release notes, the public API snapshot, and the sample
  where the usage shows (the regional fallback in the sample's front end).
- Nothing names the consuming application.

## Lane choices

| # | Choice | Status |
|---|---|---|
| FX1 | FB88: only a `404` counts as a missing regional file. A host that answers a missing JSON file with its `index.html` and `200` still fails as before; the guidance asks to answer `404` under the translation prefix. The base language is the first subtag in lower case (`nl` for `NL-be`), as `nhCommonText` reads it, and transfer state is read per language: the regional key first, then the base key. | for Lars to review |
| FX2 | FB89: the warning carries no actor id and no hash: the assistant's logs name no people (the alert dispatcher writes "one person"), so it is one fixed, content-free text naming `INhAiPrincipalFactory`. It comes once per person per hour on the application's clock from every check of `NhAssistantPrincipalRecipientRules` that skips a person the factory rebuilds nobody for (live updates, Web Push, in-app and agent notifications, alerts, alert reads), never for a factory that cannot tell. The memo remembers at most 1024 people; while that many were warned about within the hour, nobody new is. | for Lars to review |
| FX3 | FB90: the container registers itself with an internal `NhToastrContainers` service, and the check runs after the next render (`afterNextRender`), so a container that the same render creates counts. One warning per application. Development mode comes from an internal token (`isDevMode()` by default) that only the spec overrides; neither is exported. | for Lars to review |
| FX4 | FB91: `release:breaking` also lists the `Behavior change \| Required action` rows of three older notes, not `Adoption note` rows. A section belongs to the unit of its heading's packages in the current manifest. Without `--unit` a range that holds rows of several units is refused instead of mixed; `--to` is optional, `--next` adds `v-next.md` last. Its tests run in `release:test` (`test:affected` runs no tooling tests). | for Lars to review |
| FX5 | FB92: the scripts are `newheap-assistant-schema-<sqlserver\|postgresql>-<version>.sql` artifacts of `nuget-common`, also in the preview artifacts, covered by `SHA256SUMS` and required by the artifact validation, for the default `nhai` schema. The design-time factories now register the providers' SQL generators: a script used to keep the `{nhai}` placeholder of `McpReaders`, which no database accepts. They also take `-- --schema <name>`, which `release:sql-scripts -- --schema` passes, for another schema. The workflows install `dotnet-ef` at the repository's EF Core version from nuget.org into a tool path: a build tool, no package reference or tool manifest. | for Lars to review |
| FX6 | Sample: the platform playground loads `nl-BE` through `nhTranslateBrowserLoaderFactory` and shows the Dutch title (the sample ships `en.json` and `nl.json`). FB89, FB90 and FB92 change logs, the console and release artifacts, not usage, so the sample is unchanged; cases SPM-264, SPM-218 and SPM-247 name the new evidence. FB91's note is under `newheap-platform`, whose consumer skill names the command, because repository tooling has no release unit. | for Lars to review |
