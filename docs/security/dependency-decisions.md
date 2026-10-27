# Dependency security decisions

## NewHeap mapping recursion boundary

NewHeap no longer depends on the AutoMapper package, so the former AutoMapper 14 recursion advisory and its NuGet audit suppression have been removed. `NewHeap.Platform.Mapping` provides only the profile and runtime mapping surface exercised by NewHeap and its executable sample.

Every NewHeap type map has a default maximum depth of 64, regardless of whether it is registered through `NewHeapAspNetCommonOptionsBuilder.ConfigureAutoMapper` or an independent `MapperConfiguration`. An explicit `MaxDepth` overrides that default. Focused mapping tests prove both the AutoMapper 14-compatible explicit-depth boundary and the 64-level default on recursive graphs, while nested-object and collection tests protect mapping into existing destinations.

## Testcontainers SSH dependency

The shared relational test infrastructure `NewHeap.Platform.Tests.Relational` and every test project that uses Testcontainers, directly or through it, pin the transitive `SSH.NET` dependency to 2026.0.0 because Testcontainers 4.13.0 declares the vulnerable 2025.1.0 version. Each project has a pin of its own, because a private reference does not flow to the projects that reference it. The references are test-only and private so they cannot become a dependency of any NewHeap release package. Remove the direct pins when a future Testcontainers version resolves to an equally new or newer patched version.

## Angular 20 build-only image parser advisory

Angular build tooling 20.3.34 currently resolves `less` to an `image-size` release affected by GHSA-w3rx-r6r6-pgpr and GHSA-5p2g-fcmc-qvqq. No patched `image-size` release is available as of 2026-08-19, and npm's proposed fix is a major Angular toolchain upgrade that is outside this release-preparation change.

This dependency is development-only and is absent from the published NewHeap npm tarballs. Pull-request validation has read-only repository permissions, release publication only runs from the validated `main` commit, and the build does not accept external image inputs. CI logs the full audit and fails on every critical advisory; it separately fails on high or critical runtime advisories. Reassess this exception on every Angular toolchain update and remove it as soon as Angular resolves to a patched parser.

## Document reading in the sample only

SampleProjectManagement's example reading tool, `OfficeDocumentReader`, turns Word and Excel documents into text for the assistant with `DocumentFormat.OpenXml` 3.5.1 (MIT) and its `DocumentFormat.OpenXml.Framework` dependency (MIT). Only the sample's API references it; the NewHeap packages deliberately carry no document library, so applications copy the example with a library of their choice or use a document-reading MCP server.

The reader runs in the application's process on files whose size the assistant's limits already bound. `OpenSettings.MaxCharactersInPart` bounds what one package part may unpack to, but it bounds no total: cells can name one shared string again and again, and several sheets can name one worksheet, so the text can be far longer than the file. The reader therefore also bounds the text it makes, value by value, by the turn's budget for it (`NhAssistantFileReadRequest.MaxTextCharacters`, refused with `assistant-file-token-limit`) and by its own `MaxCharactersInText`, reads every worksheet once and checks the turn's cancellation at every row and paragraph. A damaged or oversized document fails the file instead of returning partial text. Review the pin with the sample's other dependencies; a replacement must keep a permissive licence.

## Audit policy

Do not suppress high or critical dependency advisories merely to make release output green. A suppression requires a documented, tested mitigation and an expiry/review condition. Public release validation must run NuGet and npm audits and report infrastructure-limited checks explicitly.
