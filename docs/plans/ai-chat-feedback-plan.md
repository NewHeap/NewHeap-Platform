# AI Chat Feedback Plan

This document records three improvements of the assistant chat that came from consumer feedback,
what was built for each and the choices made while building them. Lars decided the improvements;
the choices FB1 to FB14 were open and are made here **for Lars to review**. It is a plan, not usage
documentation: the guidance rules `nh-ai-assistant` and `nh-ai-assistant-ui`, the release notes and
sample cases SPM-305, SPM-310 and SPM-312 are authoritative for usage.

## Status

Implemented on 2026-10-23 on branch `feature/chat-feedback`, with sample case SPM-312 added and
SPM-305 and SPM-310 extended. The choices below wait for Lars's review.

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
  message with `Duplicate`, without notifying or auditing again. Two posts with the same key at the
  same moment can both be stored: the store has no unique index on client message ids, and adding one
  needs a migration over existing data. Without a key every post adds a message.
- **FB12 — Open conversation and access.** "Open" means not archived: an archived conversation is
  `assistant-conversation-not-found`, as an unknown one, and a running or waiting conversation
  accepts the message (it does not start a turn). The API checks no person's access by itself;
  `ReadableBy` (`NhAssistantConversationPerson`) makes the post depend on that person being the
  owner or a participant of the same tenant, which the sample uses because the conversation id comes
  from the person's client. Posting while the assistant is disabled is `assistant-disabled`.
- **FB13 — Ordering and the conversation list.** The message gets `max(sequence) + 1`, as every
  message does; a post at the same moment as a turn's own message can share its sequence number,
  as two concurrent turns could before. It moves the conversation's update time, so it rises in the
  people's lists, except while a turn runs, whose update time tells an abandoned turn from a live
  one. A tab that streams a turn of that conversation ignores its live updates meanwhile, as for a
  colleague's message, so it shows an application message that arrived during the turn when it next
  loads the conversation.
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
