# v-next

## NewHeap.Platform.AI.Chat.AspNet: a host with only chat-only agents follows the run configuration

This adds to the 22.0.0 note on chat-only agents (`run.ChatOnly()`); nothing changes. A host whose only
agents with run settings are chat-only still loads the run configuration defined as data when it
starts and checks its stamp every 30 seconds (`NhAssistantRunConfigurationRefresh`, a hosted service,
not a background operation). A chat turn does not check the stamp, so this is how the agent's skills
defined as data come into effect and follow changes made on another host.

| Breaking change | Required action |
|---|---|
| None. | None. |

## NewHeap.Platform.Common: complete filter keys checked against [Filterable] view models

Collection processing now checks every dotted segment of a client filter key, in every `Ands` and
`Ors` node, on the attribute-based path (`ProcessFilter`, `IsFilterValid`) and on the options-based
path (`FilterableFromAttributes`). The first segment must be a `[Filterable]` property of the view
model, and every later segment a `[Filterable]` property of the view-model type that the previous
segment reaches, or of its element type after a collection step such as `tasks{any}`. Before, only
the first segment was checked, so a key that continued behind a `[Filterable]` navigation or
collection could filter on entity columns that the view model does not expose. A configured
`WithFilterable(key, selector)` still allows exactly its own key. The shipped `DivisionUserViewModel`, `NhUserViewModel` and
`NhUserNotificationViewModel` already mark their nested filter members, so keys such as
`user.email`, `division.name`, `user.activeDivision.name` and `messages{any}.title` keep working.

The predicate builder follows the same path: it treats interface-typed collection navigations such
as `ICollection<T>` as collection steps (they failed with an `ArgumentException` before), applies
each collection step's own `{any}`/`{all}` token in a nested collection key (an inner token used to
replace the outer one), and reads those tokens in any letter case. On the options path, configured
selector keys inside `Ands` and `Ors` are no longer checked a second time against the view-model
attributes, which could reject them with a plain `Exception`.

| Breaking change | Required action |
|---|---|
| A filter key whose later segment names a member that is not `[Filterable]` on the nested view-model type, or not on it at all, is rejected with `InvalidFilterCollectionResultException` (`400` from the base controllers), also inside `Ands` and `Ors`. | Mark each nested view-model member that clients may filter on `[Filterable]`, together with the navigation or collection that leads to it; configure `WithFilterable(key, selector)` for a deliberate entity path. |
| Members of scalar values, such as `name.length` or `creationDateTime.year`, are no longer accepted as filter key segments. | Filter on a `[Filterable]` view-model property, or configure a selector for the value. |
| On the attribute-based path an unknown field or an unsupported operator throws `InvalidFilterCollectionResultException` instead of `Exception`, and the message names the complete key instead of its first segment. | None, unless code or a test compares the exception's exact type or message; catch `InvalidFilterCollectionResultException`. |

## NewHeap.Platform.Media.Core and NewHeap.Platform.Media.Http: moves and renames authorize their destination

A file or folder that moves or gets another name is now also authorized where it lands, as a create:
`IAuthorizationModule` sees the same `ActionType.Create` decision a direct upload or folder create there
would get (the destination folder and file name for a file, the destination parent for a folder). This
applies to `UpdateFileAsync(Guid, FileModel)`, `MoveFileAsync`, both `RenameFileAsync` overloads and
`UpdateFolderAsync`; an update that keeps the folder and name asks only for `ActionType.Update`, as before.
A destination that already holds a file or folder with that name, as the storage's own path lookup finds
it, is refused with `NhMediaErrorCodes.FileNameExists` or `NhMediaErrorCodes.FolderNameExists`. The
`PUT {prefix}/file/{id}` and `PUT {prefix}/folder` endpoints return 401 when the module refuses the source
or the destination, instead of failing with 500, and 409 with the error code for an existing name.

| Breaking change | Required action |
|---|---|
| A move or rename now needs create rights at its destination. | Grant `ActionType.Create` there in your `IAuthorizationModule` to principals who may move or rename into that folder. |
| A move or rename onto an existing name now fails instead of creating a second entry with that name. | Handle the failed result, or 409 from the HTTP endpoints, and choose another name. |
| The file and folder update endpoints now return 401 when authorization is refused. | Treat 401 as a refused media mutation, as for the other media endpoints. |

