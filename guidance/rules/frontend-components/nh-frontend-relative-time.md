---
id: nh-frontend-relative-time
title: "Relative times in the active language"
area: frontend
reference: frontend-relative-time
summary: "Show moments relative to now with nhRelativeTime in the active language, let it keep itself current, and pass an explicit now for deterministic tests and server renders."
sample-cases: ["SPM-156"]
public-symbols: ["NhRelativeTimePipe", "NhRelativeTimeOptions", "NhRelativeTimeInput", "nhFormatRelativeTime", "NhDatePipe", "NhDateTimePipe"]
skills: ["newheap-frontend-development"]
providers: ["frontend"]
risk: medium
---
## Preferred approach

Show how long ago or how soon something happens with `nhRelativeTime`, next to `nhDate` and
`nhDateTime` for absolute dates: `{{ task.lastModifiedDateTime | nhRelativeTime }}` gives
"3 minutes ago" or "in 2 days". `NhCommonModule` exports the pipe, and standalone components may
import `NhRelativeTimePipe` alone. It formats with `Intl.RelativeTimeFormat` in the language of the
module configuration, as `nhDate` does, falling back to the default language and then English, and
follows a language change. The unit goes from seconds to years (a month counts 30 days and a year
365 days) and the amount is rounded to the nearest whole unit, so 90 seconds read as "2 minutes"
and 47 hours as "2 days"; less than half a second reads as "now". Pass
`{ style: 'short' }` or `'narrow'` for compact columns. `numeric: 'auto'` allows words such as
"yesterday", which then means a rounded day (12 to 36 hours) ago rather than the previous calendar day, so keep the
default `always` unless that is acceptable. Put the absolute date in a `title` or `<time datetime>`
when the exact moment matters.

Without a `now` argument the pipe reads the clock and keeps itself current in the browser: it
marks its view for check when its text would next change, at most once a second while it counts
seconds and at least once an hour, through one timer outside Angular that every such pipe shares.
This also works in `OnPush` and zoneless components, so do not add an interval of your own to
refresh it. Pass `now` (a `Date`, epoch milliseconds or a string) for a fixed reference: in tests,
or in a server render that must match a known time. The text then changes only when the value,
`now` or the language changes, and no timer runs; no timer runs during a server render either. A
missing or invalid value renders nothing, and a missing or invalid `now` reads the clock. In code,
`nhFormatRelativeTime(value, now, language, options)` returns the same text, for example for an
`aria-label`, and `null` for an invalid value or `now`.

Send moments from the API with an offset, as `DateTimeOffset` serializes them: a string without
an offset is read as local time.

## Avoid

- Computing "x minutes ago" texts by hand or with a translation key per unit.
- Refreshing relative times with `setInterval` and `detectChanges` in the component.
- Asserting the text of a pipe without `now` in tests; pass a fixed `now` or use `fakeAsync`.
- Sending UTC moments without an offset and relying on them being read as UTC.

## Verification

Test with a fixed `now` in each supported language for the past and the future and for invalid
input. Test the self-refresh in an `OnPush` view with `fakeAsync` and `tick`, and destroy the view
at the end so no timer remains.
