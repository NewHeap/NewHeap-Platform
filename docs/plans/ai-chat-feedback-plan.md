# AI Chat Feedback Plan

This document records three improvements of the assistant chat that came from consumer feedback,
what was built for each and the choices made while building them. Lars decided the improvements;
the choices FB1 to FB14 were open and are made here **for Lars to review**. It is a plan, not usage
documentation: the guidance rules `nh-ai-assistant` and `nh-ai-assistant-ui`, the release notes and
sample cases SPM-305, SPM-310 and SPM-312 are authoritative for usage.

## Status

Implemented on 2026-10-23 on branch `feature/chat-feedback`, with sample case SPM-312 added and
SPM-305 and SPM-310 extended. FB11 and FB13, the two known races, are closed on branch
`fix/sequence-lock-and-shadow-test`.

Decided by Lars, 2026-10-23: every choice from FB1 to FB14 is **Decided** (see the decision record).
Lars kept FB5, FB6, FB9, FB11, FB13's lock timeout and FB14 as built, and asked for changes to FB1,
FB2, FB3, FB4, FB7, FB8, FB10, FB12 and FB13's live updates. Those changes were built on 2026-10-23 in
merge train 15 (`feature/upload-type-followups`, `feature/application-message-followups`,
`fix/live-messages-while-streaming`). Lars decided the lanes' choices FB15 to FB27 and FB30 to FB32 on 2026-10-23: kept
everywhere except FB23 (b), FB26 (b) and FB32 (b), which train 15 also built; FB28 and FB29 are for Lars to review.

## 1. Uploads without a usable file type

A browser sends no type, or `application/octet-stream`, for a file its system does not know, and
Windows names some extensions after the program that opens them, such as `application/vnd.ms-excel`
for `.csv`. Such uploads were refused although the application accepts the file's real type.

Built: `NhAssistantUploads` resolves the media type with `NhAssistantUploadMediaTypes.Resolve`. A
missing type, a generic type, or a type that devices commonly send for the file's extension takes the
type of the file name's extension, only when the application accepts that type; otherwise the
declared type counts as it is. The content check and the scanner run unchanged. The composer of
`@newheap/platform-ai-chat` and the mock API apply the same table, and the file picker's `accept`
attribute offers the extensions of every accepted type the table knows.

- **FB1 — The table is the platform's, not configurable.** Extensions: `.pdf`, `.png`, `.jpg`,
  `.jpeg`, `.gif`, `.webp`, `.docx`, `.xlsx`, `.pptx`, `.doc`, `.xls`, `.csv`, `.tsv`, `.txt`, `.md`,
  `.markdown`, `.json`, `.xml`. An application-specific type is not derived from an extension; a
  device that sends it declares it. Alternative: an `NhAssistantUploadOptions.MapExtension` option.
- **FB2 — What counts as generic.** Always: `application/octet-stream`, `binary/octet-stream`,
  `application/unknown`. Per extension: for `.csv` `application/vnd.ms-excel`, `application/csv`,
  `application/x-csv`, `text/x-csv`, `text/comma-separated-values`, `text/x-comma-separated-values`;
  for `.md` `text/x-markdown`; for `.jpg` `image/jpg`, `image/pjpeg`; for `.png` `image/x-png`; for
  Office Open XML documents `application/zip` and `application/x-zip-compressed`, because the
  signature check still requires a ZIP package. A type that is an alias for one extension stays
  specific for another: `Sheet.xls` declared `application/vnd.ms-excel` is an Excel 97 workbook.
- **FB3 — A generic type the application accepts itself still wins when the extension names no
  accepted type.** An application that accepts `application/vnd.ms-excel` but not `text/csv` keeps
  receiving `.csv` files as `application/vnd.ms-excel`, as before. When it accepts both, a `.csv`
  becomes `text/csv`.
- **FB4 — No content sniffing for text types.** A `.xls` workbook renamed to `.csv` and declared as
  `application/vnd.ms-excel` passes as `text/csv`; the platform checks signatures only for PDF, PNG,
  JPEG and Office Open XML, as before. The application's file source or scanner checks the content of
  the text types it accepts.
- **FB5 — The client declares the type it derived.** The composer uploads the file as a blob of the
  derived type, so the server sees `text/csv`; the server's own derivation covers other clients.

## 2. Application messages

