# v-next

## @newheap/platform-common: debounced search fields

- `onDebounce` of `[ngModel][nhDebounce]` and of `nh-search-input` no longer reports the initial `ngModel`
  value. `preDebounce` and `onDebounce` shared one guard that `preDebounce` consumed, so `onDebounce`
  reported the initial value 500 ms after the field appeared and a page loaded its data twice. A value
  typed while the initial value is still within the debounce time is reported.
- A value that still waits for the debounce time when the field is destroyed is no longer reported while
  the field is destroyed.
- `nhButtonDebounce` no longer writes `tap` to the console for every click.
- `nh-search-input` has an accessible name: the new `label` input, by default the translated
  `general.search` text that its placeholder shows. The new optional `inputId` puts an id on the input.
  Its look, `value` and `onDebounce` are unchanged.

| Breaking change | Required action |
|---|---|
| `onDebounce` of `[ngModel][nhDebounce]` and `nh-search-input` no longer reports the initial value. | Load the initial data yourself, for example in the collection's initial load, instead of from the first `onDebounce`. |
| A value that waits for the debounce time is dropped when its field is destroyed. | Save such a value from `preDebounce` or on blur when it must survive closing the field, as in an autosave field. |
| `nh-search-input` sets `aria-label`, which wins over a `<label>` that wraps it or points at it. | Pass that label's text through `[label]`. |

## @newheap/platform-common: modals close on browser back and forward navigation

`NhModalService` now follows the router: a `NavigationStart` with the `popstate` trigger, which the
browser's back and forward buttons cause, dismisses every open modal as Escape does, the topmost
first, so cleanup and the `closed` event run. Content with the new optional
`INhModalComponent.onModalBackNavigation` decides itself; other content with `onModalEscape` gets that
call, and other content closes as the close button does. Mutating and confirmation content call
`onModalEscape`, and therefore `closeDialog`, but stay open while their save or confirmation runs. A
modal opened with `closeable: false` stays open, and navigation started from code keeps every modal
open. `NhModalOptions.closeOnBackNavigation` (default `true`) opts a modal out.

| Breaking change | Required action |
|---|---|
| An open modal closes on browser back or forward navigation instead of staying open over the next page. | Pass `closeOnBackNavigation: false` in `NhModalOptions` for a modal that must survive the navigation, and host it in the root view container (`NhModalService.setViewContainerRef`). |
| A `closeDialog` or `onModalEscape` override of modal content now also runs on browser back or forward navigation. | Override `onModalBackNavigation` when back navigation needs other handling than Escape. |

Fixed in the same release, without required action:

- `NhModalService.confirm` and `confirmDialog` now run the passed `onConfirm` on "Yes" (it may return a promise), show its error in the dialog and close the dialog after success; "No" closes the dialog, and `onClose` still runs once when it closes. `NhModalConfirmComponent` emits `confirmed` after `onConfirm` completed without an error, and ignores clicks while its confirm or cancel delegate runs.
- A modal that a route change destroys with its hosting page now leaves `NhModalService.modalRefs`, so `modalRefsOpen` reports `false` once no modal is open, and it no longer repeats `false` for one closing modal.

## @newheap/platform-common: context menu

`NhContextMenuService` renders item titles as text, opens a WAI-ARIA menu that
works with the keyboard and returns focus when it closes, and keeps the menu
inside the viewport of a scrolled page. `NhContextMenuItem` gains `iconClass`,
`disabled` and `checked`, and `NhContextMenu.fromElement` anchors a menu to the
button that opens it. A rejected `onClick` closes the menu and reaches the
`ErrorHandler`.

