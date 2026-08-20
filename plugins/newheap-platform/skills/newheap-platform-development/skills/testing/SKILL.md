---
name: newheap-testing
description: Set up or review tests in NewHeap consumer applications, including reusable test-helper packages, consumer test-project boundaries and relational integration evidence.
---

# NewHeap consumer testing

Read [testing](references/testing.md) before selecting NewHeap test packages or organizing test projects.

Consumer tests may use the packable `NewHeap.Platform.*.Test` helper packages. NewHeap's own regression tests belong only in non-packable plural `*.Tests` projects and are not consumer dependencies.

Use InMemory helpers for isolated unit tests only. Translation, constraints, transactions, migrations and raw SQL require real relational-provider evidence.

Run the smallest focused tests that prove the requested behavior and report which reusable helpers and real providers were exercised.
