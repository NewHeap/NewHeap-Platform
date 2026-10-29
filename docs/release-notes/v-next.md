# v-next

## NewHeap.Platform.DatabaseRead.Tool: the MySQL and MariaDB server check and `mysql-compatible`

A `mysql` or `mariadb` profile now checks the flavor and the minimum version (MySQL 8.0, MariaDB 10.11) on the
version the server reports while the connection opens, also behind MariaDB's `5.5.5-` prefix, before any
statement runs. Schema reads and queries against an older server, such as MySQL 5.7, now stop with
`provider-version-not-supported` (exit code 3), as documented, instead of a `database-query-failed` with
`permission-denied` (1370) or `database-failure` (1305) from `CURRENT_ROLE()` (SPM-367). Supported servers are
unchanged.

A profile's `provider` may also be `mysql-compatible`, for one profile that reaches a MySQL server in one
environment and a MariaDB server in another. The tool takes the flavor from that server version, keeps each
flavor's minimum, and follows the detected flavor for the statement limit, role activation, grant reading and
index evidence. `target.provider` and a database failure's `provider` report the detected `mysql` or `mariadb`;
`validate` and a failure before the server answered report `mysql-compatible`. `mysql` and `mariadb` stay strict
and still stop with `provider-flavor-mismatch` against the other flavor (SPM-367). An MCP wrapper or script that
switches on `provider` sees only the values it already knows once a server answered.
