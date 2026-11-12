# Database diagnostics

`newheap-db` reads the application database for diagnostics through the
`commerce-development` profile in `../../.newheap/database-read.json`. Run it from
`src/Back-end`; the tool manifest is in `.config/dotnet-tools.json` at the
repository root.

The profile resolves the `DiagnosticsReadOnly` connection string through the
application's normal configuration and secrets of the selected environment. That
connection string belongs to a dedicated read-only database account.

Checked-in requests in `requests/`:

- `order-schema.json` describes the `Orders` table;
- `order-by-id.json` reads one order by its id.
