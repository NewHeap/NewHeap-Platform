# Run work in the background

Use a Hangfire job when a request should enqueue work and return immediately.
Add durable operations when users need progress, cancellation or resumable steps.

## Configure job storage

In an application using `NewHeap.Platform.AspNet.Common`, add `WithHangfire` to
the NewHeap registration chain. For PostgreSQL, reference `Hangfire.PostgreSql`
and import `Hangfire` and `Hangfire.PostgreSql`:

```csharp
.WithHangfire(options =>
{
    options.UsePostgreSqlStorage(storage => storage.UseNpgsqlConnection(connectionString));
})
```

For SQL Server, use `options.UseSqlServerStorage(connectionString)` instead.
`WithHangfire` registers the worker as well as storage. Use the same queue
configuration for the host that enqueues work and the host that processes it.
The [sample startup](../../examples/SampleProjectManagement/src/Back-end/Applications/SampleProjectManagement.Api/Program.cs)
shows both provider alternatives.

## Enqueue a job

Write a concrete job class and register it with DI:

```csharp
builder.Services.AddScoped<ProjectMaintenanceJob>();
```

The sample's [ProjectMaintenanceJob](../../examples/SampleProjectManagement/src/Back-end/Applications/SampleProjectManagement.Api/Jobs/ProjectMaintenanceJob.cs)
injects the DbContext and moves overdue active projects to On Hold. Its
`RecalculateOverdueAsync` method can then be queued from an application service:

```csharp
var jobId = NhHangfireUtil.BackgroundJob.Enqueue<ProjectMaintenanceJob>(
    job => job.RecalculateOverdueAsync());
```

Import `NewHeap.Platform.Common.Utilities` for `NhHangfireUtil`. The returned ID
identifies the queued job; it does not mean the job has finished. Make the work
safe to retry.

## Run it on a schedule

Use a stable ID to register or update a recurring job:

```csharp
NhHangfireUtil.RecurringJob.AddOrUpdate<ProjectMaintenanceJob>(
    "project-overdue-maintenance",
    job => job.RecalculateOverdueAsync(),
    "0 */6 * * *");
```

This queues maintenance every six hours. The
[operations service](../../examples/SampleProjectManagement/src/Back-end/Applications/SampleProjectManagement.Api/Services/OperationsSampleService.cs)
also demonstrates delayed jobs, email and persisted user notifications.

## Try it

Run the sample and use its API's `/scalar` page with a manager bearer token.
Call `POST /operations-samples/jobs/enqueue-overdue`. It returns `202` with a job
ID. Once the worker runs, overdue active projects move to On Hold.

## Add durable workflows when needed

- [Durable operations](../consumer-guide/background-processing.md) for nested progress, idempotency and concurrency limits.
- [Suspension and resumption](../consumer-guide/background-operation-suspension.md) for workflows waiting on an external decision.
- [Transactions](data-access.md#commit-a-business-operation) for publishing events atomically with database writes.
