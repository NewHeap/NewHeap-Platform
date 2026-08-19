# Dependency security decisions

## AutoMapper 14 recursion advisory

NewHeap temporarily remains on the official `AutoMapper` 14.0.0 package. This preserves the existing dependency and API while the project decides between a licensed official upgrade and another mapping strategy.

This version is affected by [GHSA-rvv3-g6hj-g44x](https://github.com/advisories/GHSA-rvv3-g6hj-g44x). Official patched releases start at 15.1.1 and require an AutoMapper license as well as version 15 API adjustments. NewHeap applies AutoMapper's documented pre-patch mitigation instead: after the built-in and consumer profiles have been registered, every type map without an explicit depth limit receives `MaxDepth(64)`. The built-in NewHeap profile applies the same convention when it is used independently.

`AutoMapperSecurityConfigurationTests` constructs circular consumer mappings, enumerates the resulting type-map graph, requires a non-zero maximum depth for every circular map, verifies the default of 64 for otherwise unbounded maps, and proves that an explicit consumer limit is not changed. The repository and executable sample therefore suppress only this exact advisory while continuing to fail on every other high or critical NuGet advisory.

The mitigation boundary is the mapper configuration managed by NewHeap. A consumer that constructs an independent `MapperConfiguration` outside `NewHeapAspNetCommonOptionsBuilder.ConfigureAutoMapper` must apply and test its own depth guard. Reassess this exception on every AutoMapper dependency change, when the mapping strategy changes, or no later than 2027-02-19.

## Testcontainers SSH dependency

`NewHeap.Platform.AspNet.Common.Tests` pins its transitive `SSH.NET` dependency to 2026.0.0 because Testcontainers 4.13.0 declares the vulnerable 2025.1.0 version. The reference is test-only and private so it cannot become a dependency of any NewHeap release package. Remove the direct pin when a future Testcontainers version resolves to an equally new or newer patched version.

## Angular 20 build-only image parser advisory

Angular build tooling 20.3.34 currently resolves `less` to an `image-size` release affected by GHSA-w3rx-r6r6-pgpr and GHSA-5p2g-fcmc-qvqq. No patched `image-size` release is available as of 2026-08-19, and npm's proposed fix is a major Angular toolchain upgrade that is outside this release-preparation change.

This dependency is development-only and is absent from the published NewHeap npm tarballs. Pull-request validation has read-only repository permissions, release publication only runs from the validated `main` commit, and the build does not accept external image inputs. CI logs the full audit and fails on every critical advisory; it separately fails on high or critical runtime advisories. Reassess this exception on every Angular toolchain update and remove it as soon as Angular resolves to a patched parser.

## Audit policy

Do not suppress high or critical dependency advisories merely to make release output green. A suppression requires a documented, tested mitigation and an expiry/review condition. Public release validation must run NuGet and npm audits and report infrastructure-limited checks explicitly.
