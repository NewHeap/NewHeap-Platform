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
