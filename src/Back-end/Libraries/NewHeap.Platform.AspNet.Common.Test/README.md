# NewHeap.Platform.AspNet.Common.Test

Reusable ASP.NET and EF Core test helpers for applications that consume
`NewHeap.Platform.AspNet.Common`. The package provides an in-memory DbContext
test context and automatic repository registration for consumer-owned DbSets.

This is a support library, not the location of NewHeap Platform's own regression
tests. Library self-tests live in the non-packable
`src/Back-end/Tests/NewHeap.Platform.AspNet.Common.Tests` project. EF Core
InMemory is suitable for isolated service tests, but does not replace SQL Server
or PostgreSQL verification for relational behavior.
