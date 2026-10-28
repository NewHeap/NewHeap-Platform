# v-next

## NewHeap.Platform.AspNet.Common: division lockouts in whole UTC days

`NhUserManager.GetValidClaimsByUserIdAsync` decides whether a division membership is locked out by
comparing its `LockOutStartDateTime` and `LockOutEndDateTime` with the start of today and of
tomorrow in UTC, instead of reading their `.Value.Date`. A membership stays locked out from its start
day through its end day, or indefinitely without an end date. The predicate no longer reads the value
of an empty date, so a user without an active division or memberships gets the same claims on
EF Core InMemory as on SQL Server and PostgreSQL. The day follows the application's clock: a
registered `TimeProvider`, otherwise the system clock.

| Breaking change | Required action |
|---|---|
| A lockout date stored with a non-zero offset counts in the UTC day of its instant at the day edges; SQL Server's `CONVERT(date, …)` used the day of the stored offset before, PostgreSQL already stored UTC. | None, unless an application stores lockout dates with a local offset and expects that offset's day; store them in UTC for the day they mean. |
| A registered `TimeProvider` decides the current day of division lockouts. | None, unless a host registers a clock that differs from real time outside tests. |