## NewHeap.Platform.Media.Core and NewHeap.Platform.Media.Http: tag updates are authorized

`IMediaLibraryService.UpdateFileTagsAsync` now asks `IAuthorizationModule` for an `ActionType.Update` of the
file's path and name before it reads or writes the file, like the other file mutations. A refused caller
gets the same answer for an existing and a missing file. `POST {prefix}/file/tags` returns 401 when the
module refuses the update.

| Breaking change | Required action |
|---|---|
| Tag updates now need update rights on the file. | Grant `ActionType.Update` in your `IAuthorizationModule` to principals who may change a file's tags. |
| `POST {prefix}/file/tags` now returns 401 when authorization is refused. | Treat 401 as a refused media mutation, as for the other media endpoints. |

## NewHeap.Platform.Media.Core and NewHeap.Platform.Media.Http: localizations are authorized updates of display strings

`IMediaLibraryService.LocalizeFieldAsync` now asks `IAuthorizationModule` for an `ActionType.Update` of the
file, with the localization's language, before it writes; until now only the endpoint's read of the file
was authorized. Only `Title`, `Description` and `AltText` can be localized, in any case: the service and
`RelationalFileStructureStorage.LocalizeAsync` refuse any other property name with
`NhMediaErrorCodes.PropertyNotLocalizable`, and reading files in a language applies only these three, so a
stored localization of another property, such as a file's name or id, no longer changes or breaks lists,
searches and file reads. `POST {prefix}/file/localize` returns the service's result: 204 when the
localization was stored or removed, 400 with the error code for another property, 404 for a missing file or
localization and 401 when the module refuses the update.

| Breaking change | Required action |
|---|---|
| Localization writes now need update rights on the file. | Grant `ActionType.Update` in your `IAuthorizationModule` to principals who may translate a file's display strings. |
| Only `Title`, `Description` and `AltText` can be localized; stored localizations of other properties are ignored. | Localize only these three; remove other stored rows from the `Localizations` table if you want them gone. |
| `POST {prefix}/file/localize` now returns 400 or 404 when the localization fails instead of 204. | Handle these responses; removing a localization that does not exist returns 404. |

## NewHeap.Platform.AspNet.Proxy: host-relative redirect destinations stay on this origin

A redirect destination that starts with `/` is now validated as the path the `Location` header
carries, after dot segments (`..`, `.`, `%2e%2e`, `%2e`) are removed. This applies when a rule is
saved or activated and to every regex expansion. A capture with dot segments can no longer turn a
host-relative destination into the network path `//host`: under `^/old-projects/([^?]+)(\?.*)?$`
with `/projects/$1$2`, a request for `/old-projects/%252e%252e//host` reaches the pattern as
`%2e%2e//host` and expanded to `/projects/%2e%2e//host`, which redirected to `//host`. Such an
expansion now skips redirect evaluation and the request passes through, as other unsafe expansions do.

| Breaking change | Required action |
|---|---|
| An exact target or regex template that resolves to `//host`, such as `/a/..//host`, now fails validation; a snapshot with one no longer saves or activates. | Use the host's absolute URL, or a path that resolves to one leading `/`. |

## NewHeap.Platform.AspNet.Common: refresh tokens stored as keyed digests

A refresh-token row (`NhUserAuthRefreshToken.Token`) now holds `nhr1:` and the HMAC-SHA256 of the token
instead of the token itself; only the client receives the token. The key comes from the new optional
secret `NewHeap:PlatformAspNetCommon:Authorization:RefreshToken:Key` (`NhRefreshTokenKey`), or else from
the shared JWT signing key. Refresh and logout look the row up by the digest of the presented token, so
a copy of the table cannot be presented as a refresh token. A row stored before the upgrade holds the
token itself and no longer matches; nothing falls back to it. The column keeps its type, since the
digest has 69 characters, so no migration is needed.

