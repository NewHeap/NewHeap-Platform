# v-next

## NewHeap.Platform.Media.FileStructureStorage.SqlServer: patched SQL Client cryptography dependency

The SQL Server media provider now requires `System.Security.Cryptography.Pkcs`
9.0.21 or later to address CVE-2026-84008 (GHSA-846f-f764-76mx). Upgrade the Media
release unit so the patched minimum reaches consuming applications; no API or
schema changes are required.
