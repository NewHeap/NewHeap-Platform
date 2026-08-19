---
id: nh-consumer-bootstrap-sequence
title: "Bootstrap an empty consumer before feature work"
area: consumer-bootstrap
reference: consumer-bootstrap
summary: "Turn a confirmed product scope into a deterministic, capability-sized NewHeap foundation, prove anonymous public package restore, and only then generate application features."
sample-cases: ["SPM-216", "SPM-217"]
public-symbols: ["NewHeapPlatformCommonConfigurator", "NewHeapPlatformAspNetCommonConfigurator", "NhCommonModule"]
skills: ["newheap-consumer-development"]
providers: ["sql-server", "postgresql"]
risk: high
---
## Preferred approach

Install the versioned NewHeap plugin or repository-pinned skill before asking an agent to bootstrap an empty repository. Complete the product-language scope gate, then run `scripts/bootstrap-newheap-consumer.mjs` with the repository root, application name, explicit `service`, `api` or `management-portal` profile, and `none`, `postgresql` or `sqlserver` persistence decision. Add `--authentication` for a non-portal API that needs protected access. The bootstrap records the decisions in `newheap-consumer.json` and places the solution and both central props files in `src/Back-end`.

Generate only the selected application host and its compact, complete foundation. Every profile keeps the standard application, library, test and orchestration seams so later APIs or services fit without a repository restructure. A backend-only profile writes `src/Front-end/.gitkeep` but no Angular or npm files. Only the `management-portal` profile creates the Angular workspace in `src/Front-end`, sets `newProjectRoot` to `projects`, and installs the compatible NewHeap npm package.

Treat successful anonymous `dotnet restore`, the profile-relevant npm installation, and `inspect-newheap-consumer.mjs --mode foundation` as a hard gate. Stop when a NewHeap package resolves through a private feed or requires a token; remove stale repository, user-level, or CI source overrides and rerun the idempotent bootstrap against nuget.org and npmjs.org. Only after this gate may an agent use the linked executable sample evidence to generate the confirmed identity, domain, API, service, frontend or optional-infrastructure capabilities. Run `--mode validate` after implementation and resolve every profile-relevant error before handoff.

The bootstrap refuses to overwrite a different existing file. It may be rerun when its managed files are unchanged. Credentials never belong in the repository, package URL, manifest, lockfile or generated instructions.

## Avoid

- Running the bootstrap without an explicitly confirmed profile and persistence decision.
- Starting domain CRUD, authentication or frontend screens while their required NewHeap package registry is unavailable.
- Putting the `.slnx`, `Directory.Build.props`, `Directory.Packages.props` or Angular workspace in the repository root.
- Installing Angular or npm packages for a backend-only profile.
- Copying a token into `.npmrc`, `nuget.config`, a command committed to source control, or a URL.
- Claiming setup is complete after file generation without a package restore, npm installation and final validation.
- Replacing a conflicting existing file through a force flag during bootstrap.

## Verification

Run the bootstrap without `--skip-install`, then run `inspect-newheap-consumer.mjs <consumer-root> --mode foundation`. Confirm the NewHeap NuGet restore produced the selected application host's `project.assets.json` and the `.slnx` is under `src/Back-end`. For a backend-only profile, confirm only the frontend placeholder exists. For a management portal, also confirm the frontend package exists under `node_modules` and Angular workspace files are under `src/Front-end`. Run the profile's normal build and `--mode validate` as the final structural and integration audit.
