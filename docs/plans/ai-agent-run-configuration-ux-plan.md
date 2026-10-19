# Agent Run Configuration UX Plan

This document plans the usability pass over the run configuration of agents in the
administration of `@newheap/platform-ai-chat/admin`, delivered in 15.0.0 by the
[agent run configuration plan](ai-agent-run-configuration-plan.md). It was checked against `main` at
`7298d9c8` on 2026-10-19.

## Status

Accepted on 2026-10-19 after a review of the screen in a consuming application; U1 to U5 implemented. The
decisions below are the defaults chosen for that review and can be adjusted while it is built.

| Phase | Status |
| --- | --- |
| U1 | Implemented: consistent controls, errors at their fields and next to the actions, the internal searchable picker, layout. |
| U2 | Implemented: `INhActorSearch` with the NewHeap Identity search, `GET admin/accounts` and the account picker. |
| U3 | Implemented: `HideFromChat()`, `offeredInChat`, the `AgentChatAvailability` migrations and the editor's use choice. |
| U4 | Implemented: policy summaries, the regrouped run settings and the guided setup. |
| U5 | Implemented: mock API, the sample's runtime agent and real administration page (SPM-298), guidance and release notes. |
| Review | Implemented after a walk-through in the sample: translated names and descriptions of agents in code (also in the editor, which keeps their keys), readable skill names and translatable skill descriptions and policies, people by name, notes instead of reasons, "Cancel" instead of "Keep", tool descriptions in the skill's tool choice, and singular counts. The sample got an optional real model and a connection state that follows every API answer. |
| U6 | Proposed: a flow view of an agent (triggers, skills, tools, approval, account) as an overview with entry points to the forms; not a free node editor. It follows the [agents working on application objects plan](ai-agent-work-on-objects-plan.md), which adds what an agent works on and its results to the flow. |

## Problem

The review found that the run configuration works, but is hard to use:

- **Controls look different.** Number fields have no styling of their own and look like browser
  defaults next to the styled text fields and selects.
- **Errors are not seen.** A refused save shows its message at the top of the screen, above the
  form the administrator is looking at. Only some fields show their own error; numbers, budgets and
  several selects only turn red, and nothing is checked before the request is sent.
- **Choices cannot be searched.** Rule handlers, approval policies, profiles, events, subject sets
  and skills are plain selects. Their options read `name · description`, which a select cuts off.
- **The technical account is a GUID.** The administrator has to type the id of an account.
- **Every agent is offered in the chat.** An agent that only works on triggers appears in the
  agent picker of every user who may use it.
- **The words do not explain the choices.** "Rules", "approval policy", "triage profile", "reads
  with the account's rights" and the gateway and delegation options say what they are called, not
  what they do. Run settings, skills and triggers are separate forms that the administrator has to
  find and fill in the right order before anything happens.
- **The layout overflows** in narrower hosts: long option texts and unbroken values make the form
  wider than its container.

## Decision record

| # | Decision | Reason |
| --- | --- | --- |
| UD1 | **One internal searchable picker** replaces the selects of the run configuration: it filters on name and description, shows both on two lines, works with the keyboard as a combobox, offers an explicit "none" choice where one exists, and can search a server source. It stays internal to the library. | One accessible control for every catalog choice; no third-party dependency in the package. |
| UD2 | **Errors appear at their field and next to the save button.** The form checks required fields and numbers before sending; server field errors map to their field; errors without a field and request failures appear above the form's actions; the first invalid field receives focus. | The administrator sees what is wrong where they are looking. |
| UD3 | **Accounts are chosen by name through a platform search.** `NewHeap.Platform.Common.Actors` gains `INhActorSearch`; NewHeap Identity implements it on its user table and an application with its own identity registers its own with `AddNewHeapActorSearch<T>()`. The administration searches it through `GET admin/accounts` and shows names for stored ids through `INhActorDirectory`. Without a search, the screen asks for the account id and shows the name it resolves to. | Applications without NewHeap Identity keep working, and most applications get the picker without code. |
| UD4 | **An agent can be kept out of the chat.** Agents gain `OfferedInChat`: an agent in code sets it with `HideFromChat()`, an agent created in the administration with a choice in its editor. A hidden agent is left out of the agent picker and refuses new chat conversations as unknown; its runs, subject threads, consultations and existing conversations are unaffected. Existing agents stay offered. | Background agents do not clutter the picker or invite chats they are not built for. |
| UD5 | **Catalog choices explain themselves.** Approval policies come with a summary of who approves, for how long, with four eyes or not, and how; the screen words it in the user's language. Rule handlers, events and subject sets show their description. | An administrator chooses by meaning, not by an identifier. |
| UD6 | **Run settings are grouped by question** — who answers for the agent, how it works, who approves its proposals, its limits — with the rarely changed options under "Advanced" and their defaults filled in. | The common path is short; nothing is lost. |
| UD7 | **A guided setup lets an agent work on its own** in one flow: when it starts (event or schedule), what it does (its way of working, instructions and tools), on whose behalf, approval and limits, and a review. It saves the run settings, a skill and a trigger, which start in shadow as before. The separate forms remain for later changes. | Creating an agent that works on triggers without code becomes one task instead of three. |

