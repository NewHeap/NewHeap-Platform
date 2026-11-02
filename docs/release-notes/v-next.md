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

## NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.SqlServer, NewHeap.Platform.AI.Chat.PostgreSql and NewHeap.Platform.AI.Chat.AspNet: alerts (FB87)

- An alert warns people about an object without a proposal: nothing is approved and nothing changes. Kinds are code:
  `NhAssistantBuilder.AddAlertKind(id, kind => ...)` with a display name key, an `NhAssistantAlertUrgency`, a title
  and text written from named parameters per culture, recipients (`NhAssistantAlertRecipients.SubjectResponsible`,
  `Explicit` and `SubjectApprovers`, combined with `Or`), an optional `RepeatAfter`, the default delivery
  (`NhAssistantAlertDelivery`: immediately, one daily digest with `WorkdaysOnly`, `ImmediatelyFrom` an urgency), what
  a person may change (`NhAssistantAlertChoices`) or `Required()`, and `PushShows` (`TitleOnly` by default). The
  delivery itself and a person's choices follow in a later version; the alerts are stored and planned now.
- An agent lists the kinds its rules may raise with `NhAssistantAgentRunBuilder.Alerts(...)`, which startup checks.
  Rules raise them with `NhAssistantRuleResult.WithAlerts` and resolve them with `ResolveAlerts`; a kind the agent does
  not list fails the run with `assistant-alert-kind-not-listed`. A paused or stopped agent raises nothing and a shadow
  agent or run records shadow alerts that reach nobody. The application's code raises and resolves alerts without an
  agent through `INhAssistantAlerts`, with `TaskResult` and the codes of `NhAssistantAlertCodes`.
- One open alert exists per kind, subject and key: raising it again updates it in place as its next revision, and it
  reaches people again only when its urgency rises or after `RepeatAfter`, never a person who dismissed it until the
  key changes. Resolving or an expiry closes it and takes back what did not go out; the run maintenance purges closed
  alerts after `NhAssistantLimits.AlertRetention` (30 days). `MaxAlertsPerRun` and `MaxAlertsPerSubjectPerDay` bound
  them. Every recipient is checked before anything reaches them: the principal factory still rebuilds them for the
  subject and they have at least `Reader` on it; `INhAssistantAlertPolicy` (`UseAlertPolicy`) replaces the selectors.
- The content-free audit kinds `AgentAlertRaised` (127), `AgentAlertResolved` (128), `AgentAlertDismissed` (129) and
  `AgentAlertRecipientDropped` (130), with `NhAssistantAuditEvent.AlertId`, `AlertKind`, `AlertRevision` and
  `AlertRecipientCount`. Forgetting a user also removes their alert deliveries.

| Breaking change | Required action |
|---|---|
| The `Alerts` migrations add the tables `AssistantAlert` and `AssistantAlertDelivery` on SQL Server and PostgreSQL. | Apply the assistant migrations for your provider, or keep `RunMigrations` on. An application without alert kinds sees no other change. |
| An audit sink that switches over every `NhAssistantAuditEventKind` sees four new kinds. | Handle or ignore `AgentAlertRaised`, `AgentAlertResolved`, `AgentAlertDismissed` and `AgentAlertRecipientDropped`. |
