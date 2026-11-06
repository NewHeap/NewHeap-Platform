# v-next

## NewHeap.Platform.AspNet.Common, NewHeap.Platform.AspNet.Common.SqlServer and NewHeap.Platform.AspNet.Common.PostgreSql: bulk upsert writes database-defaulted columns

`ExecuteUpsertAsync` skipped every non-key property generated on add, which includes each
property with `HasDefaultValue` or `HasDefaultValueSql`, so a value the caller set was lost
silently. Per batch such a column is now written on insert and update when every row sets it
to a value other than its sentinel, and left to the database default when no row does. The
graph overload applies the rule to the roots and to each group of dependents. Properties
generated on update or computed are still never written. On SQL Server a graph update of a
dependent with an identity key staged renumbered keys and could update another row; the
staged keys are now kept.

| Breaking change | Required action |
|---|---|
| A property with a database default that every row of a batch sets is now written on insert and update instead of being replaced by the database default. | Leave the property at its CLR default (or configured sentinel) on every row to keep the database default, and on updates that must keep the stored value. |
| A batch in which only some rows set such a property throws `InvalidOperationException` naming the property before any SQL runs. | Set it on every row or on none, or import the two groups in separate calls. |
| For entity types with such properties the scalar overload reads the input into memory once before staging it. | Split very large imports of those entity types into batches. |

## NewHeap.Platform.AspNet.Common: impersonation token lifetime

`UserNamePasswordOptions.ExpirationTimespanImpersonationToken` sets how long an impersonation
access token is valid, through `AuthenticationConfiguration.ExpirationTimespanImpersonationToken`.
The default stays one day; a value that is not positive fails at registration.

## NewHeap.Platform.AspNet.Common: Dutch Identity error messages

`MultiLanguageIdentityErrorDescriber` now ships an English neutral resource and an nl-NL
resource for all its messages, so Identity errors are Dutch for nl-NL instead of English in
every culture.

| Breaking change | Required action |
|---|---|
| The describer's English neutral resource is found before translations of an application assembly registered in `NhLocalizationOptions` with an `Order` of 99 or more, so those translations no longer apply, and the library's Dutch replaces the application's own nl-NL text for it. | Register the application assembly with an `Order` below 99, as the sample does with 0. |
