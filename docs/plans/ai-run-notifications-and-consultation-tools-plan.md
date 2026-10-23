# Run Notifications and Consultation Tools Plan

This document records two improvements of `NewHeap.Platform.AI.Chat` and `@newheap/platform-ai-chat`
that came from consumer feedback on NuGet 19.0.0 and npm 0.18.0: one notification per run instead of one
per object, and a tool selection per consultation. It is a plan, not usage documentation; the guidance
rules, the release notes and the sample cases are authoritative once it is built.

## Status

Decided by Lars on 2026-10-23: RN1 to RN7 and CT1 to CT5, every one as recommended. Not built yet.

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

### Notes for building

- Storage: a `ToolSelectorsJson` column on `AssistantAgentConsultationDefinition` with a migration for
  SQL Server and PostgreSQL; the selection is part of the consultation's version and audit.
- The consultation's tool contract does not change: the consult tool's input stays the same, so MCP
  clients and the attested export are not affected.

## Delivery

Two independent lanes in one merge train: A (notifications, no migration) and B (consultation tools, one
migration). Both edit the release notes, the guidance rules and the sample registry additively.
