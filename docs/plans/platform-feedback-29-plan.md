# Platform Feedback on 29.0.0 Plan

A consuming application built a chat about its business objects on NuGet 29.0.0 and
`@newheap/platform-ai-chat` 0.26.0, ran it against a real model and measured twenty questions with fixed
answers. It reported five points: retrieved context that disappears without a trace, a chat agent that cannot
be used, a run rule that blocks the application's own chat, no way to chat about an object without running as
an agent, and a context budget that does not count what the context costs. Each finding was checked against
`main` at 15df3560 (NuGet 29.0.0, `@newheap/platform-ai-chat` 0.26.0); all five hold, and FB72 goes further than
reported. This plan records one choice per finding. It is a plan, not usage documentation: once it is built, the
guidance rules, the release notes and the sample cases are authoritative.

## Status

Decided by Lars on 2026-11-01, one by one: FB72 to FB76 as advised (option (a)), and FB77 option (b), all of it in one
train. The decisions are summarized below; the sections under "Choices" keep the options that were weighed. Not built
yet.

## Decisions

| # | Decision |
|---|---|
| FB72 | **One fit step before the model call** drops the lowest-ranked passages and then the application map, says so in the block and in a content-free warning, and records the hash of what was sent. A startup warning names an agent whose instructions plus retrieval budget cannot fit. |
| FB73 | **The budget counts the formatted block**, whichever formatter is registered, and the default formatter leaves out empty fields and escapes only what JSON requires. The defaults stay; a release-note row says a full budget holds fewer passages. |
| FB74 | **`reloadStatus` selects among `chatAgents()`**, with a guard for a subject whose agents are still loading, and a regression spec. |
| FB75 | **A property on `NhAiInvocationContext`**, for example `IsAgentWork`: true in an agent run and a consultation, false in a person's chat. `ActorKind` keeps its meaning. |
| FB76 | **`ChatOnly().ChatsAbout(types)`**: a chat-only agent names the object types people may chat with it about, and `RequireChatSubject()` becomes valid next to it. |
| FB77 | **All of it in train 32**, with lanes OB (FB74, FB76), CB (FB72, FB73) and AK (FB75). |

## Rules for all of it

- Every change is opt-in or compatible unless its choice says otherwise. A breaking change gets a
  `Breaking change | Required action` row.
- Every change follows the repository rules:
  - code and migrations for SQL Server and PostgreSQL where persistence changes;
  - tests with authorization negatives;
  - a release-notes section per release unit;
  - guidance and consumer guide updates;
  - an executable sample case, extending the existing case where one covers the surface;
  - the public API snapshot for a public change;
  - English and Dutch texts for Angular and model-facing parts.
- Nothing names the consuming application.

## Choices

Each choice lists the options that were weighed. "(advice)" marks the recommendation.

### A. Retrieved context

#### FB72. Turn data that does not fit the instructions

The model instructions are at most 32,768 characters. When the text the model would receive is longer,
`NhAssistantTurnRunner` (line 3101) runs the turn with `prompt.Instructions` alone. That leaves out more than the
retrieved context: the application map and the turn data (the situation, the person's screen and the shared
conversation) go as well. Nothing is logged. The audit still records the context hash of a block the model never
received, because `RetrievedContextHash` is set before the check. The model then answers without the documents,
and that reads as a confident "the documents do not say". The application met this on a real dossier with
instructions of 29,600 characters, and only a measurement with fixed answers showed it.

- **(a) Fit the blocks, in a fixed order, and tell the log and the model (advice).**
  - One fit step before the model call drops the lowest-ranked passages one at a time until the text fits, then
    the application map. Only when that is not enough does the turn fall back as it does today.
  - The block is formatted again for what remains, and the recorded context hash is that of what was sent;
    no hash when nothing was sent.
  - When passages were left out, the block says so in the turn's language, so that the model does not conclude
    that the sources are silent.
  - A content-free warning names the agent, the turn, the passages kept and dropped and the characters.
  - At startup, a warning for each agent with `UseRetrievedContext` whose instructions plus retrieval budget
    cannot fit.
