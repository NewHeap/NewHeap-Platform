# Run Notifications and Consultation Tools Plan

This document records two improvements of `NewHeap.Platform.AI.Chat` and `@newheap/platform-ai-chat`
that came from consumer feedback on NuGet 19.0.0 and npm 0.18.0: one notification per run instead of one
per object, and a tool selection per consultation. It is a plan, not usage documentation; the guidance
rules, the release notes and the sample cases are authoritative once it is built.

## Status

Decided by Lars on 2026-10-23: RN1 to RN7 and CT1 to CT5, every one as recommended. Built on 2026-10-23
in merge train 16 (`feature/run-notification-bundles`, `feature/consultation-tool-selection`, with the
`ConsultationToolSelectors` migrations); Lars kept the lanes' own choices RN8 to RN17 and CT6 to CT13 on
2026-10-23.

## A. One notification per run

### Problem

A run that ends with proposals that wait tells its initiator once about the proposals in its own thread
(`NhAssistantRunEnding.NotifyWaitingProposalsAsync`), and then tells the approvers of every other object
it placed proposals at once per object (`NotifyPlacedProposalsAsync`). An approver of ten projects that
one run of the project follow-up touched receives ten notifications. Reminders (`ProposalExpiring`) and
escalations (`ProposalEscalated`) are even sent per proposal by the run maintenance. The notification
policy and the channels see one object per notification, so an application cannot bundle them itself.

### Decisions