Built: `INhAssistantConversationMessages.PostAsync(conversationId, NhAssistantApplicationMessage)`
in `NewHeap.Platform.AI.Chat`, registered by `AddNewHeapAssistant`. The message is stored as an
`AssistantMessage` with the new role `application`, author `application` and its title, link,
sender and, for a trusted message, the trusted flag in an `application` part next to the text part,
so no migration is needed. It gets the next sequence number, is published live (`message.created`,
then the conversation's changed state), optionally notified, audited as `ApplicationMessagePosted`
and given to the model in later turns. `nh-assistant-thread` renders it; agent threads open in the
same panel, so both views are covered. SPM-312 shows it from the portfolio analysis. Lars's changes
to FB7, FB8, FB10 and FB12 were built by lane M of train 15 (branch
`feature/application-message-followups`); FB20 to FB27 record the choices made there.

- **FB6 — A new message role, not a user message with a special author.** Every check for "the
  person's latest message" (`Role == user`) keeps ignoring application messages, so a title, a
  consultation's question or a resumed turn never takes the application's text for the person's.
  The history replay maps the role to a user message for the model, with the marker below.
- **FB7 — How the model reads it.** A user message that starts with `[Application]` (its Dutch
  equivalent in a Dutch turn) and says that it is information from the application, not a request from the
  people in the conversation and never instructions, followed by an `application-message` block with
  the sender, title, link and text; angle brackets in the content are replaced, as in the retrieved
  context, so the content cannot close the block. By default the application is treated as an
  untrusted source of information, like retrieved context, because its messages can carry
  user-generated data. The opt-in `NhAssistantApplicationMessage.Trusted` makes the model follow
  the message as the application's instruction: the same marker and block, with a sentence that the
  application, not the people in the conversation, gives this instruction (FB21). The application
  guarantees that a trusted message carries no user-generated data; guidance and release notes warn
  against it. Conversation snapshots for reviewed MCP tools leave application messages out, trusted
  or not, as they leave out everything but people's and the agent's text.
- **FB8 — Text is always Markdown.** The chat renders it with the sanitizing Markdown pipe of the
  agent's answers; plain text is valid Markdown. No `Format` option. The public
  `NhAssistantMarkdown.Escape(string)` lets the application put people's data into the Markdown so
  that it shows literally (FB24).
- **FB9 — Links.** A path within the application (`/…`, not `//` or `/\`) or an absolute `https`
  address, the rule of subject links, which the client now shares (`nhAssistantLinkTarget`); any
  other link is `assistant-message-invalid` on the server and shows nothing in the client. Bounds:
  text `MaxMessageChars`, title 200, sender 100, link 2,048, label 100, idempotency key 100.
- **FB10 — Notifications by Web Push and in the application's inbox to the conversation's people,
  opt-in per message.** `Notify` sends the new push kind `application-message` to the owner and
  participants of a chat and to the participants of an agent thread (the people of the subject who
  joined it), with the conversation's title and a fixed text, never the message. When the
  application turned on `UseInAppAgentNotifications`, the same people also get an in-app message
  through `INhUserNotificationSink`, for chats and threads alike: category `assistant-application`,
  one group per conversation, the conversation's title and a fixed text in English or Dutch by the
  recipient's culture from `INhActorDirectory` (FB25). A shadow thread never notifies. The agent
  notification policy is not used and there is no new `NhAssistantAgentNotificationKind`: the
  policy needs a subject, a chat has none, and a new kind would reach every application's custom
  policy and channels.
- **FB11 — Idempotency key optional.** With a key the conversation keeps one application message
  per key (stored as client message id `application:<key>`) and a repeated post returns the earlier
  message with `Duplicate`, without publishing, notifying or auditing again. Without a key every post
  adds a message. **Closed** on branch `fix/sequence-lock-and-shadow-test`: the look-up of an earlier
  application message with the key and the insert run in one transaction that holds the
  conversation's message lock (FB13), so two posts with the same key at the same moment store one
  message. There is still no unique index on client message ids, so no migration.
- **FB12 — Open conversation and access.** "Open" means not archived: an archived conversation is
  `assistant-conversation-not-found`, as an unknown one, and a running or waiting conversation
  accepts the message (it does not start a turn). Every post makes the access choice, which the
  compiler requires (FB20): `NhAssistantApplicationMessageAccess.ReadableBy(person)`
  (`NhAssistantConversationPerson`) makes the post depend on that person being the owner or a
  participant of the same tenant, which the sample uses because the conversation id comes from the
  person's client; `WithoutPersonCheck()` says explicitly that the application chose the
  conversation itself. A message whose choice is null anyway is `assistant-message-invalid`. Posting
  while the assistant is disabled is `assistant-disabled`.
- **FB13 — Ordering and the conversation list.** The message gets `max(sequence) + 1`, as every
  message does. **Closed** on branch `fix/sequence-lock-and-shadow-test`: every message insert reads
  the highest number and inserts in one transaction that holds the conversation's message lock, the
  platform's transaction lock that already serializes joins (`sp_getapplock` on SQL Server,
  `pg_try_advisory_xact_lock` on PostgreSQL, resource `NhAssistant:ConversationMessages:<id>`). That
  covers a person's message, the assistant message of every turn (chat, decision, agent run, resumed
  and consultation turns), an agent's queued input, whose "written once" check now runs under the
  same lock, and the application's message. No two messages of a conversation share a number,
  including those of two concurrent turns; no unique index, no migration. Choice for review: a
  message that waits longer than 15 seconds for the lock is not stored and the store throws a
  `TimeoutException`, unlike a join, which continues without its lock after 5 seconds; without the
  lock the message could share its number, and the lock is held for three short statements, so only
  a stalled database waits that long. Alternative: continue without the lock with a warning, as a
  join does. The application's message moves the conversation's update time, so it rises in the
  people's lists, except while a turn runs, whose update time tells an abandoned turn from a live
  one. A tab that streams a turn of that conversation shows an application's or a colleague's message
  that arrives during the turn at once, where its sequence number puts it: after the answer that is
  still being written, because the server numbers a turn's answer when the turn starts (FB30). It
  keeps taking the turn's own events and its own message from its stream (FB31).
- **FB14 — Audit kind number 70.** `ApplicationMessagePosted = 70` leaves 66 to 69 free so that the
  parallel lanes of this train can add kinds without colliding values. `ActorId` is `application`,
  `ObjectId` the message id, `ResultCode` `posted` or `notified` (by Web Push, in the inbox or both),
  `ApplicationMessageTrusted` whether the message was trusted (FB22), and in a thread `SubjectType`
  and `SubjectId`.

The sample's management page that starts the portfolio analysis does not pass the assistant
conversation yet; the start request (`AssistantConversationId`) and the sample test show the path.

## 3. The inbox description in grouped view

Built: `nh-assistant-agent-inbox` shows `nh-assistant.agent-runs.inbox.description-grouped` when
`grouped` is on: "Proposals of agents that wait for your decision, grouped by object; the group with
the proposal that expires first is on top.", with the Dutch translation Lars gave. The flat inbox
keeps its text. The board and the subject suggestions show other texts and do not group, so
they stay as they are.

## Decision record

| Choice | Decision | To build |
|---|---|---|
| FB1 | (b) An application can map its own extensions: `NhAssistantUploadOptions.MapExtension(extension, mediaType)`. | Built by lane U (2026-10-23) on `feature/upload-type-followups`: `MapExtension` and `MappedExtensions` in `NhAssistantFiles.cs`, the derivation in `NhAssistantUploadMediaTypes.Resolve`, the mappings of accepted types in `NhAssistantAgentUploadsDto.MappedExtensions` and `AgentUploads.mappedExtensions`, used by the composer's derivation and `accept` attribute (`nh-assistant-files.ts`) and the mock API; the sample maps `.jfif` to JPEG (SPM-305). Choices FB15 to FB18. |
| FB2 | Kept, plus `text/xml` as an alias of `.xml`: browsers send `text/xml`, the table names `application/xml`. | Built by lane U (2026-10-23): the alias in `NhAssistantUploadMediaTypes` and `nh-assistant-files.ts`, tested in `AssistantUploadRuleTests`, `nh-assistant-uploads.spec.ts` and the composer spec. |
| FB3 | Kept. | Built by lane U (2026-10-23): the breaking-change row of `docs/release-notes/v-next.md` says it, also for `.xml`. |
| FB4 | (b) Text types are refused when their content starts with a known binary signature. | Built by lane U (2026-10-23): `NhAssistantUploads.MatchesSignature` and `StartsAsBinary`, the same check in the mock API (`nh-assistant-mock-files.ts`), a UTF-16 test on both, and the `.pptx` ZIP check on the server and in the mock. Choice FB19. |
| FB15 | Whether an application's mapping may replace an extension the platform's table knows: (a) yes, the mapping takes precedence for its extension, and a later mapping of an extension replaces an earlier one; (b) no, `MapExtension` throws for a known extension; (c) the mapping only applies when the table's type is not accepted. Chosen (a): the application knows which types it accepts, for example `.xml` to `text/xml` or `.md` to `text/plain` for an application that accepts only those; a mapping cannot widen the accepted types, and the content check and the scanner still run. | Built by lane U (2026-10-23): (a); decided by Lars (2026-10-23): kept |
| FB16 | A mapping to a type the application does not accept: (a) allowed and without effect, so the derivation falls back to the platform's table and clients never receive it; (b) `ConfigureUploads` throws. Chosen (a): `AcceptMediaTypes` and `MapExtension` may be called in any order or per environment, as a type the table names that the application does not accept names nothing either; the server and the client apply exactly the same mappings. | Built by lane U (2026-10-23): (a); decided by Lars (2026-10-23): kept |
| FB17 | What `MapExtension` takes: (a) a dot followed by letters, digits, `_`, `+` or `-`, trimmed and lowercased, at most 32 characters, one segment (only the last extension of a name counts), a media type by `NhAiMediaTypes.Normalize`, at most `MaxMappedExtensions` (100) mappings, otherwise `ArgumentException`; (b) also an extension without the dot, no bound. Chosen (a): explicit programmer input as `AcceptMediaTypes` validates it, and a bound because every agent summary carries the list. | Built by lane U (2026-10-23): (a); decided by Lars (2026-10-23): kept |
| FB18 | How the client receives the mappings: (a) `NhAssistantAgentUploadsDto.MappedExtensions`, an init property listing `{ extension, mediaType }` (`NhAssistantUploadExtensionDto`) ordered by extension, empty when there are none, and `mappedExtensions?` optional in `AgentUploads` for older servers and mock scenarios; (b) a dictionary or a new positional parameter. Chosen (a): no change to the record's constructor, no dependence on a dictionary key naming policy, a stable order. | Built by lane U (2026-10-23): (a); decided by Lars (2026-10-23): kept |
| FB19 | The details of the content check of text: (a) also types with the `+json` or `+xml` suffix (such as `application/ld+json`, `image/svg+xml`), the empty ZIP archive (`PK 05 06`) next to `PK 03 04`, the full eight bytes of OLE2, and every signature only at the start (`%PDF-` too, so a text that quotes it further on passes); (b) only the listed types and `PK 03 04`. Chosen (a): those types are JSON or XML text by definition and an empty archive is no text either; matching only at the start keeps text that mentions a signature. | Built by lane U (2026-10-23): (a); decided by Lars (2026-10-23): kept |
| FB5 | Kept. | — |
| FB6 | Kept. | — |
| FB7 | (b) An opt-in `NhAssistantApplicationMessage.Trusted`: the model follows a trusted message as the application's instruction; the application guarantees that it carries no user-generated data. | Built by lane M (2026-10-23): `Trusted` with the same marker and block and a sentence of its own for the model (FB21), the flag in the `application` part (no migration), `ApplicationMessageTrusted` in the audit (FB22), `trusted` in the DTO with a label in the thread (FB23), MCP snapshots still leave application messages out, and guidance and release notes warn against user data in trusted text. |
| FB8 | (b) Text stays Markdown; a public `NhAssistantMarkdown.Escape(string)` lets applications put user data into it without formatting. | Built by lane M (2026-10-23): the helper (FB24), its server tests, a client spec that renders an escaped string literally, guidance; SPM-312 escapes the division's name (FB26). |
| FB9 | Kept. | — |
| FB10 | (b) Web Push and, when the application turned on in-app agent notifications (`UseInAppAgentNotifications`), an in-app message through `INhUserNotificationSink`, for chats and threads alike; no new agent notification kind. | Built by lane M (2026-10-23): category `assistant-application`, one group per conversation, the conversation's title and a fixed text without the message, the recipient's culture from `INhActorDirectory` (otherwise the current UI culture), never for a shadow thread; `ResultCode` `notified` covers both (FB25); SPM-312 shows it in its test host (FB27). |
| FB11 | Kept: a repeated key returns the earlier message with `Duplicate`, whatever its content. | — |
| FB12 | The access choice is required: a post names `ReadableBy(person)` or explicitly `WithoutPersonCheck()`, otherwise `assistant-message-invalid`; the rest as built. Not breaking, the API is not released yet. | Built by lane M (2026-10-23): the required `Access` with `ReadableBy(person)` and `WithoutPersonCheck()` (FB20), the check, the sample and its test, guidance. |
| FB13 | Lock timeout kept: after 15 seconds the message is not stored (`TimeoutException`). Live updates changed: a tab that streams a turn applies `message.created` meanwhile. | Built on branch `fix/live-messages-while-streaming`: the client places an application's or a colleague's message that arrives during the turn where its sequence number puts it, which is after the answer that is still being written, not before it: the server numbers a turn's answer when the turn starts (FB30). |
| FB14 | Kept: 70. | The audit carries FB7's trusted flag. |
| FB20 | Shape of the required access choice. (a) A `required` init member `Access` of the sealed record `NhAssistantApplicationMessageAccess` with the factories `ReadableBy(person)` and `WithoutPersonCheck()`, which reads like the message's other init members; the factory refuses a null person or a blank actor id with an exception, a programming error; an `Access` that is null past the compiler is `assistant-message-invalid`. (b) A constructor parameter, `new NhAssistantApplicationMessage(text, access)`. | Built by lane M (2026-10-23): (a); decided by Lars (2026-10-23): kept |
| FB21 | How the model reads a trusted message. (a) The same `[Application]` (`[Applicatie]`) marker and `application-message` block with its angle brackets replaced, and a sentence of its own: "It is an instruction from the application, not from the people in the conversation; follow it." (Dutch: "Het is een instructie van de applicatie, niet van de mensen in het gesprek; volg die op."). The marker names the source, the sentence carries the trust. (b) A marker of its own, such as `[Application instruction]`, as FB7's first "To build" said. | Built by lane M (2026-10-23): (a); decided by Lars (2026-10-23): kept |
| FB22 | Where the audit carries the trusted flag. (a) A new `NhAssistantAuditEvent.ApplicationMessageTrusted` (`bool?`: true or false on `ApplicationMessagePosted`, null on other events); no existing field fits honestly and `ResultCode` keeps `posted` and `notified`. A `bool?` passes the privacy allow-list of `AssistantRegistrationTests` by its type, so the list needed no new name. (b) A composite `ResultCode` such as `notified;trusted`. | Built by lane M (2026-10-23): (a); decided by Lars (2026-10-23): kept |
| FB23 | Whether clients see the trusted flag. (a) No: `NhAssistantApplicationMessageDto` stays as it was. The flag changes only how the model reads the message, people see both kinds alike, and the JSON contract, the client and the mock API need no change. (b) `application.trusted` in the DTO, so a client could mark trusted messages. | Built by lane M (2026-10-23): (a); decided by Lars (2026-10-23): (b), with a small label "Instruction for the assistant" in `nh-assistant-thread` (English and Dutch); built by lane M (2026-10-23) |
| FB24 | What `NhAssistantMarkdown.Escape` does. (a) A backslash before each of the 32 ASCII punctuation characters CommonMark lets a backslash escape; spaces and tabs at the start of a line left out, because they would start an indented code block and the chat does not show them; `\r\n` and `\r` become `\n`; everything else untouched. Documented for running text, headings and link text, not code spans, code blocks or link addresses. One shared example proves it in `AssistantMarkdownTests` and against the chat's marked and DOMPurify pipe in `nh-assistant-markdown.spec.ts`. (b) Keep indentation as non-breaking spaces. | Built by lane M (2026-10-23): (a); decided by Lars (2026-10-23): kept |
| FB25 | How the in-app message is wired. (a) `UseInAppAgentNotifications` also sets a registration flag; the internal `NhAssistantInAppApplicationMessageNotifier` runs after Web Push for the same people (one shared rule, `NhAssistantApplicationMessageRecipients`), at level Information, without a `Url` (the library knows no chat route, and the message's link is content), with the conversation's title whatever `Push.IncludeConversationTitle` says, as the in-app agent channel shows the subject's name, and "Assistant" (Dutch "Assistent") without a title. It catches its own failures, so neither channel fails the post, and logs a warning without a sink. (b) A builder switch of its own for application messages. | Built by lane M (2026-10-23): (a); decided by Lars (2026-10-23): kept |
| FB26 | The user data of SPM-312. (a) The division the analysis covered, escaped, so the message stays untrusted; the evidence uses a name with Markdown, HTML and an instruction for the model. An analysis spans all projects of a division, so no single project name fits. Trusted messages have no sample evidence of their own, because the sample's only application message carries user data; library tests cover them. (b) A second, trusted message with fixed text in SPM-312. | Built by lane M (2026-10-23): (a); decided by Lars (2026-10-23): (b), SPM-312 also posts a trusted message with fixed text and no people's data; built by lane M (2026-10-23) |
| FB27 | Where SPM-312 shows the inbox. (a) The sample application still does not turn on in-app notifications; its collaboration test host now registers an inbox and `UseInAppAgentNotifications`, and the agent-run host of SPM-251, which derives from it, shares that inbox instead of registering its own. (b) Turn them on in `AddSampleAssistant`, which changes the agent notifications of every sample host. | Built by lane M (2026-10-23): (a); decided by Lars (2026-10-23): kept |
| FB28 | How clients and SPM-312 show a trusted message. (a) `NhAssistantApplicationMessageDto.Trusted` is a positional `bool` that is always written, `false` for every other application message and for messages stored before the flag existed, and `ApplicationMessageDetails.trusted` is a required `boolean`, since neither is released yet. The label "Instruction for the assistant" (Dutch "Instructie voor de assistent", `nh-assistant.thread.application-trusted`) sits in the author line after the sender and the "Application" tag, in the same muted tag style, without an icon; the mock's `postApplicationMessage` takes `trusted` (default `false`). SPM-312 posts its instruction only after the analysis message succeeded, with the same `ReadableBy` choice, the sender "Portfolio analysis", no title or link, no notification (the person was notified of the analysis) and the idempotency key `portfolio-analysis-instruction:<operation>`. Its text: "When the person asks about this portfolio analysis, first offer a short summary of its outcome and point to the analysis link in the message before this one for the outcome per project. Start no new analysis unless the person asks for one." (b) Write `trusted` only when true, as an optional client field, and notify of the instruction as well. | Built by lane M (2026-10-23): (a), for Lars to review |
| FB29 | How the thread finds the answer being written (FB32 (b)). (a) While a turn runs, `nh-assistant-thread` passes over the messages at the end that can arrive during a turn, the application's and other people's user messages; when an assistant message comes before them, that is the answer being written. The indicator then renders in that answer's row, and whether it shows is judged on that answer as if the arrived messages were not there, so an answer that streams text shows no indicator, as without them. Before the turn has an answer, as when an application message arrives after the person's own message, the indicator stays last. It needs no store state or new input, so custom layouts that pass `messages` and `streaming` get it too. In the instant between a colleague's question and its live `turn.started`, a conversation already marked running shows the indicator under the previous answer until the turn's answer arrives. (b) A `writingMessageId` that the store tracks from `turn.started` and the panel passes to the thread. | Built by lane M (2026-10-23): (a), for Lars to review |
| FB30 | Built by lane L (2026-10-23): (a), for Lars to review. A message that arrives during the turn sits where its sequence number puts it, so the tab shows what a reload shows. The server stores a turn's answer, empty, when the turn starts (`NhAssistantTurnRunner.CreateStateAsync`, also for a resumed turn) right after the person's message, and fills it later, so a message stored during the turn follows the answer that is still being written. `applyNhAssistantLiveEvent` places a message before the numbered messages at the end with a higher number; a message without a number (the optimistic message, the answer being written) counts as earlier. A message the server stored just before this tab's own message, but that arrives during the stream, sits after it until the conversation loads again. Alternative (b): number the answer when the turn ends, so arriving messages precede it; not chosen, because the answer's row exists from the start for its tool invocations and approvals. | Decided by Lars (2026-10-23): kept |
| FB31 | Built by lane L (2026-10-23): (a), for Lars to review. While a tab streams a turn it applies only `message.created` of the application's messages and of user messages that another writer wrote; the turn's events and the current user's own message come from the stream. The server publishes the sender's message to all of the sender's connections before it emits `turn.started`, so the live copy can arrive while the optimistic message still has its client id and would show twice. Without a known current user (`currentActorId`) every user message counts as the tab's own. Alternative (b): take the live copy in place of the optimistic message, which gives it its number earlier but adds state to the send flow. | Decided by Lars (2026-10-23): kept |
| FB32 | Built by lane L (2026-10-23): (a), for Lars to review. The working indicator stays below a message that arrived during the turn while the answer above it is still written, since the end of the thread no longer shows the answer's progress. The no-answer notice of a completed turn ignores other people's messages that arrived meanwhile (inside the store; `nhAssistantLatestTurnHasText` is unchanged). The mock API stays as it is: it numbers a turn's answer at the start like the server and posts application messages during a turn, but sends the sender's own message and turn only over the stream, not also as live copies. | Built by lane L (2026-10-23): (a); decided by Lars (2026-10-23): (b), the working indicator stays under the answer being written; built by lane M (2026-10-23) |