| Breaking change | Required action |
| --- | --- |
| `NhContextMenuItem.title` is rendered as text, no longer as HTML. | Remove markup from titles; put icons in `iconClass` (rendered as `<i class="..." aria-hidden="true">`). |
| The menu uses `position: fixed` with `x`/`y` as viewport coordinates. | Pass `clientX`/`clientY` (or `NhContextMenu.fromEvent`), not `pageX`/`pageY` or coordinates with the scroll offset added. |
| Focus moves into the menu on open and returns to the previously focused element on close; a pointer down outside closes it instead of a click. | Do not move focus from code right after `open`; drop a `stopPropagation` that only kept a menu opened from a click handler open. |

## @newheap/nh-toastr: an action button inside a toast

- `NhToastrConfig.action` (`NhToastrAction`: `label`, `value`, `dismiss`) renders a real `<button type="button">`
  in the toast, for example "Undo". Pass it per toast through the `override` parameter. Clicking it emits `value` on
  the toast's `onAction` through the new `NhToastrService.runAction(toastId)` and dismisses the toast unless
  `dismiss` is `false`; the click never counts as a tap, so `onTap` and `tapToDismiss` are not involved.
- The timeout now also pauses while keyboard focus is inside a toast, not only while the pointer is over it, and
  resumes once neither is the case, so keyboard users can reach the toast's buttons. Switching to another window
  while a toast button has focus keeps that toast paused until focus moves elsewhere on the page.
- Theme the button with the `--nh-toastr-action-*` custom properties and `--nh-toastr-actions-justify` /
  `--nh-toastr-actions-margin-top`; its focus-visible outline uses `--nh-toastr-focus-color`.
- `NhToastrComponent` gains the `actionTriggered` output, which `NhToastrContainerComponent` connects to `runAction`.
  Existing toasts without `action` render as before.

## @newheap/platform-common: form dropdown values and custom form controls

`nh-form-dropdown` keeps a value that the form sets. An external `setValue` no longer reverts, or
flips between the old and the new value on every change detection, when the selected option resolves
synchronously: the dropdown takes a new value only from a user selection, check all or uncheck all.
Single-select keeps the options `0` and `false`. Lazy options keep their `image`, and the new optional
`NhFormDropDownSettings.classesGetLambda` sets the CSS classes of an option in every loading mode. Loaded
options render inside an `OnPush` parent, Escape closes an open list and returns focus to its toggle
button, and the unused stylesheet whose `app-form-dropdown` selector never matched is removed, so
layouts do not change.

| Breaking change | Required action |
|---|---|
| `AbstractValueAccessor.writeValue` no longer calls `onChange`, so `setValue`, `reset` and an `ngModel` binding no longer emit a second `valueChanges`, mark the control dirty or emit `ngModelChange` at initialization; this includes `nh-form-dropdown` and `nh-page-size`. | Remove workarounds for the echo, such as skipping the first change or resetting the dirty state, and start initial work from initialization instead of the first `ngModelChange`. |
| `nh-form-dropdown` emits `closed` after the chosen value with `closeOnSelect`, and `toggleDropdown()` called from a click outside the dropdown opens the list once that click has finished, instead of opening and closing at once. | Read the chosen value in `closed`, and handle `opened` instead of acting right after such a `toggleDropdown()` call. |

## @newheap/platform-common: HTML sanitizer for rich text

The new optional entry point `@newheap/platform-common/sanitize` sanitizes untrusted HTML, such as
pasted e-mail or Word content, with DOMPurify: `NhHtmlSanitizer` (`sanitize`, `sanitizeToSafeHtml`,
`isSupported`) and the standalone `nhSanitizeHtml` pipe (`NhSanitizeHtmlPipe`) for `[innerHTML]`.
The strict default allow-list keeps the structure, links and inline formatting of rich text without
`class`, `id`, `style` or images; scripts, styles, event handlers, frames, embedded objects, forms,
SVG and MathML are always removed. Links keep only `http`, `https`, `mailto` and `tel` URLs and get
`rel="noopener noreferrer"` and `target="_blank"`. Configure it with `NhHtmlSanitizerOptions` through
`provideNhHtmlSanitizer(...)` (`NH_HTML_SANITIZER_OPTIONS`) at the root or in a component, or per
call, and extend `NH_HTML_SANITIZER_DEFAULT_TAGS`, `NH_HTML_SANITIZER_DEFAULT_ATTRIBUTES` or
`NH_HTML_SANITIZER_DEFAULT_URI_SCHEMES`. Without a DOM, during server-side rendering, it returns the
HTML escaped as text.