- **(b) Only the warnings.** This shows the problem but still answers without the context.
- **(c) Fail the turn with its own code.** This is honest, but the person gets an error on a question the model
  could still answer from the passages that fit.

#### FB73. A context budget that counts what the context costs

`NhAiContextResolver` counts only each item's content against `MaxCharacters` (`ContentCharacters`, line 79). The
default formatter adds about 320 characters of JSON per item. It also uses the default JSON encoder, which writes
quotes, `<`, `>` and every non-ASCII character as `\uXXXX`, so Dutch text costs more than it looks and reads worse
for the model. `NhAssistantRetrievalService.Neutralize` then has no `<` or `>` left to replace. A block that stays
within `MaxRetrievedCharacters` by this count can be far longer in the instructions, which is how FB72 is reached.

- **(a) The budget counts the formatted block, and the format gets leaner (advice).**
  - The retrieval service applies the fit step of FB72 to the formatted block, so `MaxRetrievedCharacters` and
    `UseRetrievedContext`'s `MaxCharacters` limit what the model receives, whichever `INhAiContextFormatter` is
    registered.
  - The default formatter leaves out empty fields and escapes only what JSON requires, as the stored client
    context does since FB52.
  - A full budget holds fewer passages than before, so this gets a release-note row. The defaults stay.
- **(b) The resolver counts a fixed overhead per item.** This is an estimate, and wrong for another formatter.
- **(c) Only guidance:** name the overhead, so that applications budget for it.

### B. The chat in the panel

#### FB74. `RequireChatSubject` agents cannot be used

`sendMessage` calls `reloadStatus()` (`nh-assistant.store.ts`, line 1121), and that selects the agent again with
`selectInitialAgent(status.agents)` (line 796). An agent with `RequireChatSubject()` is by definition not in
`status.agents`, so the store selects another agent and the server refuses the first message with 409
`assistant-agent-does-not-work-on`. `RequireChatSubject()` therefore never worked from the panel. The application
works around it by leaving the agent without `ConfigureAgent`, which costs it three things: the agent shows in the
general list, it cannot ask structured questions and it works on every object type.

- **(a) `reloadStatus` selects among the agents of the chat (advice).** `selectInitialAgent(this.chatAgents())`,
  with a guard for a subject whose agents are still loading, and a regression spec that sends the first message of
  a chat about a subject.
- **(b) No status reload before sending.** This removes the trigger, but the reload is what notices a change of
  rights before a message goes out.

### C. Agents and chats

#### FB75. Telling a person's chat from agent work

In a person's chat, `NhAssistantInvocationGate` (line 67) and `NhAssistantTurnRunner` (line 3228) set
`NhAiActorKind.Agent`, as in a run. An application that puts a rule for runs on the actor kind blocks its own
chat; the application lost every call of one tool family in every chat this way, and no test caught it. `RunId`
does not help, because it holds the turn id everywhere. `AgentRunId` is null in a chat but also in a consultation,
so an application has to combine it with `NhAiAgentInvocationScopes.Current`.

- **(a) A property on `NhAiInvocationContext` that says it (advice).**
  - For example `IsAgentWork`: true in an agent run and a consultation, false in a person's chat. The agent
    invocation gate sets it from the scope.
  - `ActorKind` keeps its meaning, the actor is still the agent, so nothing changes for existing checks.
  - Guidance says to read this property for a rule about runs, and names the actor kind as the wrong signal.
- **(b) A new `NhAiActorKind` member for a chat.** Every `== Agent` check, in the library as in applications,
  silently changes meaning: audit, proposals, snapshots and the MCP context.
- **(c) Only guidance:** combine `AgentRunId` with the scope.

#### FB76. Chatting about an object without running as an agent

`ChatOnly()` sets `WorksOn` to nothing and refuses `RequireChatSubject()`. With `WorksOn`, the assistant is a run
agent: it joins the object's board with a manual start. There is no way to say "chat about this object, but do
not run as an agent". `NhAssistantChatSubjects.WorksOn` decides a chat's subject through `agent.WorksOn`.