| Breaking change | Required action |
|---|---|
| Refresh tokens issued before the upgrade, or by a node that still runs the previous version, no longer refresh; each device signs in again once. | None. Optionally delete the `NhUserAuthRefreshToken` rows whose `Token` does not start with `nhr1:` once every node runs this version. |
| Signing in needs a key for the digest: an application that signs asymmetrically without the shared `JWT:Token:Key` fails with a configuration error. | Set `NewHeap:PlatformAspNetCommon:Authorization:RefreshToken:Key`, a random secret of at least 32 bytes, in the secrets file. |
| Changing the key in use ends every refresh session: the refresh-token key, or the shared signing key while no refresh-token key is set. | Set `RefreshToken:Key` before you rotate the shared signing key, so refresh sessions survive the rotation. |

## NewHeap.Platform.AspNet.Common: refresh refuses accounts that may not sign in

`AuthenticateRefreshTokenAsync`, behind `POST authentication/refresh`, now refuses a user whom Identity
does not let sign in (`SignInManager.CanSignInAsync`) or who is locked out or blocked
(`INhUserManager<TUser>.IsBlockedAsync`, which also sees a `LockoutStart` without an end or on an
account with lockout disabled). The check runs before the token is consumed and fails with the same
`Invalid refresh token` as an unknown token, so the response says nothing about the account. The token
stays stored and refreshes again once the account may sign in.

| Breaking change | Required action |
|---|---|
| A locked-out, blocked or otherwise not permitted account no longer refreshes; before, the refresh rotated its token and issued a new session. | None. To end the account's refresh sessions for good, also reset its password, which revokes its refresh tokens. |

## NewHeap.Platform.AspNet.Common: every sign-in path refuses blocked accounts

`NhAuthenticationService` now applies one eligibility check to every first factor and every step that
completes a sign-in: Identity must let the user sign in (`SignInManager.CanSignInAsync`), and the account
may be neither locked out nor blocked (`INhUserManager<TUser>.IsBlockedAsync`). Before, these paths saw
only Identity's lockout, so an account blocked through `UpdateUserLockout` without an end date, or on an
account with lockout disabled, still received a session. The check covers the password with and without
two-factor authentication, passkey sign-in, consumer credentials through `CreateAuthenticationSessionAsync`
and `CompleteFirstFactorAsync`, external providers, second-factor challenges, enrollment during sign-in
and refresh. The password is not checked for a blocked account, so a wrong one no longer counts as a
failed attempt.

| Breaking change | Required action |
|---|---|
| A blocked account receives `User locked out` on a first factor and `two-factor-locked-out` on a pending challenge or enrollment, instead of a session. | None. |

## NewHeap.Platform.AspNet.Common: a stored recovery-code hash redeems nothing

`NhUserManager.RedeemTwoFactorRecoveryCodeAsync` compared input that matched no hash once more with the
stored entries as it was, so that plaintext codes stored before NewHeap hashed recovery codes keep
working. A copy of a stored `nhv1:` hash therefore completed a recovery-code challenge or
reauthentication. Input with a `:`, which every stored hash and no generated code contains, now skips
that comparison and fails as a wrong code: it counts as a failed attempt and leaves the codes unchanged.
A plaintext code stored before the upgrade still redeems once.

| Breaking change | Required action |
|---|---|
| None. | None. |

## NewHeap.Platform.Common and NewHeap.Platform.AspNet.Common: impersonation bound to the origin's session

An impersonation token now also carries a SHA-256 digest of the security stamp of the user who started it
(`NhPlatformClaimTypes.ImpersonateOriginSecurityStamp`). The session validator refuses the token once
that stamp changes, for example after the origin's password change or reset, and while the origin is
locked out or blocked; before, it checked only the impersonated user. `Impersonate` refuses an origin
who may not sign in, and `ImpersonateRevert` issues the origin's new session only when the origin may
still sign in (`MaySignInAsync`: allowed, not locked out or blocked, a required second factor enrolled).

| Breaking change | Required action |
|---|---|
| Impersonation tokens issued before the upgrade carry no origin stamp and are refused with 401. | None. Start the impersonation again. |
| A password change, a reset or another security-stamp change of the origin, and a lockout or block of the origin, end its impersonation; `ImpersonateRevert` answers 400 for an origin who may not sign in. | None. |

