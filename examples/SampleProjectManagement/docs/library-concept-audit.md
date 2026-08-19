# NewHeap library concept audit

This audit complements the [library sample plan](library-sample-plan.md) and
assesses only behavior from the public NewHeap API. Every conclusion points to a
self-contained SampleProjectManagement case and a focused regression test.

| Library behavior | Sample coverage | Decision |
|---|---|---|
| Ordered EF query in asynchronous batches | SPM-161, SPM-201, SPM-202 | The UI and API display batches; tests guard size and cancellation. |
| Outer transaction owned by the application service | SPM-191–194, SPM-196–198 | Services own the scope; nested library calls do not commit independently. |
| Publication in the same SQL scope as the write | SPM-193, SPM-195, SPM-199, SPM-200 | CAP outbox publication happens before one commit; rollback removes both the write and event. |
| Scheduled and repeatable jobs with locking | SPM-081–083, SPM-203–204 | Hangfire registration and keyed locks are live; wrappers have separate contract tests. |
| Retries around external systems | ServiceDefaults and SPM-079 | Retries wrap concrete external calls, not a database transaction. |

The regression tests are located in
`src/Back-end/Tests/SampleProjectManagement.Core.Tests/EfCoreExtensionSamplesTests.cs` and
`src/Back-end/Tests/SampleProjectManagement.Core.Tests/ServerHelperExtensionSamplesTests.cs`.
