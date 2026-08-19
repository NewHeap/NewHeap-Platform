# Contributing to NewHeap Platform

Thank you for helping improve NewHeap Platform. Contributions should keep the
reusable libraries, executable SampleProjectManagement evidence, tests, and
generated consumer guidance aligned.

## Before starting

1. Search existing issues and sample cases before creating a duplicate.
2. Open an issue before substantial public API, behavior, provider, dependency,
   security, or release changes.
3. Never include customer code, credentials, personal data, private domains,
   certificates, or other confidential material.
4. Follow the repository and subtree `AGENTS.md` instructions.

## Development workflow

1. Create a focused branch from the current default branch.
2. Add or update focused tests for behavioral changes.
3. Update the executable sample, case registry, and guidance rule when a public
   library contract or recommended usage pattern changes.
4. Generate derived guidance instead of editing generated files directly.
5. Run the smallest relevant checks followed by the applicable repository
   verification checklist.
6. Describe the compatibility and provider impact in the pull request.

Database migrations must be generated with the EF CLI. Do not hand-edit an
existing migration or model snapshot. Provider-neutral behavior must be checked
against both SQL Server and PostgreSQL where relational semantics are involved.

## Pull requests

Keep pull requests reviewable and limited to one coherent change. Include:

- the problem and intended behavior;
- verification commands and results;
- affected packages and sample cases;
- compatibility or migration notes;
- screenshots for user-interface changes;
- any provider or environment gap that remains unverified.

Maintainers may ask for changes, split an oversized pull request, or close work
that cannot be distributed safely or does not fit the project direction.

## Contribution licensing

NewHeap Platform is licensed under Apache-2.0. Under Section 5 of that license,
unless you explicitly state otherwise, a contribution intentionally submitted
for inclusion in the project is provided under Apache-2.0 without additional
terms. You represent that you have the right to submit the contribution and
that it does not contain material you are not authorized to disclose.

Mark material clearly as `Not a Contribution` when you are sharing it only for
discussion and do not intend it to be incorporated.
