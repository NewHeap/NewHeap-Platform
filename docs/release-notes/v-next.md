# v-next

## NewHeap.Platform.Media.FileStructureStorage.SqlServer: cryptography dependency on the 10.0 line

The SQL Server media provider requires `System.Security.Cryptography.Pkcs` 10.0.13 or later instead
of 9.0.21, in line with the mail dependencies of `NewHeap.Platform.Common`; 10.0.13 is the 10.0
line's fix for CVE-2026-84008 (GHSA-846f-f764-76mx). No API or schema changes are required.