`dompurify` `^3.4.0` is a new optional peer dependency: install it when you import the entry point;
applications that do not import it need not install it. `safeHtml` is deprecated and unchanged: it
calls `bypassSecurityTrustHtml`, which bypasses Angular's sanitizer, so keep it only for HTML the
application wrote itself and move other content to `nhSanitizeHtml`.

| Breaking change | Required action |
|---|---|
| npm checks the new optional peer `dompurify` `^3.4.0` when an application installs `dompurify` itself, and refuses a version below 3.4. | Upgrade the application's `dompurify` to `^3.4.0`, or remove it when nothing else uses it. |

## @newheap/platform-common: relative times

`nhRelativeTime` (`NhRelativeTimePipe`, standalone and exported by `NhCommonModule`) shows a moment
relative to now with `Intl.RelativeTimeFormat` in the active language of the module configuration,
as `nhDate` uses it: "3 minutes ago", "in 2 days", "now". The unit goes from seconds to years and
the amount is rounded to the nearest whole unit; `NhRelativeTimeOptions` sets `style` and `numeric`.
Without a `now` argument the pipe keeps itself current in the browser through one shared timer
outside Angular, also in `OnPush` and zoneless components; with `now`, and during server-side
rendering, no timer runs. A missing or invalid value gives `null`. `nhFormatRelativeTime(value, now,
language, options)` formats the same text in code.

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

## NewHeap.Platform.Common and NewHeap.Platform.AspNet.Common.PostgreSql: case-insensitive collection search on PostgreSQL

| Breaking change | Required action |
|---|---|
| Collection search and the `LIKE` filter on a context configured with `UseNewHeapPostgreSql` now match case-insensitively (`ILIKE`), as on SQL Server's default collation, so they can return more rows. | To keep case-sensitive matching, call `postgres.UseNewHeapCaseSensitiveCollectionTextMatch()` in the `UseNewHeapPostgreSql` configure action. |

- Collection processing matches text with `EF.Functions.Like(value, NhCollectionTextMatch.Pattern(pattern))`. SQL
  Server and contexts without a NewHeap PostgreSQL registration keep their `LIKE`; `%` and `_` keep their meaning
  on every provider. Mark a pattern with `NhCollectionTextMatch.Pattern` to match text the same way in a query of
  your own. A context configured with plain `UseNpgsql` keeps the case-sensitive `LIKE`.

## NewHeap.Platform.AI.Common, NewHeap.Platform.AI.AgentFramework and NewHeap.Platform.AI.Chat: diagnosable model provider failures

- A model call that fails at the provider still returns `ai-model-dependency-unavailable`, and now says why
  without content: `NhAiModelProviderFailure.Of(result)` on the executor's result or failed stream completion,
  and `NhAiUsageRecord.ProviderFailure`, give the HTTP status, the provider's error code, the exception type
  and an `NhAiModelProviderFailureKind` (`Transient`, `Authentication`, `Request` or `Unknown`). The executor
  logs one warning with them and tags the `ai.model.invoke` activity (`http.response.status_code`,
  `newheap.ai.provider.code`, `newheap.ai.provider.failure_kind`); the provider's message stays out.
- The executor reads `HttpRequestException` itself. For an SDK that throws its own exception type, such as
  the OpenAI client's `ClientResultException`, register an `INhAiModelProviderFailureReader` with
  `AddModelProviderFailureReader<TReader>()`; a status it returns also decides the retry of a retry-eligible
  profile. SampleProjectManagement shows one for the OpenAI client (SPM-232).
