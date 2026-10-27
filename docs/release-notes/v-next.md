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

## NewHeap.Platform.AspNet.Proxy: denied addresses no longer spend the administrator's login budget

`NhProxyAdministrationService.SignInAsync` now refuses a caller outside the IP allowlist before it takes a
permit from the shared login budget (`Administrator:LoginAttemptLimit` per `LoginAttemptWindow`). Before,
every tokenless `POST /newheap-proxy/Login` from a denied address spent one, so about five such requests
a minute kept allowlisted administrators at 429. Denied attempts are still audited as `IpDenied`, now
within a separate budget of `LoginAttemptLimit` rows per `LoginAttemptWindow`; further denied attempts
in that window are refused with 403 without an audit row, so outside traffic cannot grow the login audit
without bound.

| Breaking change | Required action |
|---|---|
| Denied attempts beyond `LoginAttemptLimit` per `LoginAttemptWindow` no longer appear in the login audit. | None, unless monitoring counts `IpDenied` rows to measure outside traffic; count refused requests in the host's request logs instead. |

## @newheap/platform-common: logout always ends the browser session

`BaseNhAuthService.logout()` now ends the local session after the logout request whether the request
succeeds or not. Before, a failed request (offline, a timeout, CORS or an error response such as 500)
left the stored authorization (`at` in `localStorage`) with its access and refresh token, so
`isAuthenticated()` stayed `true` and the next person on the device kept the session, also after a
reload. The result still fails in that case, so the application can report that the sign-out did not
reach the server, which alone can end a cookie session. `clearAuthorization()` now removes the stored
record and the cached authorization also when the access token has expired; before, it did nothing
unless the session was still valid, which left the refresh token in the browser. It emits `undefined`
on `authSubject` (unless `doDispatchEvent` is `false`) and dispatches the session expiration information
only when there was an authorization to remove, as before for a valid session.

| Breaking change | Required action |
|---|---|
| `logout()` ends the local session also when it returns a failed result. | None, unless the application kept people signed in after a failed logout; report the failure and let them sign in again. |
| `clearAuthorization()` removes an expired authorization and then emits `undefined` on `authSubject` and new session expiration information. | None, unless a subscriber treats that emission as an explicit sign-out; an expired session already read as signed out. Remove overrides that only made either method clear in these cases. |

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

## NewHeap.Platform.AI.Chat: a shadow run executes no effect

