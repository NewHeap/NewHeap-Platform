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

## Apply the assistant schema yourself

Every `nuget-common` GitHub Release carries
`newheap-assistant-schema-sqlserver-<version>.sql`, the idempotent script of these
migrations for the default `nhai` schema, written by
`dotnet ef migrations script --idempotent` from an empty database. It brings a
database of any earlier version up to that release and changes nothing when it runs
again, so a deployment step can apply it with `sqlcmd` or another tool that understands `GO`. For
another schema, write the script from a checkout of the release's tag:

```text
npm run release:sql-scripts -- --out <directory> --schema <name>
```

The design-time factory routes the migrations to the schema after `--` as a host
does; add migrations only for the default schema.