- `NhAiAgentModelCallException` gains `ProviderFailure` and a constructor with an inner exception; the
  assistant logs a turn that ends on a failed model call with the code, status, provider code and kind.

| Breaking change | Required action |
|---|---|
| `NhAiAgentModelCallException` for a call that failed at the provider now keeps the provider's exception as `InnerException`, whose message can carry provider text. | Where provider text must not reach logs, log `Code` and `ProviderFailure` instead of the exception. |

## NewHeap.Platform.AI.Common: a busy tool waits for a free slot

- `INhAiToolConcurrencyLimiter.WaitToAcquireAsync` lets a limiter wait for a free slot. The default in-process
  limiter queues calls per partition, first come first served; its default implementation on the interface
  asks `TryAcquireAsync` once, so a custom or distributed limiter keeps its behavior until it implements it.
  NewHeap ships no distributed limiter.

| Breaking change | Required action |
|---|---|
| A tool call whose `MaxConcurrency` bound is full now waits for a free slot until the tool's `Timeout` or the invocation context's `Deadline`, on the application's `TimeProvider`, instead of failing at once with `ai-tool-concurrency-limited`, which it now gets only when that wait ends. | Where a caller relied on the immediate refusal, give the call a shorter `Deadline` or the tool a shorter `TimeoutSeconds`; in tests, move a test `TimeProvider` past the timeout instead of waiting. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: alerts wait for a person's fresh access context

- In a host whose principals come from the application's access source (`UsePrincipalsFromAccessSource`), a
  recipient who lacks the read role while the application holds no fresh context of their rights is no
  longer dropped with `no-access`. The raise plans the person, the delivery stays pending and a later
  maintenance pass checks the person again, about every five minutes. It goes out once the person is back,
  is taken back as expired when the alert expires first, or is dropped with the new reason
  `NhAssistantAlertDropReasons.NoFreshContext` (`no-fresh-context`) once `NhAssistantLimits.AlertFreshContextWait`
  (24 hours by default, from one minute to 30 days) passed since it fell due. A person whose fresh rights give
  no read role is still dropped at once with `no-access`. A waiting delivery carries the reason code
  `assistant-alert-awaiting-fresh-context`, keeps the moment it fell due and counts no delivery attempt.

| Breaking change | Required action |
|---|---|
| An alert recipient without a fresh access context is no longer dropped with `no-access`; the delivery waits and is dropped with `no-fresh-context` after `NhAssistantLimits.AlertFreshContextWait`. | None. Set `AlertFreshContextWait` to change the wait, and expect `no-fresh-context` where you read drop reasons or the `AgentAlertRecipientDropped` audit. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: one maintenance pass on demand

- `INhAssistantMaintenance.RunOnceAsync`, registered by `AddNewHeapAssistant`, runs one pass of the run
  maintenance now: proposal expiry, reminders and escalations, executions and overdue reports, and in a host
  with alert kinds alert expiry, purging and delivery. It is the hosted maintenance's own instance, whose
  passes no longer overlap, so when it returns a full pass ran after the call. Tests move the application's
  clock and run a pass instead of waiting for `NhAssistantLimits.MaintenanceInterval`.

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: boards count the work of agents that work on objects

- On `GET boards/{group}`, an agent that works only on objects now shows `openProposals` and `latestRun` for
  its threads at the objects the person reads by the member snapshot, in the person's tenant and, on a
  division's board, in that division; before, they were `0` and `null`. `isNew` follows them.
- `NhAssistantAlertKindPreferenceDto.Channels` documents that it is the person's choice whether or not the
  host offers the channel; a client names only the channels that are also in `AvailableChannels`.

## @newheap/platform-ai-chat: the alert channel hint names only offered channels

- The notification preferences' hint of an alert kind the person may not choose no longer names Web Push, or
  in-app notifications, when the server does not offer that channel (`availableChannels`).

## @newheap/platform-common: list building blocks

New standalone components for collection pages, with English and Dutch texts under `nh-common.` that an
application's translations override, and the `--nh-color-*` custom properties for theming:

