# v-next

## NewHeap.Platform.Media.Core and NewHeap.Platform.Media.Http: moves, renames, tag updates and localizations are authorized

A file or folder that moves or gets another name is now also authorized where it lands, as a create:
`IAuthorizationModule` sees the same `ActionType.Create` decision a direct upload or folder create there
would get (the destination folder and file name for a file, the destination parent for a folder). This
applies to `UpdateFileAsync(Guid, FileModel)`, `MoveFileAsync`, both `RenameFileAsync` overloads and
`UpdateFolderAsync`; an update that keeps the folder and name asks only for `ActionType.Update`, as before.
A destination that already holds a file or folder with that name, as the storage's own path lookup finds
it, is refused with `NhMediaErrorCodes.FileNameExists` or `NhMediaErrorCodes.FolderNameExists`. The
`PUT {prefix}/file/{id}` and `PUT {prefix}/folder` endpoints return 401 when the module refuses the source
or the destination, instead of failing with 500, and 409 with the error code for an existing name.

`IMediaLibraryService.UpdateFileTagsAsync` now asks `IAuthorizationModule` for an `ActionType.Update` of the
file's path and name before it reads or writes the file, like the other file mutations. A refused caller
gets the same answer for an existing and a missing file. `POST {prefix}/file/tags` returns 401 when the
module refuses the update.

`IMediaLibraryService.LocalizeFieldAsync` now asks `IAuthorizationModule` for an `ActionType.Update` of the
file, with the localization's language, before it writes. Only `Title`, `Description` and `AltText` can be
localized, in any case: the service and `RelationalFileStructureStorage.LocalizeAsync` refuse any other
property name with `NhMediaErrorCodes.PropertyNotLocalizable`, and reading files in a language applies only
these three; a stored localization of another property is ignored by lists, searches and file reads.
`POST {prefix}/file/localize` returns the service's result: 204 when the localization was stored or
removed, 400 with the error code for another property, 404 for a missing file or localization and 401 when
the module refuses the update.

| Breaking change | Required action |
|---|---|
| A move or rename now needs create rights at its destination. | Grant `ActionType.Create` there in your `IAuthorizationModule` to principals who may move or rename into that folder. |
| A move or rename onto an existing name now fails instead of creating a second entry with that name. | Handle the failed result, or 409 from the HTTP endpoints, and choose another name. |
| Tag updates now need update rights on the file. | Grant `ActionType.Update` in your `IAuthorizationModule` to principals who may change a file's tags. |
| Localization writes now need update rights on the file. | Grant `ActionType.Update` in your `IAuthorizationModule` to principals who may translate a file's display strings. |
| The file and folder update endpoints and `POST {prefix}/file/tags` now return 401 when authorization is refused. | Treat 401 as a refused media mutation, as for the other media endpoints. |
| Only `Title`, `Description` and `AltText` can be localized; stored localizations of other properties are ignored. | Localize only these three; remove other stored rows from the `Localizations` table if you want them gone. |
| `POST {prefix}/file/localize` now returns 400 or 404 when the localization fails instead of 204. | Handle these responses; removing a localization that does not exist returns 404. |

## NewHeap.Platform.Media.Core and NewHeap.Platform.Media.Http: downloads served with a server-chosen type

`GET {prefix}/download` now opens the file with the new `IMediaLibraryService.OpenDownloadAsync`, which
reads the content through `DownloadFileAsync(path, fileName)`, so its authorization and any override of it
decide the download as before, and returns a `MediaFileDownload` with the stored file reference. The stored name chooses the content type from the fixed list in the new
`NhMediaContentTypes`; a name with another extension or none, and every type a browser runs scripts in
such as HTML, XHTML, XML and JavaScript, is served as `application/octet-stream`. Content without a stored
reference, for example from an override that serves a file the storage does not list, is an
`application/octet-stream` attachment. Every download carries `X-Content-Type-Options: nosniff`. Raster
images (PNG, JPEG, GIF, WebP, AVIF, BMP) are served inline with `Content-Disposition: inline`; SVG, PDF and
every other file are served as an attachment, both under the stored file name. `OpenDownloadAsync` has a
default interface implementation, so custom `IMediaLibraryService` implementations keep compiling.
`DownloadFileAsync` itself is unchanged.

| Breaking change | Required action |
|---|---|
| `GET {prefix}/download` now returns a listed content type or `application/octet-stream`, `nosniff`, and `Content-Disposition: attachment` for every file but a raster image. | Read the file name from `Content-Disposition` and the type from `Content-Type`; show SVG, PDF or other documents from a blob or a sandboxed viewer instead of navigating to the download URL. |
| The download endpoint calls `IMediaLibraryService.OpenDownloadAsync`, which `MediaLibraryService` implements through `DownloadFileAsync(path, fileName)`, so an override of that method still applies. | None. |

## NewHeap.Platform.AspNet.Proxy: host-relative redirect destinations stay on this origin

A redirect destination that starts with `/` is now validated as the path the `Location` header
carries, after dot segments (`..`, `.`, `%2e%2e`, `%2e`) are removed, and must then still start with
exactly one `/`. This applies when a rule is saved or activated and to every regex expansion. An
expansion whose path resolves to a network path (`//host`) skips redirect evaluation and the request
passes through, as other unsafe expansions do.

| Breaking change | Required action |
|---|---|
| An exact target or regex template whose path resolves to `//host` once dot segments are removed now fails validation; a snapshot with one no longer saves or activates. | Use the host's absolute URL, or a path that resolves to one leading `/`. |

## NewHeap.Platform.AspNet.Proxy: denied addresses no longer spend the administrator's login budget

`NhProxyAdministrationService.SignInAsync` now refuses a caller outside the IP allowlist before it takes a
permit from the shared login budget (`Administrator:LoginAttemptLimit` per `LoginAttemptWindow`), so only
attempts from allowlisted addresses spend that budget. Denied attempts are still audited as `IpDenied`, now
within a separate budget of `LoginAttemptLimit` rows per `LoginAttemptWindow`; further denied attempts in
that window are refused with 403 without an audit row, so the login audit stays bounded.

| Breaking change | Required action |
|---|---|
| Denied attempts beyond `LoginAttemptLimit` per `LoginAttemptWindow` no longer appear in the login audit. | None, unless monitoring counts `IpDenied` rows to measure outside traffic; count refused requests in the host's request logs instead. |

## @newheap/platform-ai-chat: the impersonation refusal has a text

The bundled English and Dutch texts and `NH_ASSISTANT_SERVER_ERROR_CODES` include
`assistant-not-allowed-while-impersonating`, which the server answers when an impersonation session tries
to connect an account, allow background use or register push notifications for the impersonated person.
The mock API's status table maps it to 403.

| Breaking change | Required action |
|---|---|
| None. | None. |
