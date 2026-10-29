# v-next

## NewHeap.Platform.DatabaseRead.Tool: the MySQL and MariaDB server check

A `mysql` or `mariadb` profile now checks the flavor and the minimum version (MySQL 8.0, MariaDB 10.11) on the
version the server reports while the connection opens, also behind MariaDB's `5.5.5-` prefix, before any
statement runs. Schema reads and queries against an older server, such as MySQL 5.7, now stop with
`provider-version-not-supported` (exit code 3), as documented, instead of a `database-query-failed` with
`permission-denied` (1370) or `database-failure` (1305) from `CURRENT_ROLE()` (SPM-367). Supported servers are
unchanged.