## Design

### U1: controls, errors, pickers and layout

- The shared form styles cover every input type the administration uses, including numbers, with
  the same height, border, focus ring and invalid state as text fields.
- `NhAssistantPickerComponent` (internal) implements the ARIA 1.2 combobox pattern with a listbox
  popup: typing filters, arrow keys move, Enter chooses, Escape closes, and the chosen value shows
  its name with the description underneath. It accepts a static list or a search function with a
  debounce, and reports `aria-invalid` and its error id like the other controls.
- A small field-error helper gives each field its hint and error ids; the run configuration
  validates before sending and sets the first invalid field's focus after a refusal. Every server
  field name the run configuration endpoints return has a field; anything else is listed in the
  form-level error.
- Forms keep to the width of their host: no control or text sets a minimum width, values break
  anywhere, and grids collapse to one column.

### U2: accounts by name

- `INhActorSearch.SearchAsync(NhActorSearchRequest)` returns `NhActorInfo` entries for a query of
  at least two characters, at most `Limit` of them, for the actor kinds asked for.
- NewHeap Identity searches user name and e-mail address, case-insensitively and
  provider-neutrally, and leaves out users who are blocked.
- `GET admin/accounts?query=` returns `{ actorId, displayName, detail }` entries for assistant
  administrators. The run catalog reports whether a search exists (`accountSearch`). Run settings
  and the technical account of an agent return `serviceAccountDisplayName` next to the id.
- The run settings form and the technical account action of the agent list use the picker.

### U3: agents outside the chat

- `AssistantAgent.OfferedInChat` (default `true`) with migrations `AgentChatAvailability` for SQL
  Server and PostgreSQL. Code agents take it from `HideFromChat()` at every seed; administration
  agents from `offeredInChat` on create and update.
- The status and `GET agents` lists leave hidden agents out; `POST conversations` answers
  `assistant-agent-not-found` for them. Agent runs, subject threads, consultations and conversations
  that already exist do not check it.
- The agent list shows a "Not in the chat" badge; the editor offers the choice with an
  explanation.

### U4: language and guided setup

- The run catalog gains `approvalPolicySummaries`: per policy its approvers (initiator, subject role
  or authorization policy), lifetime, four-eyes and mode. The screen turns them into a sentence.
- The run settings form reads as questions: *Who answers for this agent?* with the account picker,
  *How does it work?* (the model, or fixed rules from the catalog with their descriptions),
  *Who approves its proposals?*, and *Limits*. Retries, concurrency, runs per subject, triage,
  elevated reads, gateway reads and delegation sit under *Advanced* with their explanation.
- The guided setup opens from an administration agent without run settings ("Let this agent work
  on its own") and from the editor of a new agent that is meant for the background. Its steps save
  in order and stop at the first refusal, with the error at the step's fields.
- Summaries show names and descriptions, never raw ids.

### U5: mock, sample and documentation

- The mock API serves accounts from `admin.accounts` in the scenario, the chat availability and
  the policy summaries, with the same validation as the server.
- The sample creates its project starter at runtime in development, as data and hidden from the
  chat, answering to a new technical account; the portal gets a real assistant administration page.
  The playground offers its development accounts in the account search. SPM-298 covers it.
- Guidance rules, the consumer guide and the release notes describe the picker behavior, the
  account search hook, `HideFromChat()` and `offeredInChat`.

## Delivery order

| Phase | Delivers | Verification |
| --- | --- | --- |
| **U1** | Control styles, the picker, field errors and validation, layout | UI specs for validation, error placement, focus and the picker's keyboard behavior; mobile width without horizontal scroll |
| **U2** | `INhActorSearch`, the NewHeap Identity search, the accounts endpoint and the account picker | Search tests on SQL Server and PostgreSQL; endpoint authorization; UI spec choosing an account by name |
| **U3** | `OfferedInChat`, `HideFromChat()`, the migrations and the editor choice | Status, create-conversation and agent-run tests on both providers |
| **U4** | Policy summaries, the regrouped form and the guided setup | UI specs for the setup's steps, refusals and the shadow start |
| **U5** | Mock, sample, guidance, consumer guide and release notes | Sample tests, browser walk-through, validators |

## Out of scope

Editing building blocks themselves, choosing tools beyond the agent's own selectors, skills and
triggers in code, and a visual cron editor (the schedule keeps its expression with examples).