Shadow is now part of the effect decision. A run in an agent's shadow thread (an agent, skill or data
trigger in shadow, a shadow variant, a skill or trigger candidate, a replay) needs an approval for
every effect it calls, whatever the effect policy decides, and records it as a shadow proposal that
never executes. Before, an effect the policy runs without approval ran live: an idempotent mutation
with `Approval = NotRequired`, the generated `assistant.request.*` tool, an `Issuer` or
`ConsumerAuthoritative` tool of an agent that answers to a technical account or the subject's
responsible person, and an imported MCP tool that the application's effect policy allows. A read runs
as before; the generated consult tool, a read, refuses a shadow run itself (see "shadow runs consult and
request no live agent"). Live runs, people's turns and consultations decide as before.

| Breaking change | Required action |
|---|---|
| A run in a shadow thread records a call of an effect the policy allows without approval as a shadow proposal instead of running it, and refuses an `Issuer` or `ConsumerAuthoritative` tool with `ai-tool-effect-denied`. | None; put the agent, skill or trigger live to let it act. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: a hand-off follow-up of shadow work stays in shadow

A hand-off with `NotifySource()` sent its follow-up (`agent-handoff-ended`) into the source agent's live
thread whatever had worked in shadow, so a live source that handed work to an agent in shadow started a
live run and its thread's readers read the shadow run's summary. When the target's run or the handing
run worked in shadow, the follow-up now goes to the source agent's shadow thread of the subject, where
nobody is notified and nothing executes, whatever the source's mode is by then. While the source agent
or skill is paused, such a follow-up is ignored instead of waiting for a resume.

| Breaking change | Required action |
|---|---|
| The follow-up of a hand-off whose target or source run worked in shadow starts a run in the source agent's shadow thread instead of its live thread, and is ignored while the source is paused. | None; read the follow-up in the source's shadow thread while the target is evaluated. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: a hand-off summary reaches only the target's audience

A hand-off follow-up copied the target run's summary into the source's thread whenever both agents
worked on the same subject, so every reader of the source's thread read an answer meant for the
target's audience. The summary is now copied only when every reader of the thread that receives the
follow-up may read the target's thread: the target's required policy, and for the whole application
its view policy, names no policy or the source's own, and every person invited to that thread may read
the target's thread too. Otherwise the follow-up names the outcome and the number of proposals only.

| Breaking change | Required action |
|---|---|
| `NhAssistantHandOffResult.Summary` is `null`, and the follow-up's text names no summary, when the target has a required or view policy the source does not share or a person invited to the source's thread may not read the target's. | None; give both agents the same policies when the source must read the target's answer. |

## NewHeap.Platform.AI.Chat: application messages readable by a person follow the live read rule in agent threads

`NhAssistantApplicationMessageAccess.ReadableBy(person)` accepted an agent thread as soon as the person
had a participant row, which a role on the thread's subject leaves behind and which outlived that role
until the person opened the thread again. For an agent thread the post now applies the live read rule
before anything is stored, shown or notified: never a shadow thread or a thread of a shadow variant,
only a thread of the person's tenant or of the whole application they take part in, and only with a
live role on the thread's subject or an invitation to the thread. Chats keep their rule: the owner or a
participant of the same tenant.

| Breaking change | Required action |
|---|---|
| `PostAsync` with `ReadableBy` answers `assistant-conversation-not-found` for an agent thread whose subject the person no longer has a role on, a shadow thread and another tenant's thread, also when a participant row remains. | None; such a person may no longer open the thread. |
| `PostAsync` with `ReadableBy` posts into an agent thread of a person with a live role on its subject who never opened it; before, it answered `assistant-conversation-not-found`. | None, unless the application relied on the refusal; check the person's role itself before posting. |

## @newheap/platform-common: credentials go only to the configured API

`NhApiAuthInterceptor`, `NhActiveDivisionInterceptor` and `NhServerHttpInterceptor` now compare a
request with `apiBaseUrl` and `authApiBaseUrl` by origin and path instead of by string prefix. Both
URLs are resolved against the document as the browser resolves them, and a request matches when it
has the base URL's scheme, host and port and its path is the base path or continues it after a `/`,
in any letter case. An empty base URL, the default, stands for the application's own origin:
relative and same-origin requests still match, while a request to another origin no longer receives
the access token, `X-NH-ActiveDivisionId` or, in a server render, the forwarded session cookies, and
its `Set-Cookie` is no longer relayed. Before, an empty base matched every URL and
`https://api.example.com` also matched `https://api.example.com.attacker.example`. A relative base
such as `/api` now also matches absolute URLs on the application's own origin. With
`authType: 'header'`, the access token goes to requests under `authApiBaseUrl` always and to
requests under `apiBaseUrl` while `addAuthTokensToRequests` is `true`, as before.

| Breaking change | Required action |
|---|---|
| With an empty `apiBaseUrl` or `authApiBaseUrl`, requests to another origin no longer receive the access token, the active division or forwarded server cookies. | Set `apiBaseUrl` and `authApiBaseUrl` to the API's URL, for example `https://api.example.com`, when the API runs on another origin. |
| A base URL matches only at a path-segment boundary: `/api` no longer matches `/apiary` or `/api-docs`, and `https://api.example.com/v1` no longer matches `/v10`. | Configure the base path that the API's requests start with. |

## @newheap/platform-common: JSON-LD text cannot end its script element

`NhJsonLdComponent` (`<nh-json-ld>`) now writes every `<` in the serialized JSON as `\u003c` and
escapes U+2028 and U+2029. Before, it escaped only the exact lowercase `</script>`, so a string
value with `</SCRIPT>`, `</script >`, `</script/>` or `<!--<script>`, such as a review body passed
through `NhJsonLdService` or `NhPageSettings.jsonLdData`, ended the script element and the rest of
the value was inserted as markup. The script text still parses to the same values.

| Breaking change | Required action |
|---|---|
| The JSON-LD script text holds `\u003c` instead of `<`, and escapes U+2028 and U+2029. | None, unless a test compares the script's raw text; compare the parsed JSON instead. |

## @newheap/platform-common: stripHtml parses in an inert document

`NhMetaService.stripHtml` now parses its input in a document without a browsing context, created
with `DOMParser` (or, where that is missing as in a server render, by the application document's
`implementation`), instead of in an element of the application's document. Images in the markup
no longer load and their inline event handlers no longer run; the returned text is the same.

## NewHeap.Platform.AI.AspNet.Common: access contexts come from the person's own requests only

`NhAiAccessContext.IsPersonsOwnRequest(principal)` says whether an access source may record a request:
an authenticated principal without the marker claims of a delegated read token
(`nh-ai-delegated-read`, `nh-ai-delegated-read-action`), a delegated MCP token (`nh-ai-mcp-delegated`,
`nh-ai-mcp-audience`) or an impersonation session (`NhPlatformClaimTypes.ImpersonateOriginUserId`).
Such a request is an agent working for the person, or someone else acting as the person, so recording
it kept the person's rights alive past `MaxAge` and recreated them after sign-out. The principal factory
of `UsePrincipalsFromAccessSource` now refuses a context that holds one of these claims, whatever its
date. The sample's `SampleAccessSourceComposition` records only the person's own requests and forgets
the context in the person's own sign-out request (`UseSampleAccessContexts(signOutPath)`).

| Breaking change | Required action |
|---|---|
| A context whose claims hold a delegated read, delegated MCP or impersonation marker yields no principal, so background work that relied on it is refused with the usual codes. | Record only requests for which `NhAiAccessContext.IsPersonsOwnRequest` is true, and forget the person's context at sign-out; the next own request of the person records a context that lends again. |

## NewHeap.Platform.AI.AspNet.Common: headless gateway reads need a fresh access context

With `UsePrincipalsFromAccessSource`, a headless gateway read now asks the host's
`INhAiDelegatedReadCredentialIssuer` for a credential only while `INhAiPrincipalFactory` builds the
person's principal from the access source: without a context, with one older than `MaxAge`, or with one
that holds a derived-credential or impersonation marker, the read fails with
`api-bridge-unauthenticated` before the issuer is asked and no request is sent. Before, the read was
issued whenever the host's issuer issued, also for a person who had signed out. Hosts without an access
source keep their issuer's decision, as the built-in issuer with the person's sign-in state.

| Breaking change | Required action |
|---|---|
| Under `UsePrincipalsFromAccessSource` an agent's gateway read for a person without a fresh context fails with `api-bridge-unauthenticated`. | None; the read works again after the person's next own request. Keep refusing unknown or disabled people in a custom issuer. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: impersonation sessions grant nothing in the person's name

While a NewHeap impersonation marker (`NhPlatformClaimTypes.ImpersonateOriginUserId`) is present, the
assistant refuses the personal grants that would outlive the session with
`403 assistant-not-allowed-while-impersonating` (`NhAssistantErrorCodes.NotAllowedWhileImpersonating`):
consent to background use (`PUT mcp-connections/{serverId}` with `allowBackgroundUse: true`), starting a
personal OAuth connection (`POST mcp-connections/{serverId}/authorize`), completing one
(`POST mcp-connections/complete`, which ends the flow) and registering a browser for push
(`PUT notifications/push-subscription`). Withdrawing the consent, disconnecting and removing a
subscription stay allowed; their `McpBackgroundUseChanged` and `McpConnectionDisconnected` audit events
name the impersonating user in the new `NhAssistantAuditEvent.ImpersonationOriginActorId`. Organization
connections and hosts without NewHeap impersonation are unchanged. An application channel that issues
its own credential for the person, such as the sample's messaging link token (SPM-332), applies the same
rule: no issuance from an impersonation session, a binding to the person's security stamp so that a
password change or session revocation ends it, and no renewal beyond the presented credential's expiry.

| Breaking change | Required action |
|---|---|
| An impersonation session can no longer consent to background use, connect a personal MCP account or register a push subscription for the impersonated person. | None; the person does it in their own session. Map `assistant-not-allowed-while-impersonating` (403) where your own client shows assistant errors. |
| `NhAssistantErrorCodes` gains `NotAllowedWhileImpersonating` and `NhAssistantAuditEvent` gains `ImpersonationOriginActorId`. | None; an audit sink that stores events field by field may store the new value. |

## @newheap/platform-ai-chat: the impersonation refusal has a text

The bundled English and Dutch texts and `NH_ASSISTANT_SERVER_ERROR_CODES` include
`assistant-not-allowed-while-impersonating`, which the server answers when an impersonation session tries
to connect an account, allow background use or register push notifications for the impersonated person.
The mock API's status table maps it to 403.

| Breaking change | Required action |
|---|---|
| None. | None. |

## NewHeap.Platform.AI.Generators: every authorization declaration reaches the tool descriptor

Generated tools never pass the MVC authorization filter, so the descriptor's policy list is what the
tool gate, agent grants and the approver check enforce. The generator now reads every `IAuthorizeData`
declaration ASP.NET would apply to the tool method: on the method and the methods it overrides, and on
the tool set and its base types. It takes the policy from the named `Policy` argument, otherwise from
the constructor argument of `[Authorize("policy")]`. Before, only the named `Policy` of an
`AuthorizeAttribute` directly on the method or the tool set reached the descriptor, so a positional or
inherited policy, `Roles` and an `AuthorizeAttribute` subclass were dropped and every authenticated
caller ran the tool. Declarations the gate cannot enforce now fail the build with `NHAI018`. Tools that
declare only `[Authorize]` and `[Authorize(Policy = ...)]` on the method and the tool set keep their
contract hashes.

| Breaking change | Required action |
|---|---|
| A tool restricted with `Roles`, `AuthenticationSchemes`, an `AuthorizeAttribute` subclass or another `IAuthorizeData` attribute fails to build with `NHAI018`. | Register a named policy for the roles or schemes and declare `[Authorize(Policy = "...")]` instead. |
| A positional (`[Authorize("policy")]`), overridden or inherited policy is now enforced, and the tool's contract hash includes it. | Grant that policy to the people and agents who use the tool, and refresh pinned manifests or bound proposals of the affected tools. |

## NewHeap.Platform.Common and NewHeap.Platform.AI.AspNet.Mvc: gateway search never matches a redacted field

A search matches every field the API searches, so with `RedactResultFields` a search could still test
the values that redaction removes from the result. `NhAiBridgeQueryDescription.SearchFields` now
publishes the result field paths a search matches, and `describe-resource` shows them as
`query.searchFields`. The canonical collection provider takes them from the new
`NhCollectionContract.SearchPaths`: the `[Searchable]` members of the item type, including those of
nested searchable objects up to two levels, as collection processing searches them. With redaction
configured, the gateway `query` refuses a non-empty `search`, also with `countOnly`, before the HTTP
call on a resource whose search fields are unknown or include a redacted field, and
`describe-resource` reports that resource as `searchable: false`. Gateways without redaction, and
resources whose searched fields are all visible, search as before.

| Breaking change | Required action |
|---|---|
| With redaction, a search on a resource that marks a redacted field `[Searchable]`, directly or in a nested searchable object, fails with `api-bridge-validation`. | Remove `[Searchable]` from the redacted field, or let the model filter on visible fields. |
| With redaction, a resource whose collection contract provider sets no `SearchFields` is no longer searchable through the gateway. | Set `NhAiBridgeQueryDescription.SearchFields` to the fields the API searches. |
| A service that searches through `WithSearchable` selectors publishes only its `[Searchable]` members as search paths. | Describe such a resource with a collection contract provider that publishes the selector fields. |

## NewHeap.Platform.AI.Chat: retrieved context is bound to the profile that serves the turn

A turn retrieves up to the highest classification its agent's profile permits, but its skills and
fallback profiles may permit less. The highest classification among the retrieved items now raises
the classification of the turn's model calls, as the files the model reads already did. `use_skill`
refuses a skill whose profile chain does not permit it with `assistant-classification-not-permitted`,
also in a turn without files, and the profile resolution no longer falls back to a profile that does
not permit it: the turn fails instead of sending the items. Turns whose retrieved items stay within
the agent's configured classification are unchanged.

| Breaking change | Required action |
|---|---|
| A turn whose retrieved items are classified above the agent's classification is served only by profiles that permit them; it fails when none in the chain can serve it. | Let the agent's skill and fallback profiles permit the classifications its retrieval sources return, or classify those sources lower. |
| `use_skill` refuses a skill whose profile does not permit the retrieved context of the turn. | Give the skill a profile that permits it, or keep that work in the agent's own profile. |

## NewHeap.Platform.AI.Chat: a person's thread turn retrieves within the thread's subject

A person's message in an agent's subject thread retrieved context with the person's request
context, so the active division the client sent for the whole application decided what was
retrieved, and items of that division reached a thread that every reader of the subject reads. The
turn now keeps the person as the actor, so the context sources and their policy still decide for
that person, but retrieves within the execution scopes and scope values of the thread's subject and
its division, as the turn's tool calls get them, and without the capabilities the request derived
from its active division. A plain chat retrieves in the person's own context as before.

| Breaking change | Required action |
|---|---|
| A context source that serves a person's turn in a subject thread sees the subject and its division instead of the request's active division, and no capability grants. | Read the division from the context's scope value or execution scopes; let the source policy decide on the person and the subject rather than on capabilities. |

## NewHeap.Platform.AI.Chat.AspNet: one start rule for every run a person asks for

A message to a busy agent thread, a restart of a failed run (`POST runs/{runId}/retry` with `mode`
`restart`) and the generated request tool now create a run only for a person who may start the agent
by hand, as `POST threads/start` decides: the agent's required policy as it is in effect now, on an
object the participant role, on the whole application the agent's run and view policies, and only
administrators for an agent in shadow. These paths checked a thread or participant role only, so an
approver of the whole application without the run policy, or a participant of an agent in shadow,
could start runs that the start by hand refuses them. Hosts whose approvers pass the run policies of
their agents, and agents that are not in shadow, see no change on objects.

| Breaking change | Required action |
|---|---|
| A message to a busy agent thread from someone who may not start the agent answers `409 assistant-conversation-busy`, and nothing waits for them. | None; give the people who leave messages for runs the agent's required policy and, for the whole application, its run policy. |
| `POST runs/{runId}/retry` with `mode` `restart` refuses as `POST threads/start` does, for example `403 assistant-agent-forbidden`. | Give the people who restart runs the agent's required policy and, for the whole application, its run policy. |
| The request tool follows the same rule, also when its consultation names a `Policy` of its own: it answers `assistant-agent-forbidden` when the rule refuses and `assistant-consult-subject-unavailable` without a role for the agent's thread; on the whole application the run policy decides instead of the approve policy. | Give the people who request runs the agent's required policy and, for the whole application, its run policy. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: consultations follow the agent's required policy in effect

A consultation that names no asker policy of its own, in code (`Policy`) or as data (`askerPolicy`),
now checks at every call the consulted agent's required policy as it is in effect, so an administrator
who changes the required policy of an agent in code in its editor also changes who consults it. The
request tool follows the rule of a start by hand, which includes that policy. The generated tools keep
the policies of code, so a change that admits more people does not offer the tools to them.

| Breaking change | Required action |
|---|---|
| The consult tool answers `assistant-agent-forbidden`, without consulting, to an asker who fails the agent's required policy in effect after an administrator changed it. | None; give the consultation an asker policy of its own when its askers should differ from the agent's users. |

## NewHeap.Platform.AI.Chat: consultations about the whole application follow the agent's view policy

Consulting an agent about the whole application (`nh-system`) now resolves the asker's role for the
agent's thread, as the thread, its runs, its proposals and the request tool do: a person who reads the
whole application but fails the agent's view policy (by default its required policy) is refused before
a consultation is recorded or the consulted model runs. Consultations about the application's own
subject types are unchanged.

| Breaking change | Required action |
|---|---|
| A consultation of an agent about the whole application answers `assistant-consult-subject-unavailable` to an asker who fails the agent's view policy. | None; give the people who consult such an agent its view policy. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: shadow runs consult and request no live agent

A run in an agent's shadow thread never consults or requests another agent, as a hand-off from shadow
work to a live agent is already refused. The generated consult tool, which reads, is refused with
`assistant-consult-agent-unavailable`. The generated request tool is an effect, so the shadow run's
effect decision records the call as a shadow proposal that never executes before the tool runs (see "a
shadow run executes no effect"); the request tool refuses a call from a shadow run itself as well. The
other agent records no consultation, gets no thread, input or run, leaves no proposal for its subject's
approvers and notifies nobody, so evaluating an agent in shadow, a new data trigger included, never
changes live work. Runs in live threads and people's own chats are unchanged.

| Breaking change | Required action |
|---|---|
| The consult tool refuses a call from a run in a shadow thread with `assistant-consult-agent-unavailable`. | None; put the asking agent or its trigger live to let its runs consult other agents. |
| The request tool's call from a run in a shadow thread is recorded as a shadow proposal and starts no run of the other agent. | None; put the asking agent or its trigger live to let its runs request other agents. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: a claimed delivery stays deduplicated

A webhook delivery, or an event with a dedupe key, that joined a pending run or waited while the agent
was paused is now deduplicated also after the run that took it claimed it, so redelivering it starts no
second run. Such a run records the key of the event that started it only, and the deduplication looked
only at that key and at inputs that still waited. The input a delivery wrote now counts once a run took
it; the receipt of the redelivery reads `deduplicated` with that run. An input dropped without a run,
because its subject or the agent's owner was gone, does not count.

| Breaking change | Required action |
|---|---|
| A redelivery of an event that a run already took answers `deduplicated`, naming that run, instead of starting a new run. | None. |

## NewHeap.Platform.AI.Chat.AspNet: follow-up runs keep the initiator of the work they continue

A follow-up run after a proposal decision (`StartFollowUpRun`) now keeps the initiator of the run that
made the proposal, or none for trigger work and for a proposal a run placed at another object. Before,
it took the person who decided as its initiator, after a rejection, an executed or failed approval, an
approval refused because the subject changed and an application's execution report alike, so a
delegating agent's follow-up used the decider's rights and four eyes kept the decider instead of the
requester out of its proposals. The decider stays in the approval (`DecidedByActorId`) and its audit.

| Breaking change | Required action |
|---|---|
| The `proposal-decided` and `proposal-executed` runs, their trigger audit and the proposals they make name the proposal's initiator, or none, instead of the person who decided. | None; read the decider from the approval or its decision audit. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.AspNet.Mvc: an approver's decision lends no credentials

When an approver decides another person's proposal in an agent thread under the approval policy
(`POST conversations/{id}/approvals/{approvalId}/decide`), the person's request turn continues inside
the approver's request. That continuation now works for the person who asked as a headless turn does:
its MCP servers use only that person's consented connection or delegated token, never the approver's
connection or ambient token, and the approver is not its turn person (`INhAiTurnPerson`). The API bridge
sends the request's bearer token in an agent turn only inside the initiator's own request turn; in
another person's request and in a headless turn it sends nobody's token. Before, the continuation's MCP
calls and the bridge's calls were sent with the approver's credential while the gate authorized them with
the initiator's rights.

| Breaking change | Required action |
|---|---|
| A personal MCP server serves an approver's continuation of another person's turn only with that person's consent (`headlessUse` `personal`), otherwise it is left out as in a headless turn. | Let people allow background use of servers their agents need after an approval, or keep such tools out of proposals that others approve. |
| A bridge call in an agent turn that does not run in its initiator's own request fails with `api-bridge-unauthenticated` before a request is sent. | None; use a gateway read (`AllowGatewayReads`) for headless reads. |

## NewHeap.Platform.AI.Chat: an approver's decision continues with the asker's prompt context

The continuation of another person's request turn after an approver's decision under the approval policy
now composes its prompt for the person who asked, as the headless continuation of a run does: that
person's preferences, custom instructions, turn facts and name ("latest message from"), and that person's
files and daily budget. Before, the approver's own preferences and custom instructions steered a model
that acted with the asker's delegated rights. The decision, its stored decider and its audit event stay
the approver's.

| Breaking change | Required action |
|---|---|
| Turn context providers (`UseTurnContextProvider`) receive the asker's context for such a continuation, and `INhAiTurnPerson` names nobody in it. | None, unless a provider or tool reads the request's user directly; read the person from the context instead. |

## NewHeap.Platform.AI.Chat: a run lends no rights to another person's message

A turn of an agent run that answers another person's message, such as a message typed while the run
worked that the run claimed, no longer delegates from the run's initiator and makes no gateway reads
with the credential of its initiator or accountable account. The authors of the messages a turn answers
(`RequestedBy`) already kept personal MCP connections out of such a turn; delegated rights and gateway
reads now follow the same rule. A file that a person's waiting message only referenced, because the run
ended before its model read the files, opens later only in a turn of that person and is named with
`assistant-file-forbidden` in anyone else's. The run's own work, and a run of the person who wrote the
message, keep their rights.

| Breaking change | Required action |
|---|---|
| A delegating agent's turn that also answers another person's message calls tools only with the agent's grants or accountable rights; a gateway read there falls back to them as well. | None; the other person's message gets an answer within the agent's own rights, or that person starts the agent. |
| An earlier file of another person that nobody read with that person's rights is not opened in a later turn of someone else. | None. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: only a person who may act for an account binds it to an agent

The account an agent answers to lends its rights to the agent's headless runs. Setting it
(`SetServiceAccountAsync`, `POST admin/agents/{id}/service-account`), naming it in an agent's run settings
(`SaveRunSettingsAsync`, `PUT admin/agents/{id}/run-settings`) and in a package import
(`NhAssistantAgentPackageImportOptions.ServiceAccounts`) now requires that the person may act for it: the
account is that person, it is the agent's account in code, the person holds the platform permission
`nh.platform.auth.impersonate.allowed`, or the person passes every authorization policy of the agent's tools
that the account passes, evaluated against the agent without a division. The person is the request's user,
or the principal `INhAiPrincipalFactory` rebuilds; a change by application code with a technical actor id
that the factory does not know is the application's own decision, as before. Saving an account that is
already bound again checks only that it still exists.

| Breaking change | Required action |
|---|---|
| Binding an account that would lend the agent rights the administrator does not hold is refused with `403 assistant-service-account-not-permitted` (`NhAssistantErrorCodes.ServiceAccountNotPermitted`); the import refuses the part. | Let an administrator who holds the account's rights, or who may act as any user, bind it, or bind it from the application's code. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: live updates and notifications follow the read rule and the account

Live updates, Web Push and in-app notifications reach people outside any request of theirs, so the
assistant now applies the read rule of the conversation's snapshot when it sends them, per recipient,
instead of sending to every stored participant row. An agent thread reaches people whom the subject's
resolver gives a role now or who were invited, as the thread endpoints decide; a chat reaches its owner,
and a participant while the person, as the application's
`INhAiPrincipalFactory` rebuilds them, passes the agent's policy. Live updates reach participants only
on hub connections signed in for a tenant that reaches the conversation. A hub connection ends when the
token it was opened with expires, and when `INhAuthenticationSessionValidator` no longer accepts its
principal, for example after a password change. Nothing is delivered any more to a person the platform
no longer resolves, and forgetting a user removes what the assistant keeps to reach them.
`INhAiSubjectAccess.RefreshAsync` removes the participant rows of people whose role on the subject ended.

| Breaking change | Required action |
|---|---|
| Live updates of a conversation reach only the people its read rule admits when they are sent, decided again every ten seconds per person; a stale participant row of a thread reaches nobody. | None. |
| Participants receive live updates only on hub connections signed in for a tenant that reaches the conversation; a chat's owner and a person's own read position and removal still reach all their connections. | None, unless a client follows a conversation through a sign-in for another tenant; sign in for the conversation's tenant. |
| The assistant hub closes a connection when its token expires (`CloseOnAuthenticationExpiration`) and, within a minute, when the session validator refuses its principal. | None for `@newheap/platform-ai-chat`, which reconnects with a fresh token and reloads its snapshots; other clients reconnect on close and reload their snapshots. |
| Web Push about a turn, an invitation or an application message, and the in-app entry of an application message, go only to the people the conversation's read rule admits when they are sent. | None. |
| No Web Push, in-app or agent notification reaches a person for whom the principal factory, as the MCP owner sweep selects it, rebuilds nobody for the whole application, such as a deleted, blocked or locked-out account. | A host with its own `INhAiPrincipalFactory` returns a principal for every person who may still sign in when asked for `NhAiSystemSubjects.Global`, as the MCP owner sweep already expects. |
| `INhAiSubjectAccess.RefreshAsync` also removes the participant rows that a role created for people the refreshed members leave out and the resolver gives no role; such a person loses their read position and joins again when they read the thread with a role. Invitation and link rows stay. | None; keep calling `RefreshAsync` after the people on a subject change. |
| The assistant registers an `INhRetentionParticipant`, and `INhRetention` when none is registered: forgetting a user, as `NhUserManager.DeleteAsync` does, removes their push subscriptions, notification choice, MCP connection notices and participant rows. | None for hosts whose actor ids are user ids; a host with other actor ids removes those rows of a forgotten person in its own retention participant. |
