# v-next

## NewHeap.Platform.AspNet.Common.SqlServer, NewHeap.Platform.AI.Chat.SqlServer, NewHeap.Platform.Events.Cap and NewHeap.Platform.DatabaseRead.Tool: patched SQL Client cryptography dependency

SQL Server packages and the database read tool now require
`System.Security.Cryptography.Pkcs` 9.0.21 or later to address CVE-2026-84008
(GHSA-846f-f764-76mx). An explicit package dependency carries the patched minimum
into consuming applications instead of retaining SQL Client's vulnerable 9.0.4
dependency. Upgrade the owning Common release unit; no API or schema changes are
required.

## NewHeap.Platform.Media.FileStructureStorage.SqlServer: patched SQL Client cryptography dependency

The SQL Server media provider now requires `System.Security.Cryptography.Pkcs`
9.0.21 or later to address CVE-2026-84008 (GHSA-846f-f764-76mx). Upgrade the Media
release unit so the patched minimum reaches consuming applications; no API or
schema changes are required.
