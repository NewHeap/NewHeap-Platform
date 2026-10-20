---
id: nh-platform-modules-without-identity
title: "Host background operations and notifications without NewHeap Identity"
area: operations
reference: platform-modules-without-identity
summary: "Map background operations and notifications into the application's own EF context in their own schemas, register them without the platform builder, and answer the platform's actor, division, directory, notification and retention questions through its interfaces."
sample-cases: ["SPM-083", "SPM-086", "SPM-281", "SPM-302"]
public-symbols: ["NhPlatformModuleModelBuilderExtensions", "NhPlatformModuleModelOptions", "NhPlatformModuleServiceCollectionExtensions", "NhPlatformModuleEndpointRouteBuilderExtensions", "NhActorRef", "NhActorKinds", "NhActorContext", "INhActorContextAccessor", "INhDivisionAccess", "INhActorDirectory", "NhActorInfo", "INhUserNotificationSink", "NhUserNotificationRequest", "NhUserNotificationReceipt", "NhUserNotificationLevel", "INhRetention", "INhRetentionParticipant", "NhRetention", "NhActorServiceCollectionExtensions", "INhActorSearch", "NhActorSearchRequest", "INhDivisionDirectory", "NhDivisionInfo"]
skills: ["newheap-background-processing"]
providers: ["sql-server", "postgresql"]
risk: high
---
## Preferred approach

Background operations, durable AI runs, agent runs and notifications do not need
`NhIdentityDbContext`. An application with its own EF context and its own users maps the
modules into that context and registers them without the NewHeap platform builder:

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.AddNewHeapBackgroundOperations();   // schema nhops
    modelBuilder.AddNewHeapNotifications();          // schema nhnotify
}

services.AddDbContext<PortalDbContext>(options => options.UseNewHeapPostgreSql(connectionString));
services.AddNewHeapNotifications<PortalDbContext>();
services.AddNewHeapHangfireProcessing(hangfire => hangfire.UsePostgreSqlStorage(...));
services.AddNewHeapBackgroundOperations<PortalDbContext>(operations => operations
    .AddAssistantAgentRuns());
services.AddNewHeapActorDirectory<PortalDirectory>();
services.AddNewHeapAuthenticationForwarding("portal");
```

Configure the context with `UseNewHeapSqlServer` or `UseNewHeapPostgreSql` so transaction locks
work, and generate the application's own migration after adding the model extensions. The
modules live in the application's context, so an enqueue or notification inside the
application's transaction commits or rolls back with it. The `Schema` option moves a module to
another schema; `Schema = null` keeps the context's default schema, which is what
`NhIdentityDbContext` uses, so applications on NewHeap Identity keep their model unchanged.
The extensions add no foreign keys to the application's users or tenants; add them after the
call when the application wants them.

The platform asks the application what it knows about people through interfaces in
`NewHeap.Platform.Common`:

- `INhActorContextAccessor`: the current actor (`NhActorRef`, a user id in `D` format) and the
  active division. The default reads the `NameIdentifier` claim and has no division.
- `INhDivisionAccess`: whether an actor may work in a division. Division is the platform's
  tenancy term; map the application's tenant onto it. Without an implementation there are no
  divisions and every division is denied.
- `INhActorDirectory`: names, e-mail addresses and cultures of actors in one call. Owner names,
  the assistant's display names and the language of in-app agent notifications come from it.
- `INhActorSearch`: actors whose name or e-mail address contains a query, for example the account an
  agent answers to in the assistant's administration. NewHeap Identity searches its users and
  leaves out locked-out ones; without an implementation modules ask for an actor's id instead.
- `INhDivisionDirectory` (optional): the divisions of the application with their names
  (`NhDivisionInfo`), listed by name with `ListAsync(limit)` and found by id with `GetAsync`.
  Divisions are optional: without a directory the application lists no divisions and modules
  offer nothing per division, so the assistant runs every agent app-wide. NewHeap Identity lists
  its division table; an application that works per tenant lists its tenants as divisions.
- `INhUserNotificationSink`: a message for a user in the application. The notifications module
  implements it; register your own to deliver to another inbox.
- `INhRetention`: call `ForgetUserAsync` and `ForgetDivisionAsync` from the application's own
  deletion flow, inside its transaction, because the modules have no foreign keys to cascade.

Register the application's implementations with `AddNewHeapActorContext`,
`AddNewHeapDivisionAccess`, `AddNewHeapActorDirectory`, `AddNewHeapActorSearch`,
`AddNewHeapDivisionDirectory` and `AddNewHeapUserNotificationSink`; they replace both the generic
defaults and NewHeap Identity's, whatever the order. The division directory has no generic
default. NewHeap
Identity implements all of them from the request, its division claims and its user table, and
`NhUserManager.DeleteAsync` calls `ForgetUserAsync` in the same transaction as the delete.

The platform's controllers and hub authenticate with the `Bearer` and `Identity.Application`
schemes; `AddNewHeapAuthenticationForwarding` forwards each one the application did not register
to its own scheme. Map the hub with `MapNewHeapBackgroundOperations` and the controllers with
`MapControllers`. The NewHeap Angular clients expect enum names, so serialize MVC JSON with
`JsonStringEnumConverter`. Turn on in-app agent notifications with
`UseInAppAgentNotifications()` on the assistant builder.

## Avoid

- Deriving the application's context from `NhIdentityDbContext` only to host background
  operations, or copying the module mappings by hand.
- Hosting the modules in a second context of the same database when the enqueue must commit with
  the application's domain changes.
- Leaving the projection of notifications on without the notifications module or a sink; the
  host refuses to start.
- Deleting a user or tenant without `INhRetention` in an application without foreign keys to the
  module tables.
- Mapping the application's tenant onto divisions without an `INhDivisionAccess` that denies
  unknown tenants.
- Registering an `INhDivisionDirectory` that lists nothing to switch per-division work off; leave
  the directory out instead, and list only divisions `INhDivisionAccess` knows.
- Using a non-`Guid` user id; both modules store user ids as `Guid`.

## Verification

Build a host with a plain EF context on SQL Server and PostgreSQL. Assert that the module tables
land in `nhops` and the overridden notification schema, that an enqueue in a rolled-back
application transaction leaves no operation, that a committed one is listed by the controller for
the application's actor and refused for a tenant it may not use, that two milestones reach the
owner in one notification thread, and that `ForgetUserAsync` and `ForgetDivisionAsync` remove
operations leaf first and the user's notifications, with a second call changing nothing. Assert
that the host refuses to start with notification projection and no notification store. Assert
that a new migration of an application on `NhIdentityDbContext` is empty, and that deleting a user
through `NhUserManager` runs retention in the same transaction and keeps the user when a module
cannot forget it. Run an agent from a domain event to an executed proposal in a host without
NewHeap Identity and assert the approver's in-app notification in their culture. Assert on both
providers that NewHeap Identity's division directory lists divisions by name and then id within the
limit and finds them by id, that an application directory replaces it in either order, and that
the generic defaults register none. SPM-281 is the executable reference; SPM-302 shows the
directory in the sample.
