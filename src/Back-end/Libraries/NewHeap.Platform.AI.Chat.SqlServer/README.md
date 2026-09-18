# NewHeap.Platform.AI.Chat.SqlServer

SQL Server persistence, registration and migrations for NewHeap AI assistant
conversations.

```csharp
services.AddNewHeapAssistant(assistant => assistant
    .UseSqlServer(connectionString, options =>
    {
        options.Schema = "nhai";
        options.RunMigrations = true;
    })
    // agents, access policy and limits
);
```

`UseSqlServer` also accepts a `Func<IServiceProvider, string>` to resolve the
connection string from configuration. The package contains the SQL Server migrations
of the library-owned schema. They are generated for `nhai` and routed to a configured
schema at migration time without rewriting migration history; the migrations history
table lives in the same schema. With `RunMigrations = true` the host applies pending
migrations while it starts, before it accepts requests. Leave it off when migrations
are applied as a deployment step.

Design-time commands use `NH_ASSISTANT_SQLSERVER_CONNECTION`:

```text
dotnet ef migrations add <Name> --project src/Back-end/Libraries/NewHeap.Platform.AI.Chat.SqlServer --startup-project src/Back-end/Libraries/NewHeap.Platform.AI.Chat.SqlServer --context NhAssistantDbContext --output-dir Migrations
```
