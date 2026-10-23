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
FB2, FB3, FB4, FB7, FB8, FB10, FB12 and FB13's live updates; those changes are not built yet.

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
`AssistantMessage` with the new role `application`, author `application` and its title, link and
sender in an `application` part next to the text part, so no migration is needed. It gets the next
sequence number, is published live (`message.created`, then the conversation's changed state),
optionally notified, audited as `ApplicationMessagePosted` and given to the model in later turns.
`nh-assistant-thread` renders it; agent threads open in the same panel, so both views are covered.
SPM-312 shows it from the portfolio analysis.

- **FB6 — A new message role, not a user message with a special author.** Every check for "the
  person's latest message" (`Role == user`) keeps ignoring application messages, so a title, a
  consultation's question or a resumed turn never takes the application's text for the person's.
  The history replay maps the role to a user message for the model, with the marker below.
- **FB7 — How the model reads it.** A user message that starts with `[Application]` (its Dutch
  equivalent in a Dutch turn) and says that it is information from the application, not a request from the
  people in the conversation and never instructions, followed by an `application-message` block with
  the sender, title, link and text; angle brackets in the content are replaced, as in the retrieved
  context, so the content cannot close the block. The application is treated as an untrusted
  source of information, like retrieved context, because its messages can carry user-generated data.
  Conversation snapshots for reviewed MCP tools leave application messages out, as they leave out
  everything but people's and the agent's text.
- **FB8 — Text is always Markdown.** The chat renders it with the sanitizing Markdown pipe of the
  agent's answers; plain text is valid Markdown. No `Format` option.
- **FB9 — Links.** A path within the application (`/…`, not `//` or `/\`) or an absolute `https`
  address, the rule of subject links, which the client now shares (`nhAssistantLinkTarget`); any
  other link is `assistant-message-invalid` on the server and shows nothing in the client. Bounds:
  text `MaxMessageChars`, title 200, sender 100, link 2,048, label 100, idempotency key 100.
- **FB10 — Notifications by Web Push to the conversation's people, opt-in per message.** `Notify`
  sends the new push kind `application-message` to the owner and participants of a chat and to the
  participants of an agent thread (the people of the subject who joined it), with the conversation's
  title and a fixed text, never the message. A shadow thread never notifies. The agent notification
  policy and channels are not used: they need a subject, a chat has none, and a new
  `NhAssistantAgentNotificationKind` would reach every application's custom policy and channels.
  Alternative for threads: a new agent notification kind so the in-app channel and the policy see it.
- **FB11 — Idempotency key optional.** With a key the conversation keeps one application message
  per key (stored as client message id `application:<key>`) and a repeated post returns the earlier
  message with `Duplicate`, without publishing, notifying or auditing again. Without a key every post
  adds a message. **Closed** on branch `fix/sequence-lock-and-shadow-test`: the look-up of an earlier
  application message with the key and the insert run in one transaction that holds the
  conversation's message lock (FB13), so two posts with the same key at the same moment store one
  message. There is still no unique index on client message ids, so no migration.
- **FB12 — Open conversation and access.** "Open" means not archived: an archived conversation is
  `assistant-conversation-not-found`, as an unknown one, and a running or waiting conversation
  accepts the message (it does not start a turn). The API checks no person's access by itself;
  `ReadableBy` (`NhAssistantConversationPerson`) makes the post depend on that person being the
  owner or a participant of the same tenant, which the sample uses because the conversation id comes
  from the person's client. Posting while the assistant is disabled is `assistant-disabled`.
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
  `ObjectId` the message id, `ResultCode` `posted` or `notified`, and in a thread `SubjectType` and
  `SubjectId`; no new audit fields.

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
| FB1 | (b) An application can map its own extensions: `NhAssistantUploadOptions.MapExtension(extension, mediaType)`. | The server's derivation uses the mapping; the upload rules give it to the composer, so client and server derive alike. |
| FB2 | Kept, plus `text/xml` as an alias of `.xml`: browsers send `text/xml`, the table names `application/xml`. | The alias in the server's and the client's table, with tests. |
| FB3 | Kept. | The release notes' breaking change also says that an application that accepts both types now receives a `.csv` as `text/csv` instead of `application/vnd.ms-excel`. |
| FB4 | (b) Text types are refused when their content starts with a known binary signature. | For `text/*`, `application/json` and `application/xml`: OLE2 (`D0 CF 11 E0`), ZIP, PDF, PNG, JPEG and GIF. No NUL-byte check, which would refuse UTF-16 text. Also a fix found in the review: `.pptx` gets the ZIP signature check that `.docx` and `.xlsx` have, so FB2's ZIP aliases hold for it. |
| FB5 | Kept. | — |
| FB6 | Kept. | — |
| FB7 | (b) An opt-in `NhAssistantApplicationMessage.Trusted`: the model follows a trusted message as the application's instruction; the application guarantees that it carries no user-generated data. | Its own marker and sentence for the model, the flag in the `application` part (no migration), the flag in the audit, MCP snapshots still leave application messages out, and guidance and release notes warn against user data in trusted text. |
| FB8 | (b) Text stays Markdown; a public `NhAssistantMarkdown.Escape(string)` lets applications put user data into it without formatting. | The helper, its tests and guidance. |
| FB9 | Kept. | — |
| FB10 | (b) Web Push and, when the application turned on in-app agent notifications (`UseInAppAgentNotifications`), an in-app message through `INhUserNotificationSink`, for chats and threads alike; no new agent notification kind. | Its own category, one group per conversation, a fixed text without the message, the recipient's culture from `INhActorDirectory`, never for a shadow thread; `ResultCode` `notified` covers both. |
| FB11 | Kept: a repeated key returns the earlier message with `Duplicate`, whatever its content. | — |
| FB12 | The access choice is required: a post names `ReadableBy(person)` or explicitly `WithoutPersonCheck()`, otherwise `assistant-message-invalid`; the rest as built. Not breaking, the API is not released yet. | The builder methods, the check, the sample and its test, guidance. |
| FB13 | Lock timeout kept: after 15 seconds the message is not stored (`TimeoutException`). Live updates changed: a tab that streams a turn applies `message.created` meanwhile. | Built on branch `fix/live-messages-while-streaming`: the client places an application's or a colleague's message that arrives during the turn where its sequence number puts it, which is after the answer that is still being written, not before it: the server numbers a turn's answer when the turn starts (FB30). |
| FB14 | Kept: 70. | The audit carries FB7's trusted flag. |
| FB30 | Built by lane L (2026-10-23): (a), for Lars to review. A message that arrives during the turn sits where its sequence number puts it, so the tab shows what a reload shows. The server stores a turn's answer, empty, when the turn starts (`NhAssistantTurnRunner.CreateStateAsync`, also for a resumed turn) right after the person's message, and fills it later, so a message stored during the turn follows the answer that is still being written. `applyNhAssistantLiveEvent` places a message before the numbered messages at the end with a higher number; a message without a number (the optimistic message, the answer being written) counts as earlier. A message the server stored just before this tab's own message, but that arrives during the stream, sits after it until the conversation loads again. Alternative (b): number the answer when the turn ends, so arriving messages precede it; not chosen, because the answer's row exists from the start for its tool invocations and approvals. | — |
| FB31 | Built by lane L (2026-10-23): (a), for Lars to review. While a tab streams a turn it applies only `message.created` of the application's messages and of user messages that another writer wrote; the turn's events and the current user's own message come from the stream. The server publishes the sender's message to all of the sender's connections before it emits `turn.started`, so the live copy can arrive while the optimistic message still has its client id and would show twice. Without a known current user (`currentActorId`) every user message counts as the tab's own. Alternative (b): take the live copy in place of the optimistic message, which gives it its number earlier but adds state to the send flow. | — |
| FB32 | Built by lane L (2026-10-23): (a), for Lars to review. The working indicator stays below a message that arrived during the turn while the answer above it is still written, since the end of the thread no longer shows the answer's progress. The no-answer notice of a completed turn ignores other people's messages that arrived meanwhile (inside the store; `nhAssistantLatestTurnHasText` is unchanged). The mock API stays as it is: it numbers a turn's answer at the start like the server and posts application messages during a turn, but sends the sender's own message and turn only over the stream, not also as live copies. | — |
