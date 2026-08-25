# Release checklist

Run the commands applicable to the changed surface and record anything not run.

```text
npm run guidance:generate
npm run guidance:snapshot
npm run guidance:validate
npm run skills:eval
npm run plugin:validate
npm run release:test
dotnet test src/Back-end/NewHeap.Platform.sln
dotnet test examples/SampleProjectManagement/src/Back-end/SampleProjectManagement.slnx
cd examples/SampleProjectManagement/src/Front-end
npm run generate:samples
npm run verify:samples
npm run build:management
npm run build:workspace
```

For EF, migrations, raw SQL, provider translation, constraints or transactions, run the relevant integration suite against real SQL Server and PostgreSQL. InMemory is additional fast feedback only.

For Angular visual changes inspect login, shell, navigation and changed surfaces at desktop and narrow mobile widths. Check console errors, focus, reduced motion, loading/empty/error/disabled states and horizontal overflow.

Before release, run change-impact validation against the target branch and confirm that the public API snapshot, sample registry, generated plan/status, consumer guide, skill references and skill manifest are clean after regeneration. Feature branches must retain the last published guidance and plugin versions; never pre-bump them to satisfy change-impact validation.

Install the generated consumer skill suite into a temporary consumer root, run the installer again with `--check`, and validate the packaged `plugins/newheap-platform` manifest. The plugin version must match `guidance/version.json`; `distribution.json` records the focused skill list, centralized immutable-evidence source and compatible library-package versions separately.

For a package release, also dry-run the selected unit with `node tools/release/package-release.mjs --component <unit> --dry-run`. Start `Prepare release` on `main`; do not edit versions or run the reusable publisher directly. Confirm that the generated branch contains exactly one release commit on the recorded main base, the release contract passes in explicit release mode, the workflow fast-forwards main to that exact commit, and publication checks out the same SHA. If main advances before the update, start preparation again. Confirm that the OIDC registry job succeeds, the separate finalization job observes every exact public package version anonymously, and the automatic run creates the expected component tag, immutable GitHub Release, checksums and every package listed in `release/manifest.json`. Use `Finalize pending release` only to recover complete current-version drafts after packages have already been pushed. Do not restore or invoke the former PowerShell publish scripts.

For an ordinary package release, rely on the established public repository and trusted-publisher baseline; do not repeat registry bootstrap work. Recheck policy identities when a workflow identity changes or authentication fails. When a release unit gains a package name that has never been public, verify its ownership or availability and complete the exceptional onboarding procedure before release. A compatible package addition may use patch or minor; reserve major for a breaking contract. Never attach new package membership to an already-published unit version. After the push, require anonymous exact-version checks for every target. For an individual NuGet or npm unit, confirm the successful top-level run queues the guarded plugin patch follow-up; plugin and `all` runs must not recurse. Never add a long-lived publication token as the normal release path.