- `nh-pager` (`NhPagerComponent`) pages a `CollectionHttpResponse` or any `NhPagerSource` and emits
  `NhPagerPageChange` for `NhCollectionTypeBaseComponent.setPage`: a named `<nav>` with `aria-current`, a
  polite page announcement, arrow-key, Home and End focus, and "Page 2 of 5" on phones.
- `nh-empty-state` (`NhEmptyStateComponent`) with the `no-data`, `no-results` and `error` variants, an
  `nhEmptyStateIcon` slot and projected actions.
- `nh-skeleton` (`NhSkeletonComponent`) with `line`, `block`, `avatar` and `row` shapes, hidden from assistive
  technology, one polite loading status and a shimmer only without a reduced-motion preference.
- `nh-background-operations-badge` (`NhBackgroundOperationsBadgeComponent`) counts the active operations of
  `NhBackgroundOperationStore.watchAll()` in the browser, announces changes and emits `openRequested`.
- `NH_COMMON_ICONS` maps their icons to the application's icon library; `nhBackgroundOperationIsActive` tells
  whether a status is unfinished.

No breaking changes: existing components, `NhCommonModule` and their texts are unchanged.

## @newheap/platform-ai-chat: ordered list start and table column alignment in Markdown

- Assistant messages and `nh-assistant-document` now keep `start` on an ordered list that does not begin at 1
  (`3.` renders as 3) and `align` on the cells of an aligned table column. DOMPurify used to drop both because it
  checked their plain values against the link pattern. The set of allowed attributes is unchanged.

## @newheap/platform-select (new package): accessible combobox and multiselect

- `nh-combobox` chooses one value and `nh-multiselect` several, shown as removable chips. Both are
  standalone, `OnPush`, signal-based form controls (`formControl`, `formControlName`, `ngModel`) or work
  with `[value]` and `(valueChange)`, in the ARIA 1.2 combobox pattern with a listbox popup.
- A value the form writes is never reported back as a change, leaving the field marks it touched, the
  disabled state follows the form, and every primitive value is kept, including `0`, `false` and `''`;
  `compareWith` compares object values.
- Always a search field: local filtering on label and description, or a server `search` after
  `minQueryLength` characters (down to `0`) with a debounce, loading, empty and error states and a retry.
  `create` adds a "Create" row for text that matches no option; `noneLabel` and `noneValue` add an explicit
  empty choice to the combobox. Options carry `description`, `disabled`, `icon`, `image` and `classes`.
- Arrow keys, Home, End, Enter, Escape and Tab work as the pattern prescribes; Escape in an open list is
  marked as handled, so a surrounding `NhModal` stays open. Backspace in an empty multiselect search removes
  the last chip, and changes are announced to assistive technology.
- Texts are inputs with English defaults; `provideNhSelect` registers application-wide texts, also as a
  signal that follows the language, and an `errorText` for failed searches. Theme with the `--nh-select-*`
  custom properties; light and dark defaults, `size="compact"` and reduced motion are built in. The only
  peer dependencies are Angular and rxjs.
- `nh-form-dropdown` of `@newheap/platform-common` remains available as the legacy alternative.

## @newheap/platform-ai-chat: comboboxes from @newheap/platform-select

The searchable choices of the assistant administration and of the agent runs are now `nh-combobox` of
`@newheap/platform-select`, with the assistant's texts and its translated error codes. Choosing the
instructions text that is already chosen no longer reads it again; the read button next to it does.

| Breaking change | Required action |
|---|---|
| `@newheap/platform-ai-chat` has the new peer dependency `@newheap/platform-select`. | Install it next to the assistant: `npm install @newheap/platform-select`. |
| The internal exports `ɵNhAssistantPickerComponent` and `ɵNhAssistantPickerOption` are removed. | Use `NhComboboxComponent` and `NhSelectOption` from `@newheap/platform-select`. |