## NewHeap.Platform.AspNet.Common: inherited writes stay in the controller's scope

`DoUpdate` and `DoUpdatePartial` of `DbEntityProtectedNhBaseController` and
`CompositeDbEntityProtectedNhBaseController` first resolve the row through the controller's
`GetQueryableAsync`, as `DoDelete` already did, and return `404 Not Found` outside it without calling
the service. A scope filter there, such as the active-division filter of the division controllers,
now bounds writes as well as reads. Without the division access-all permission,
`NhBaseDivisionUserController.Create` and `Update` return `403 Forbidden` for a body whose
`DivisionId` is not the active division, and `NhBaseDivisionController.Create` returns `403` because
a new division lies outside the active division. The sample manages the memberships of Sample North
under `/division-memberships` (SPM-211).

| Breaking change | Required action |
|---|---|
| An update or partial update of a row outside `GetQueryableAsync`, including an id that does not exist, returns `404` instead of reaching the service (which answered `400` for an unknown id). | None, unless a client or test expected `400` for an unknown id or wrote rows that its own reads exclude; widen `GetQueryableAsync` or write through a custom action that checks access itself. |
| `NhBaseDivisionUserController.Create` and `Update` return `403` for a body that names another division than the active one, unless the caller has the division access-all permission. | None, unless a division manager adds members to other divisions; grant the access-all permission or override the action. |
| `NhBaseDivisionController.Create` returns `403` unless the caller has the division access-all permission. | Grant the division access-all permission to the people who add divisions, or override `Create` with the application's own check. |

## NewHeap.Platform.AspNet.Common: a locked-out division membership grants nothing

`NhUserManager.GetValidClaimsByUserIdAsync` counts only the user's own division memberships that
are not locked out. A membership is locked out from the day its `LockOutStartDateTime` is reached
through the day of its `LockOutEndDateTime`, or indefinitely without an end date, as
`IsBlockedAsync` treats account lockout. Before, another member's row or a lockout without an end
date could keep the division, and its roles were loaded without a lockout check. Issued tokens and
the claims that division policies, `HasDivisionAccessAsync` and `INhDivisionAccess` reload per
request now leave out the locked-out division.

| Breaking change | Required action |
|---|---|
| A member whose division membership is locked out gets no claims for that division until the lockout ends, also when the division has other members or the lockout has no end date. | None, unless an application relied on locked-out members keeping access; end or remove the lockout instead. Tokens issued before the update keep their claims until they expire, while division policies deny at once. |

## NewHeap.Platform.Common and NewHeap.Platform.AspNet.Common: division access requires membership

`NhUserManager.DivisionAccessAsync` requires membership of the requested division for every
decision, also when no claim or role is required, and treats a `null` and an empty requirement
alike. `HttpContextExtensions.HasDivisionAccessAsync`, NewHeap Identity's `INhDivisionAccess` and a
`RequireActiveDivisionAccess()` policy without arguments therefore deny a user who is no member of
the division, and a division id that does not exist; the division access-all permission still grants
every division. `GetValidClaimsByUserIdAsync` adds a `NhPlatformClaimTypes.DivisionMember` claim
(`nh.platform.division.member`, the division id as value) for every active membership, also one
without division roles, and a division role in the division counts as membership as well.

| Breaking change | Required action |
|---|---|
| A division check without requirements denies a user who is no member of the division (it allowed any signed-in user), and one with an empty claim and role list allows a member (it denied everyone). | None, unless people without a membership relied on such a check; add their membership or grant the division access-all permission. |
| Issued tokens and reloaded claims gain one `nh.platform.division.member` claim per division the user is a member of. | None, unless an application keeps division claims out of the token by type or rebuilds division claims itself; treat the membership claim like the division role and permission claims, or keep a division role on every membership. |
| `NhPlatformClaimTypes` gains `DivisionMember`. | None. |

## NewHeap.Platform.AI.Common, NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: agent work stays in the caller's tenant

