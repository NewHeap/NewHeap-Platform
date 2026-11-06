# v-next

## NewHeap.Platform.Common and NewHeap.Platform.AspNet.Common.PostgreSql: case-insensitive collection search on PostgreSQL

| Breaking change | Required action |
|---|---|
| Collection search and the `LIKE` filter on a context configured with `UseNewHeapPostgreSql` now match case-insensitively (`ILIKE`), as on SQL Server's default collation, so they can return more rows. | To keep case-sensitive matching, call `postgres.UseNewHeapCaseSensitiveCollectionTextMatch()` in the `UseNewHeapPostgreSql` configure action. |

- Collection processing matches text with `EF.Functions.Like(value, NhCollectionTextMatch.Pattern(pattern))`. SQL
  Server and contexts without a NewHeap PostgreSQL registration keep their `LIKE`; `%` and `_` keep their meaning
  on every provider. Mark a pattern with `NhCollectionTextMatch.Pattern` to match text the same way in a query of
  your own. A context configured with plain `UseNpgsql` keeps the case-sensitive `LIKE`.
