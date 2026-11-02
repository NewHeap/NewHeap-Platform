# v-next

## NewHeap.Platform.Common: time filters of a collection on every provider

- A collection filter on a `DateTime` or `DateTimeOffset` member reads its value as an instant in UTC on SQL Server
  and PostgreSQL, whatever zone the host runs in. `Z` and an offset keep their instant, and a value with neither,
  or a date alone such as `2026-11-01`, is read as UTC. PostgreSQL used to fail with a `500` on every such value
  but one with `Z` on a `DateTimeOffset` member, and on every value for a `DateTime` member.
- A filter value that its member's type cannot hold, such as `next week` for a date and time or `first` for a
  number, fails with `InvalidFilterCollectionResultException` before the query runs, which the base controllers
  return as `400`, also behind a collection key such as `tasks{any}.dueAt`. It used to fail when the query ran, as a
  `500`. The message names the field, and for a date and time the form `2026-11-01T09:00:00Z`.

| Breaking change | Required action |
|---|---|
| On SQL Server a value without a zone, and every value for a `DateTime` member, used to be read in the host's time zone; it is now read as UTC. On a host that runs in UTC nothing changes. | Send `Z` or an offset when a filter means another zone than UTC. Keep `DateTime` columns in UTC, as the providers' guidance asks. |
| `CollectionProcessingService.GetExpressionConstant` converts the value when it builds the constant and throws `InvalidFilterCollectionResultException` for a value the type cannot hold. | A subclass that calls it handles that exception as an invalid filter. |
