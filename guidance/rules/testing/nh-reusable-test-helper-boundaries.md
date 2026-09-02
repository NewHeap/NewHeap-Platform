---
id: nh-reusable-test-helper-boundaries
title: "Separate reusable test helpers from library tests"
area: backend
reference: testing
summary: "Use the packable NewHeap .Test projects only as reusable test support for consumers, and keep NewHeap library regression tests in separate non-packable .Tests projects."
sample-cases: ["SPM-173", "SPM-174", "SPM-175", "SPM-176"]
public-symbols: ["NhTestingContext", "NhDbContextTestingContext", "TaskResultExtensions", "TestExtensions"]
skills: ["newheap-testing"]
providers: ["provider-neutral"]
risk: medium
---
## Preferred approach

Reference `NewHeap.Platform.Common.Test` from a consumer test project for DI
contexts, `TaskResult` assertions, and NSubstitute predicate helpers. Add
`NewHeap.Platform.AspNet.Common.Test` for the in-memory DbContext test context
and automatic repository registration. The SampleProjectManagement tests for
SPM-173 through SPM-176 are the executable examples.

Keep the test framework version consumer-owned. The reusable packages do not
reference or flow xUnit compile/runtime assets into consumer test projects, so
a consumer can select its supported xUnit version without duplicate test
attributes. `NhTestingContextFixture<TContext>` exposes lifecycle methods but
does not implement a version-specific xUnit interface; consumers that need an
xUnit class fixture add a small version-specific adapter in their own test
project, as demonstrated by SPM-173. Resolve contexts and application services
from the validated DI provider instead of manually constructing NewHeap
repositories.

Keep the packable `NewHeap.Platform.*.Test` projects limited to public, reusable
fixtures, contexts, factories, and assertions. Put NewHeap's own `[Fact]` and
`[Theory]` regression tests under `src/Back-end/Tests` in a `*.Tests` project
with `IsPackable=false` and `IsTestProject=true`. CI still runs the library
tests, but they never become part of a consumer package.

## Avoid

- Adding library regression tests to a packable `.Test` helper project.
- Referencing an internal `NewHeap.Platform.*.Tests` project from a consumer.
- Relying on a transitive test-framework dependency instead of declaring the
  consumer's chosen runner and test framework explicitly.
- Using EF Core InMemory as evidence for query translation, migrations,
  relational constraints, transactions, or provider-specific behavior.
- Publishing test-runner, coverage, or browser dependencies when no public
  helper API depends on them.

## Verification

Run the relevant non-packable `*.Tests` project and the consumer test project.
Verify that the helper assemblies contain no `[Fact]` or `[Theory]` methods and
that only the `.Test` helper projects appear as NuGet packages in the release
manifest. For relational behavior, run the required SQL Server and PostgreSQL
tests in addition to any in-memory unit tests.
