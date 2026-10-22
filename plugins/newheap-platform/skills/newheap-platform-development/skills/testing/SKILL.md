---
name: newheap-testing
description: Set up or review tests in NewHeap consumer applications, including reusable test-helper packages, consumer test-project boundaries and relational integration evidence.
---

# NewHeap consumer testing

Read [testing](references/testing.md) before selecting NewHeap test packages or organizing test projects.

Consumer tests may use the packable `NewHeap.Platform.*.Test` helper packages. NewHeap's own regression tests belong only in non-packable plural `*.Tests` projects and are not consumer dependencies.

Use InMemory helpers for isolated unit tests only. Translation, constraints, transactions, migrations and raw SQL require real relational-provider evidence.

Keep those relational tests fast: one database container per test assembly, a database of its own per test or host copied from a template migrated once, and a registered test `TimeProvider` moved forward instead of waiting. Follow "Keep consumer tests fast" in [testing](references/testing.md).

Run the smallest focused tests that prove the requested behavior and report which reusable helpers and real providers were exercised.
