# v-next

## NewHeap.Platform.Common and NewHeap.Platform.AspNet.Common.PostgreSql: case-insensitive collection search on PostgreSQL

| Breaking change | Required action |
|---|---|
| On a context configured with `UseNewHeapPostgreSql`, collection search and the `LIKE` filter now match case-insensitively with `ILIKE`, as SQL Server's default collation does, so the same search can return more rows than before. | None to match SQL Server; to keep case-sensitive matching, call `postgres.UseNewHeapCaseSensitiveCollectionTextMatch()` in the `UseNewHeapPostgreSql` configure action. |

- Collection processing matches text with `EF.Functions.Like(value, NhCollectionTextMatch.Pattern(pattern))`. SQL
  Server and contexts without a NewHeap PostgreSQL registration keep their `LIKE`; `%` and `_` keep their meaning
  on every provider. Mark a pattern with `NhCollectionTextMatch.Pattern` to match text the same way in a query of
  your own. A context configured with plain `UseNpgsql` keeps the case-sensitive `LIKE`.