- **(a) `ChatOnly().ChatsAbout(types)` (advice).**
  - A chat-only agent names the object types people may chat with it about. `WorksOn` stays nothing, so every
    start, trigger, hand-off and consultation stays refused, and the agent joins no board or run maintenance.
  - `NhAssistantChatSubjects` accepts those types for a chat, with the subject's access resolver on creation and
    on every turn, read and invitation, as for any chat about a subject.
  - `RequireChatSubject()` becomes valid next to `ChatsAbout`, and startup refuses a type that is not registered.
- **(b) `WorksOn` with a switch that takes the agent out of boards and starts.** The agent then still counts as a
  run agent, and every run setting and maintenance path needs its own exception.
- **(c) Only guidance:** register a subject type for chats only. This works today, but doubles the application's
  resolvers per object.

### D. Delivery

#### FB77. Order

- **(a) FB74 first as a fix of its own, then a train with three lanes (advice).**
  - FB74 lands first on `main`: it blocks the application's demo, and FB76 builds on it.
  - CB: FB72 and FB73, which share the fit step.
  - AK: FB75.
  - OB: FB76.

  The full matrix runs once per train.
- **(b) All of it in one train.** One verification round less, but the bug fix waits for the features. Lars chose
  this one: FB74 then shares lane OB with FB76, which builds on it.

## Lane choices

| # | Choice | Status |
|---|---|---|
| CB1 | The budget counts the whole block: heading, introduction, note and the formatted items, not only the formatter's output. That is what the model receives, and it stays true for any formatter. | for Lars to review |
| CB2 | When the application map has to go, its room goes back to the passages: the fit runs again without the map. The order stays as decided, the map goes only when the text does not fit without any passage, but a turn no longer ends without passages next to free room. | for Lars to review |
| CB3 | A block without passages keeps its heading and the note, so the model still hears that the sources hold more. It records no hash and counts as `Public`. When not even the note fits, the turn data stays and the block goes. | for Lars to review |
| CB4 | The note and the warning count what the fit dropped, not what the resolver's `MaxItems` or content budget skipped: a cut by `MaxItems` is a choice of the agent and would put the note in most turns. So a passage the resolver skipped stays unmentioned, as today. | for Lars to review |
| CB5 | A passage the budget drops does not count for the turn's classification. A passage the fit of the turn drops does: the classification is decided before the fit, the fit can differ per skill, and keeping it errs on the safe side. | for Lars to review |
| CB6 | The startup warning reads code: the agent's instructions, the seed of the application context, the default preferences and each skill's instructions. It reads no database, so instructions that change as data (an edited application context, an overridden or administrator-created agent, long personal preferences) reach only the warning of the turn. It leaves the map and turn data out, because their size is known only in a turn. | for Lars to review |
| CB7 | The MCP tools load before the fit, so the note about missing connections keeps its room next to the block and wins from the lowest-ranked passages. When even the instructions plus the note do not fit, the note stays out with a warning of its own. | for Lars to review |
| CB8 | Characters outside the Basic Multilingual Plane, such as emoji, stay a pair of `\uXXXX` escapes: `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` keeps them and a custom encoder is not worth it for this. The budget counts them as they are written. | for Lars to review |
| CB9 | `NhAiRoleSeparatedPromptAssembler`, the only other caller of the formatter, has no delimiter of its own, but a host may join its blocks with one, and the old encoder kept markup out. Its retrieved block now neutralizes through the same helper as the assistant. A custom formatter that writes markup for this block sees it replaced; the release notes say so. | for Lars to review |
| CB10 | A turn on its instructions alone records 0 facts, 0 page entities and no page context next to its empty hashes, so the audit says what the model received throughout. Instructions over the limit record the same, because no model call follows. | for Lars to review |
| CB11 | The warning goes per model agent, so a turn that switches skills can log it twice, and it fires at warning level for a drop by the budget as well as for one by the limit. The trace of the resolution keeps the resolver's hash, which the guidance names. | for Lars to review |