Every path that reaches agent work now applies the tenant rule that the proposal and run reads already
applied: a thread, run, proposal or subject of another tenant than the caller's reads as unknown, and
the whole application reaches only callers without a tenant or of the `UseSingleTenant` tenant. The
subject access resolver gets no tenant, so a person whose actor id holds roles in several tenants no
longer reaches another tenant's agent work with a sign-in for this one. Forgetting a deleted subject
can now name its scope, so an equal id of another tenant or division keeps its threads. Hosts without
tenants, and hosts whose resolver describes each subject in the tenant its people sign in with, see no
change.

| Breaking change | Required action |
|---|---|
| The routes by id (`GET threads/{id}/runs`, `consultations`, `proposals`, `memory`, `artifacts` and an artifact's content, `POST runs/{runId}/cancel` and `retry`), deciding one or several proposals, `resolve`, `feedback`, `INhAssistantProposalDecisionService` and `GET conversations/{id}` answer a thread of another tenant `404` before they record a participant. | Describe each subject with the tenant its people sign in with (`NhAiSubjectInfo.TenantId`). |
| `GET threads`, `POST threads/open` and `threads/start`, `GET subjects/{type}/{id}/work` and `agents` answer a subject of another tenant `404 assistant-subject-not-found`; `POST subjects/{type}/summary` counts it zero. | None; treat the `404` like an unknown subject. |
| `POST threads/start` answers an unknown subject `404 assistant-subject-not-found`, also for a caller without a role, instead of `403 assistant-subject-access-denied`. | Treat both `404` and `403` as "cannot start here". |
| `GET approvals/inbox` (flat and grouped) and `INhAssistantProposalCounts` list only proposals of threads in the caller's tenant. | Pass the person's tenant in `NhAssistantDecidingPerson.TenantId`. |
| The consult and request tools refuse a subject of another tenant than the asker's, and the whole application for an asker bound to a tenant that takes no part in it, with `assistant-consult-subject-unavailable`. | None. |
| `INhAssistantSubjectWork.GetAsync` and `SummarizeAsync` with an actor id and a principal are obsolete: they compare no tenant. | Call `GetAsync(subject, new NhAssistantDecidingPerson(user, actorId, tenantId), agentId)` and `SummarizeAsync(subjectType, subjectIds, person)`; an implementation of the interface adds both. |
| `INhAiSubjectRetention.ForgetSubjectAsync(subject)` is obsolete: it forgets the id in every tenant and division. | Read the subject's scope key (`NhAiSubjectInfo.ScopeKey`) before the delete and call `ForgetSubjectAsync(subject, scopeKey)`; an implementation of the interface adds the overload. |

## NewHeap.Platform.DatabaseRead.Tool: PostgreSQL principal verification denies by default

`query` and `schema` verified a PostgreSQL principal against a fixed list of write and DDL privileges.
Membership in a predefined role such as `pg_monitor`, `pg_read_all_stats`, `pg_read_all_settings`,
`pg_signal_backend` or a server-file role passed, as did EXECUTE on a `pg_catalog` function that the
server revokes from PUBLIC, such as `pg_stat_reset`, and every function in a user schema whose name
starts with `pg` followed by another character, because the schema filter used `pg_%` as a `LIKE`
pattern. Column-level INSERT or UPDATE, writes through views and USAGE on sequences passed as well.

The verification now covers the connected role and every role it is a member of, also without
inheritance, and denies by default. Beyond the privileges the server granted at initialization, the
principal may hold SELECT on tables, views, sequences and columns, and membership in
`pg_read_all_data`. Any other predefined role, a role attribute such as CREATEROLE, ownership of the
database or of any schema, relation, function or type, CREATE on the database or a schema, any other
relation, column or sequence privilege, EXECUTE on a function outside `pg_catalog` and
`information_schema`, and EXECUTE on a `pg_catalog` function that the server does not grant to PUBLIC
reject it. Schema names are compared exactly. A rejected PostgreSQL principal now also reports the
first failed check as `error.verificationCheck` with stage `readonly-verification`: one of
`role-attribute-superuser`, `role-attribute-createrole`, `role-attribute-createdb`,
`role-attribute-replication`, `role-attribute-bypassrls`, `database-owner`,
`predefined-role-membership`, `database-create`, `schema-create`, `object-owner`, `relation-write`,
`column-write`, `sequence-usage`, `function-execute` or `catalog-function-execute`.

| Breaking change | Required action |
|---|---|
| A PostgreSQL principal that holds anything beyond SELECT and `pg_read_all_data`, including a predefined monitoring or signalling role, an administrative `pg_catalog` function, a column or view write or USAGE on a sequence, is refused with `read-only-principal-not-verified`. | Revoke the grant that `error.verificationCheck` names from the diagnostic principal and the roles it belongs to; grant only CONNECT, schema USAGE and SELECT. |
| A rejected PostgreSQL principal now reports `error.verificationCheck` and `error.stage`. | Let code that reads the field accept PostgreSQL check identifiers, or discard identifiers it does not know. |

## NewHeap.Platform.DatabaseRead.Tool: SQL Server principal verification denies by default

SQL Server verification checked a fixed list of roles and permissions. A principal with
`VIEW SERVER STATE`, `ALTER ANY CONNECTION`, `IMPERSONATE ANY LOGIN`, `ALTER ANY SCHEMA`, IMPERSONATE
on a login or a user, UPDATE on a sequence, ALTER or TAKE OWNERSHIP on a schema or an object, or a
column-level UPDATE passed. The checks that existed keep their identifiers and order. After them,
these permissions now reject the principal with `server-permission-view-server-state` (also through
`##MS_ServerStateReader##`), `server-permission-alter-any-connection`,
`server-permission-impersonate-any-login`, `database-permission-alter-any-schema`,
`login-impersonate`, `user-impersonate`, `sequence-update`, `schema-permission-alter`,
`schema-permission-take-ownership`, `object-permission-alter`, `object-permission-take-ownership`
and `column-update`. The verification then denies by default: every effective permission on the
server, the database, a schema, an object, a column, a login, a server role, a database principal or
another database securable other than connecting, SELECT, `VIEW DEFINITION`, `VIEW CHANGE TRACKING`,
`SHOWPLAN` and the column-key definitions that every database grants to public rejects the principal
with `server-permission-not-allowlisted`, `database-permission-not-allowlisted`,
`schema-permission-not-allowlisted`, `object-permission-not-allowlisted`,
`column-permission-not-allowlisted`, `server-principal-permission-not-allowlisted`,
`database-principal-permission-not-allowlisted` or `database-securable-permission-not-allowlisted`.
`db_datareader` and database-level SELECT still pass.

| Breaking change | Required action |
|---|---|
| A SQL Server principal that holds a permission beyond connecting, SELECT and metadata visibility, such as `VIEW SERVER STATE`, `VIEW DATABASE STATE`, `UNMASK`, `REFERENCES`, IMPERSONATE or UPDATE on a sequence, is refused with `read-only-principal-not-verified`. | Revoke the permission that `error.verificationCheck` names from the diagnostic login, its user and the roles they belong to. |
| `error.verificationCheck` can carry the new SQL Server identifiers listed above. | Let code that reads the field accept them, or discard identifiers it does not know. |

## NewHeap.Platform.DatabaseRead.Tool: quoted PostgreSQL function names are refused

The SQL policy skipped double-quoted text, so a forbidden PostgreSQL function written as a quoted
identifier, such as `SELECT "pg_sleep"(1)`, `pg_catalog."pg_advisory_lock"(...)` or
`"pg_terminate_backend"(...)`, passed validation and ran; PostgreSQL resolves the quoted lowercase name
to the same function. A session-level advisory lock taken this way outlives the rolled-back read-only
transaction in the pooled session. The policy now compares the text of every double-quoted identifier
on PostgreSQL, with doubled quotes unescaped and case ignored, with its function list and refuses a
match with `function-not-allowed`. A Unicode-escaped identifier (`U&"..."`) is refused with the new
code `unicode-escaped-identifier-not-allowed`. Quoted identifiers that name no forbidden function, also
keyword names such as `"Update"` or `"Share"`, stay allowed on both providers.

| Breaking change | Required action |
|---|---|
| A PostgreSQL query that uses a double-quoted identifier spelled like a forbidden function, also as a column alias, or a `U&"..."` identifier is refused before execution. | Rename the alias, or write the identifier without Unicode escapes. |