| # | Decision |
|---|---|
| RN1 | **One notification per person per run.** Everything of one run that waits for a person's decision, in the run's own thread and at the objects it placed proposals at, reaches that person as one notification. |
| RN2 | **The default, not an opt-in.** Every application gets it from the next major release; the release notes list it as a breaking change for custom policies and channels. |
| RN3 | **The policy chooses per object, the channels receive a bundle of a new kind.** `INhAssistantAgentNotificationPolicy.SelectRecipientsAsync` keeps receiving one context per object, exactly as today, so custom policies keep working. The platform then groups the recipients and gives Web Push, the in-app channel and every `INhAssistantAgentNotificationChannel` one context per group of recipients with the same objects, of the new kind `RunProposalsWaiting`, whose new `Objects` list names each object (subject type and id, display name, conversation and number of proposals). A recipient only ever sees objects that wait for that recipient. The per-object `RunEndedWithProposals` no longer reaches the channels. |
| RN4 | **Counts, no names for several objects.** For one object the notification reads as today: the object's name as title and "N proposals wait for a decision". For several the title is "Assistant" ("Assistent") and the text "N proposals at M projects wait for your decision", with the subject type's plural name in the recipient's language, or "objects" when the types differ. |
| RN5 | **A conversation by default, the run for the application.** Clicking the Web Push opens the run's own thread for its initiator and otherwise the conversation of the object with the most proposals. The payload also carries the run id, and `NhAssistantPushService` gets `openRun$`, so an application can open its inbox page with `nh-assistant-agent-inbox` `[runId]`, which lists every proposal of the run the person may decide. The in-app message groups per run instead of per conversation. |
| RN6 | **Reminders and escalations are bundled too.** The run maintenance groups the due reminders and the escalations of one pass per run and recipient and sends one `RunProposalsExpiring` and one `RunProposalsEscalated` per group, with `Objects` as in RN3. |
| RN7 | **Evidence in SPM-302 and SPM-310.** The project follow-up's run that places proposals at several projects proves one notification per approver per run with the counts, that a recipient sees only its own objects, and one reminder per run; the management portal routes `openRun$` to its approval inbox with `[runId]`. |
| RN8 | Built by lane A (2026-10-23): **a group is the same objects with the same proposals and the same object to open**, so the run's initiator and another approver of the same objects get two contexts when the initiator opens the run's own thread and the other the object with the most proposals; every context names one conversation that all its recipients open; kept by Lars (2026-10-23). |
| RN9 | Built by lane A (2026-10-23): **every proposal notification is bundled, not only the end of a run**: an inline wait arrives as `RunProposalsWaiting` with one object, and a consultation's proposals at its subject as `RunProposalsWaiting` without `RunId`, so no channel ever receives a per-object kind (RN3); kept by Lars (2026-10-23). |
| RN10 | Built by lane A (2026-10-23): **the policy is asked exactly as before**: per object with `RunEndedWithProposals` when the run ends (the run's initiator only in the context of its own thread, none for placed objects) and per proposal with `ProposalExpiring` and `ProposalEscalated`, now with `ProposalCount` 1; an object's count for a recipient is the number of proposals the policy picked them for; kept by Lars (2026-10-23). |
| RN11 | Built by lane A (2026-10-23): **the bundled context takes the opened object's subject, display name, agent and member snapshot**, `InitiatorActorId` is the run's initiator (the asker for a consultation), `ApprovalId` and `ToolId` are set only for exactly one proposal, `ExpiresAt` is the earliest expiry, and `Objects` lists the run's own thread first and then the objects in the order the run made its proposals; on a tie for the most proposals the first object in that order opens; kept by Lars (2026-10-23). |
| RN12 | Built by lane A (2026-10-23): **reminders and escalations are bundled per pass only**: per run, without a run per consultation, otherwise per proposal; nothing is pulled forward from a later pass, so a run whose reminders fall due in different passes is reminded once per pass, and every reminder is still recorded per proposal; kept by Lars (2026-10-23). |
| RN13 | Built by lane A (2026-10-23): **texts**: one object as before, with "N proposals still wait for a decision." and "N proposals were escalated because nobody decided in time." when several wait there; several objects "N proposals at M projects still wait for your decision." and "... were escalated because nobody decided in time.", likewise in Dutch; a plural whose only capital is its first letter reads in lowercase ("Projects" becomes "projects", "CRM accounts" stays), and the channels resolve the plural from the registered subject types in the recipient's language, so the context carries no names beyond the objects'; kept by Lars (2026-10-23). |
| RN14 | Built by lane A (2026-10-23): **Web Push**: only the three run kinds carry `runId`; their push kinds are `agent-run-proposals`, `agent-run-proposals-expiring` and `agent-run-proposals-escalated` (the first with high urgency), and their tag and `Topic` are per run, so runs about one object do not replace each other while a reminder replaces the run's earlier notification; kept by Lars (2026-10-23). |
| RN15 | Built by lane A (2026-10-23): **`openRun$` replaces the panel only while the application subscribes**: a click with a run emits it on `openRun$` when it has a subscriber and opens the conversation otherwise, with the public `openNotification(conversationId, runId?)` deciding; a click that opens a new window uses `#nh-assistant-run=<runId>.<conversationId>` (`NH_ASSISTANT_RUN_FRAGMENT`), so the application subscribes in its shell. No configuration option; kept by Lars (2026-10-23). |
| RN16 | Built by lane A (2026-10-23): **the in-app group per run (`assistant-agent:run:<runId>`) applies to the three run kinds with a run only**; a consultation's proposals and every other kind keep the group per agent thread, and an escalation of a run is a warning; kept by Lars (2026-10-23). |
| RN17 | Built by lane A (2026-10-23): **sample**: the agent-run sample host runs the maintenance every second (`MaintenanceInterval`) so the SPM-302 test reads the reminder right after moving the clock a day; the management portal's shell subscribes to `openRun$` and opens the intake reviews page with `?run=`, whose inbox then drops its agent and object type filters and offers "Show all proposals"; kept by Lars (2026-10-23). |

### Notes for building

- The policy is still asked per object, with the context of today (kind `RunEndedWithProposals`,
  `ProposalExpiring` or `ProposalEscalated`); only the delivery is bundled. Shadow work still notifies
  nobody, and the whole application's objects still reach only people who see them.
- The new kinds are new values of `NhAssistantAgentNotificationKind`, with texts in English and Dutch for
  Web Push and the in-app channel; their push kinds are new values of the push payload.
- No schema change: the bundle is computed when the run ends and within one maintenance pass.

## B. A tool selection per consultation

### Problem

A consulted agent works with its own tool selection (`NhAssistantTurnRunner.SelectToolsAsync`), filtered
only by effect, route and depth. In a consultation about a project an agent therefore also sees tools
that belong to other kinds of objects. Neither `AllowConsultation` nor a consultation defined as data can
narrow the tools; skills can (`Skill(...).Tools(...)`).

### Decisions

| # | Decision |
|---|---|
| CT1 | **An explicit selection that only narrows.** `NhAssistantConsultationBuilder.Tools(params string[] toolSelectors)` in code and `ToolSelectors` on a consultation defined as data, by id or `prefix.*`, each covered by the agent's own selectors, as a skill's tools are. The consulted model sees only the selected tools; the existing rules on effects, routes and depth still apply. |
| CT2 | **No selection keeps every tool of the agent**, as today, so existing consultations do not change. A consultation without tools stays `MaxToolCalls = 0`. |
| CT3 | **The selection also governs further consultations.** A consultation tool (`assistant.consult.*`, `assistant.request.*`) is offered only when the selection names it; the depth limit still applies. |
| CT4 | **Data fails closed.** Saving refuses a selector outside the agent's selectors with the field error `toolSelectors: not-allowed`. When the agent's selectors change later, a selector they no longer cover drops on load with the problem `tools-not-allowed` (as AC11 degrades per part); when every selector dropped, the consultation offers no tools, never all of them. In code, a selector the agent's selectors do not cover fails startup, as for skills. The administration's consultation form offers the agent's tools as check boxes under Advanced; none checked means every tool of the agent. |
| CT5 | **Evidence in SPM-278 and SPM-311.** In code, the consultation about projects names its tools and a test proves a tool outside the selection is neither offered nor executed when the model names it; as data, an administrator chooses the project starter's tools for its consultation, with the mock API, the form and the CT4 problem. |
| CT6 | **A call outside the selection is refused, not only hidden.** Built by lane B (2026-10-23): the tool call interceptor refuses a consulted agent's call of a tool the selection does not name with the new code `assistant-consult-tool-not-selected` (HTTP 400; the agent's memory functions excepted), next to `assistant-consult-read-only`, because the offered list is the only other guard; kept by Lars (2026-10-23). |
| CT7 | **No selection and an empty one mean the same.** Built by lane B (2026-10-23): `ToolSelectors` `null` or `[]` in a request is no selection (every tool of the agent), as "none checked" in the form, and is stored as `NULL`; only a selection whose every selector dropped on load is empty and offers no tools, so the two states never mix; an invalid selector is `toolSelectors: invalid`; kept by Lars (2026-10-23). |
| CT8 | **The data DTO reports the selection in effect.** Built by lane B (2026-10-23): `NhAssistantDataConsultationDto` has `ToolSelectors` as saved and `ToolSelectorsInEffect` (`null` every tool, `[]` none, also while out of effect), as `Routes` and `RoutesInEffect`, so the administration shows the fail-closed state without recomputing coverage; kept by Lars (2026-10-23). |
| CT9 | **No new audit field.** Built by lane B (2026-10-23): a change of the selection is the consultation's next version, which `AdminAgentConsultationDefinitionChanged` and `AgentConsulted` already record, as for routes and limits; selectors are identifiers, not text that needs a hash, so `NhAssistantAuditEvent` and its allow-list stay unchanged; kept by Lars (2026-10-23). |
| CT10 | **A corrupt stored selection takes the consultation out of effect.** Built by lane B (2026-10-23): a `ToolSelectorsJson` that is no list of valid selectors makes the consultation `invalid`, as corrupt routes do, instead of dropping selectors; only selectors the agent's selectors no longer cover drop with `tools-not-allowed`; kept by Lars (2026-10-23). |
| CT11 | **The form offers selectors, as a skill's form does.** Built by lane B (2026-10-23): the check boxes are the agent's own selectors with what each matches in the tool catalog, followed by saved selectors that are none of them (a single tool under a prefix saved through the API, or one the agent no longer covers, shown with the reason and only to clear); narrowing a prefix to single tools stays with code and the API; kept by Lars (2026-10-23). |
| CT12 | **MCP tools need no selection rule.** Built by lane B (2026-10-23): a consultation turn loads no MCP tools today (its MCP tool set is empty), so a selection covers catalog and consultation tools only and `mcp.*` gets no special handling; kept by Lars (2026-10-23). |
| CT13 | **The sample's selections.** Built by lane B (2026-10-23): the intake reviewer's consultation selects only `projects.request-review` (its intake note saves a file, which consultations refuse, and its status and task tools stay with its runs); the project starter's selects `projects.change-status`, and the SPM-311 test narrows and restores the starter's selectors to show `tools-not-allowed` with no tools offered; kept by Lars (2026-10-23). |

### Notes for building

- Storage: a `ToolSelectorsJson` column on `AssistantAgentConsultationDefinition` with a migration for
  SQL Server and PostgreSQL; the selection is part of the consultation's version and audit.
- The consultation's tool contract does not change: the consult tool's input stays the same, so MCP
  clients and the attested export are not affected.

## Delivery

Two independent lanes in one merge train: A (notifications, no migration) and B (consultation tools, one
migration). Both edit the release notes, the guidance rules and the sample registry additively.
