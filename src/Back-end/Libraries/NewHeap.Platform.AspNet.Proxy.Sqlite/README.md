# NewHeap ASP.NET Proxy SQLite

Store redirects, rewrites and login activity in a local SQLite database.
No separate database server is required.

[Install the proxy](../NewHeap.Platform.AspNet.Proxy/README.md#installation) · [Configuration](../NewHeap.Platform.AspNet.Proxy/README.md#configuration) · [Usage reference](../../../../docs/how-to/use-newheap-proxy.md)

## Storage configuration

`AddNewHeapProxy` creates the database and loads saved rules at startup.
Configure `Sqlite:DatabasePath` and `Sqlite:BusyTimeout` under `NewHeapProxy`;
see the configuration table for defaults. Restart after changing settings.

| Requirement | What to do |
| --- | --- |
| Location | Use writable, persistent local storage outside the webroot. Network and in-memory databases are unsupported. |
| Relative paths | Paths are relative to the application's content root. |
| Containers | Mount persistent local storage for the database directory. |
| Ownership | Run one proxy instance per database file. Stop it before opening an offline store. |
| Live changes | Use the administration panel or configuration service. Direct database edits and multiple writers are unsupported. |

## Backups and upgrades

- Back up after a clean shutdown, or use SQLite's backup API while running.
- Do not copy only the main database while the proxy is running; recent changes
  may be in the write-ahead log.
- Back up before upgrading. Database upgrades preserve rules and login activity,
  but older proxy versions may not reopen the upgraded file.

## Startup problems

| Problem | Action |
| --- | --- |
| Database cannot be opened | Check the directory, file permissions and available storage. |
| Another instance owns the database | Stop that instance or use a separate database file. |
| A `.lock` file remains after shutdown | Its presence alone does not mean the database is in use. Never delete it to bypass a running instance. |
| Invalid or incompatible database | Check application diagnostics and restore a compatible backup if needed. Startup fails instead of resetting stored rules. |

## Seed rules offline

See [Seed a redirect before starting the host](../../../../docs/how-to/use-newheap-proxy.md#seed-a-redirect-before-starting-the-host)
for an example that retains existing rules and checks save failures.
