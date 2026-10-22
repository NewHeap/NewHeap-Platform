# Files in Model Turns Plan

This document describes how a file, such as a PDF, reaches a model turn of the assistant: as a
chat attachment that a person uploads, and as the result of a tool. It is a plan, not usage
documentation: once the feature exists, the package READMEs, the consumer guide and the sample
case are authoritative.

## Status

Decisions F1–F11 taken by Lars on 2026-10-22; see [Decision record](#decision-record). F-1 is
implemented on `feat/ai-files-f1` (2026-10-22): file references, the file source, tool results with
files, native reading per profile, the limits, the classification, audit and retention, and the
opt-in per agent and skill in code and in the administration, with the `AcceptsFiles` migration for
SQL Server and PostgreSQL. The details it settled are in
[F-1 implementation decisions](#f-1-implementation-decisions), for Lars to review; the contracts
F-2 and F-3 build on are in [Contracts for F-2 and F-3](#contracts-for-f-2-and-f-3). F-2 is
implemented on `feat/ai-files-f2-readers` (2026-10-22): reading tools of the application
(`AddFileReader<T>()`), MCP servers set up as readers in the administration with their
classification bound and the consent rules, the reader choice, the text limits, the audit of the
reader and the `McpServerReaders` migration for SQL Server and PostgreSQL. Its details are in
[F-2 implementation decisions](#f-2-implementation-decisions), for Lars to review; what F-4 builds on
is in [Contracts for F-4](#contracts-for-f-4). F-3 and F-4 are not implemented.

## Problem

- A turn gives the model text only. `Microsoft.Extensions.AI` can carry a file as `DataContent`,
  but no NewHeap path produces it.
- A person cannot ask the assistant to open a document in the chat.
- Applications that need an agent to read documents build their own background operation around
  `INhAiChatExecutor`, read the file there and hand the outcome to a skill as an event. That
  bypasses the assistant's limits, classification checks, audit and approval flow.

## Goals

- A person can attach a file to a chat message, and a tool can return a file as its result.
- The model reads the file natively when its profile supports the file type; otherwise a tool
  that declares it can read the type turns it into text first.
- Applications decide where files live, who may read them and how they are classified.
- Limits, classification, audit and prompt-injection handling follow the assistant's existing
  rules, with defaults that an application can change.

## Non-goals

- NewHeap does not become a document store: it keeps references and hashes, not file content.
- No built-in PDF, Office or OCR engine and no reader package in the platform; reading a type the
  model cannot read is done by a tool the application or an MCP server provides. The sample shows
  one such tool; NewHeap intends to offer document reading later as an MCP server, which plugs
  into the same fallback (F3).
- No file output from the model (generating documents) in this plan.

## Decision record

| # | Question | Decision | Reason |
|---|---|---|---|
| F1 | How does a file enter a turn? | Two paths: a chat attachment that a person uploads, and a tool result that contains a file. | Covers both the person in the chat and the agent run that reads a document through a tool. |
| F2 | In what form does the model get it? | Natively, as file content, when the model profile declares the media type. Otherwise the runtime falls back to a reading tool (F3); without one the turn gets a stable error code, never a silent fallback. | Native reading keeps layout, tables and scans; an explicit error keeps what the model sees predictable. |
| F3 | Who picks the reading tool? | The runtime, from what the tool declares: the media types it accepts and that it returns text. MCP tools declare it through a setting on the MCP server in the administration, because MCP has no standard way to say it. The runtime calls the tool before the model sees the file and gives the model the text. | Predictable and auditable, and no extra model call. |
| F4 | Limits | Per file (bytes, pages), per turn (number of files, file tokens) and per agent or skill (lower only). NewHeap ships defaults; the application sets its own values in options. Exceeding a limit refuses with a stable error code; nothing is truncated silently. | Bounded cost per file and per turn, and the consumer decides what fits. |
| F5 | Classification | The application's file hook gives every file a classification; uploads get a configurable default, `Confidential` unless the application lowers it. A turn takes the highest classification of everything in it. The model profile, and a reading tool or MCP server in the fallback, must allow that classification, or the turn refuses. It combines with the classification per agent and skill. | A confidential document cannot slip through an agent configured as internal. |
| F6 | Retention | The thread stores a reference, the name, the media type and a hash, never the content. A later turn fetches the file again through the hook, if it still exists and the person or account of that turn may still read it. The application stores uploads in its own storage, such as the media library, with its own retention. | Follow-up questions about a document work, and the platform keeps no copies. |
| F7 | Prompt injection | File content is untrusted data, as retrieved context is (`NhAiContextTrust.UntrustedRetrieved`): marked as external data and never as instructions; it cannot unlock tools, rights or approvals; the audit holds the file id and hash, not the content; an evaluation case with a malicious document proves it. Whether an agent or skill accepts files is a setting (see F11). | Documents come from outside and must not steer the agent; some agents have no reason to read files at all. |
| F8 | Default limits | 20 MB and 100 pages per file; 5 files and 100,000 file tokens per turn. | Room for normal project documents while one turn stays bounded. |
| F9 | File types in the first version | PDF, PNG and JPEG natively where the profile declares them; Word and Excel documents through reading tools from the start. | Office documents are common in the applications that asked for this. |
| F10 | Reading tools from NewHeap | No reader package. The sample contains an example reading tool for Word and Excel that applications can copy; NewHeap will offer document reading later as an MCP server. | Keeps document libraries out of the platform packages; an MCP server serves every application without new dependencies. |
| F11 | Accepting files by default | New agents and skills accept no files until someone switches it on, in code or in the administration; the chat assistant accepts uploads by default. | Matches agents that start off: a deploy does not open a new data stream unnoticed, while uploading in the chat works out of the box. |

## Design

### File references and the application hook

- `NhAssistantFileReference`: an application-defined id plus name, media type, size and hash as
  the application reports them.
- `INhAssistantFileSource`, registered by the application:
  - `OpenAsync(reference, actor, cancellationToken)` returns `TaskResult<NhAssistantFile>` with
    the content stream, media type, size, page count where known, and its
    `NhAiDataClassification`. It checks that the actor of the turn may read the file and returns
    a failed result (not found or refused) otherwise.
  - `StoreUploadAsync(upload, actor, cancellationToken)` stores a chat upload and returns its
    reference, or a failed result for a refused type, size or virus-scan outcome.
- The sample implements the hook on the media library; the library ships no default
  implementation, so an application without the hook has files switched off.

### Chat attachments

- An upload endpoint under the assistant prefix accepts a file for a conversation, applies the
  per-file limits and the allowed media types before calling `StoreUploadAsync`, and returns the
  reference. The chat message then carries one or more references.
- Angular: an attach control in the chat composer, accessible by keyboard, with type and size
  checks before upload, progress, and errors shown at the control; texts in en and nl.
- The mock API in `@newheap/platform-ai-chat/testing` serves the endpoint with the same
  validation.

### Tool results with files

- `NhAiToolResult` can carry file references next to its text. The runtime resolves them through
  the hook with the turn's actor, so a tool cannot hand the model a file the actor may not read.

### Turning files into model content

1. Check the agent or skill accepts files (F11) and the per-turn limits (F4).
2. Open each file through the hook; check per-file limits and compute the turn classification
   (F5).
3. If the model profile declares the media type, add the file as `DataContent` to the message,
   marked as untrusted external data.
4. Otherwise find a reading tool that declares the media type and allows the classification:
   local tools first, then MCP servers configured as readers in the administration, with the
   existing MCP consent rules for personal connections. Call it, and add its text as untrusted
   retrieved context.
5. Without a native path or a reading tool, refuse with a stable error code that names the type.

- Model profiles get a list of accepted media types (for example `application/pdf`,
  `image/png`) next to `NhAiModelCapability`, so the check is per type rather than one flag.

### Limits

- `NhAssistantLimits` gains `MaxFileBytes`, `MaxFilePages`, `MaxFilesPerTurn` and
  `MaxFileTokensPerTurn`, with the defaults of F8; the application sets its own values, and agent
  and skill settings can only lower them.
- Each refusal has its own error code, registered for the front end.

### Audit and retention

- Messages and tool calls record file references (id, name, media type, hash), never content.
- Audit events carry the file id and hash. Evaluation reports name files by id.

## Security notes

- Every file read goes through the hook with the turn's actor, both for uploads and for tool
  results, so file access follows the application's authorization.
- Sending a file to an external MCP server is data leaving the platform. It requires that the
  classification allows that server and, for personal connections, the person's consent.
- File content never reaches logs or telemetry.

## Phases

| Phase | Content |
|---|---|
| F-1 | File references, the hook, tool results with files, native reading per profile, limits, classification, audit, accepting files per agent and skill. Library tests on SQL Server and PostgreSQL. |
| F-2 | Reading tools: local tool declarations and the MCP reader setting in the administration, with classification and consent checks. |
| F-3 | Chat attachments: upload endpoint, Angular composer control, mock API. |
| F-4 | Sample case on the media library (upload a project document and ask about it; an agent skill that reads a document through a tool), the example Word and Excel reading tool, an evaluation case with a malicious document, guidance and release notes. |

## F-1 implementation decisions

Each line: the decision, why, and its status.

| # | Decision | Reason | Status |
|---|---|---|---|
| D1 | The file reference is `NhAiFileReference` in `NewHeap.Platform.AI.Common`, not `NhAssistantFileReference`. | `NhAiToolResult`, the generator and MCP tools live in AI.Common, which cannot reference the assistant; one type serves tools, the file source and the thread. | For Lars to review |
| D2 | A tool names files through data that implements `INhAiToolResultFiles` or `NhAiToolResult.Create(result, options, files)`; the envelope gets `files` next to `data`, at most 16, on success only. | Generated tools get it through their existing marshaller; the shape stays one envelope for every protocol. | For Lars to review |
| D3 | A tool's files reach the model in one user message after the tool results of its round; the governed model call (`INhAiChatExecutor`) moves them there. | Most providers accept files only in user messages, and every tool result must follow its call directly. | For Lars to review |
| D4 | A model call is classified as the most sensitive file it carries, in `INhAiChatExecutor`; the assistant refuses a file up front when no profile may receive the turn's new classification. | A confidential file then moves the calls to the fallback that permits it instead of reaching a profile that may not take it. | For Lars to review |
| D5 | "The profile accepts the type" means the profile that would serve the turn with the file's classification, as the resolver picks it, declares the type and still reads every file the turn already holds. | Fallbacks can serve the turn; a file must never reach a model that cannot read it. | For Lars to review |
| D6 | File tokens are estimated: 1,000 per page, an image one page, a file without a page count one page per started 100 KB; the turn's files must also fit the serving profile's input budget. | F8's 100 pages and 100,000 tokens per turn imply about 1,000 tokens per page; providers do not report file tokens up front. | For Lars to review |
| D7 | The files of one tool result or message count together against `MaxFilesPerTurn`: when they do not fit, all are refused. | No silent truncation (F4) and no choice of which files the model sees. | For Lars to review |
| D8 | Refusals in a tool result are per file (`attached`, `code`, `message` in `files`) and the call stays successful; a refused attachment ends the person's turn with the first code. | The tool's data is still valid; the person who attached a file must hear why it was not read. | For Lars to review |
| D9 | The source's failures pass through as `assistant-file-not-found` or `assistant-file-forbidden`; any other code or exception becomes `assistant-file-unavailable`. | Only registered codes reach the front end; an exception may carry file content. | For Lars to review |
| D10 | The chat assistant is an agent without run settings, in code or in the administration; an agent with run settings needs `AcceptsFiles()`, and in a skill both the agent and the skill must accept files. | F11: a new skill never inherits a stream of documents; it matches the "off" state, which also applies to configured agents. | For Lars to review |
| D11 | Code limits of an agent or skill above the application's stop the host; the administration stores only the switch (`acceptsFiles`), without lower limits. | Invalid configuration fails explicitly; lower limits for data agents can follow when someone needs them. | For Lars to review |
| D12 | A later turn opens the files of the history it replays again, newest first and each once, within the per-turn limits counted on their own; a file it cannot open is named with the code. Consultations open no files of the thread. | Earlier files never crowd out the files of the current turn; a model call carries at most twice the per-turn limits. | For Lars to review |
| D13 | The thread and the audit record the SHA-256 the runtime computed over what the model read (`sha256:<hex>`), next to the application's own hash. | The audit then proves which content the model saw, also when a re-opened file changed. | For Lars to review |
| D14 | `use_skill` refuses a skill that cannot take the files the turn already reads; rule turns process no files. | The files stay in the conversation when the turn switches skills; rule tools only propose. | For Lars to review |
| D15 | The runtime already reads attachments (`NhAssistantMessageTurnRequest.Files`, internal); the HTTP field, the upload endpoint and the composer stay F-3. | The checks, limits, persistence and re-opening are shared with tool results and are tested in F-1. | For Lars to review |
| D16 | HTTP statuses: `files-unavailable` and `file-not-found` 404, `file-forbidden` 403, `file-too-large` 413, `file-type-not-supported` 415, `files-not-accepted` 409, `file-unavailable` 503, the other file codes 400. | One status per code in the shared table, as for every assistant code. | For Lars to review |
| D17 | The audit events are `FileRead` and `FileRefused`, with `FileId`, `FileMediaType`, `FileHash` and the origin (`NhAssistantFileOrigins`) as `ObjectId`. | Content-free, and the existing event shape names the tool and invocation. | For Lars to review |
| D18 | An approved call that returns files gives them to the model as any tool call does. | Same rules for every tool result that reaches the model. | For Lars to review |
| D35 | In the business audit event `FileId` and `FileHash` count as identifiers and `FileMediaType` as a code, and `FileReaderId` (F-2) is an identifier too; the test that allows only identifiers, codes and timestamps names exactly these four, and the file's name never enters the event. | The application's id, a normalized media type and `sha256:<hex>` carry no free text or content, as `SubjectId` and the other hashes do not. | For Lars to review |

## Contracts for F-2 and F-3

- **Public, stable:** `NhAiFileReference`, `NhAiMediaTypes`, `INhAiToolResultFiles`,
  `NhAiToolResult.FilesPropertyName`, `MaxFiles`, `Create(result, options, files)` and `TryGetFiles`;
  `NhAiModelProfileBuilder.AcceptMediaTypes`, `NhAiModelProfile.AcceptedMediaTypes` and
  `AcceptsMediaType`; `INhAssistantFileSource` with `OpenAsync` and `StoreUploadAsync`,
  `NhAssistantFile`, `NhAssistantFileActor`, `NhAssistantFileUpload`, `NhAssistantFileOrigins`;
  `UseFileSource<T>()`; `NhAssistantLimits.MaxFileBytes`, `MaxFilePages`, `MaxFilesPerTurn` and
  `MaxFileTokensPerTurn`; `AcceptsFiles()` on agents and skills with `NhAssistantFileLimits`; the
  eleven file error codes and the `FileRead` and `FileRefused` audit events.
- **F-2 seam:** the internal `INhAssistantFileReader` in `NhAssistantTurnFiles`. When the serving
  profile does not declare a file's type, the runtime reads the content and asks the registered
  reader for text (`NhAssistantFileReadRequest` with reference, media type, content, classification
  and actor); `null` means no reader declares the type and the file is refused with
  `assistant-file-type-not-supported`. The text reaches the model as untrusted external data with
  the same marker and annotations, and `NhAssistantTurnFile.Native` is false. F-2 registers the
  reader that chooses local reading tools first, then MCP servers set up as readers, and checks
  their classification and consent. *F-2 replaced the internal seam with the internal
  `NhAssistantFileReaders` and made the request public (`NhAssistantFileReadRequest`) for the public
  `INhAssistantFileReader`; see [F-2 implementation decisions](#f-2-implementation-decisions).*
- **F-3 seam:** `NhAssistantMessageTurnRequest.Files` takes the references a message carries; the
  turn checks them before it claims the conversation and opens them with the person's rights. A
  person's message stores them as `file` parts (`NhAssistantStoredPart.FileType`, reference and
  SHA-256) that the conversation reader does not show yet; F-3 adds the part to the message view,
  the upload endpoint that calls `StoreUploadAsync` after the per-file limits and media types, the
  composer control and the mock API.

## F-2 implementation decisions

Each line: the decision, why, and its status.

| # | Decision | Reason | Status |
|---|---|---|---|
| D19 | An application registers a reader with `AddFileReader<TReader>(readerId, reader => reader.AcceptMediaTypes(...).PermitDataClassifications(...))`; `INhAssistantFileReader.ReadAsync(NhAssistantFileReadRequest)` returns `TaskResult<string>`. The declaration lives at registration, and "returns text" is the contract of the interface, not a flag. | Like a model profile, the application decides at composition what a reader may receive; one class can serve several declarations; F-2 has text output only. | For Lars to review |
| D20 | A reader of the application permits `Public` and the classifications it declares, nothing more by default, as a model profile does. | One meaning for "permits" in the platform; uploads default to `Confidential`, so a reader of uploads is an explicit decision. | For Lars to review |
| D21 | An MCP server set up as reader serves the turns of every agent without being assigned to it, and its reading tool need not be enabled for agents; the server must be enabled, and its required policy, headless use, the person's consent (Q3) and the initiator rule (I18, I21) apply as for its tools. | The runtime calls the reader, not the model; enabling the tool would offer it to the model, and assigning a document reader to every agent is administration without a safety gain over the classification bound. | For Lars to review |
| D22 | The reading tool must be one the administrator synced: saving validates `reader.toolName` against the synced catalog (`not-found`), so a new server gets its reader after a sync. At runtime a tool the server no longer lists, or whose input schema differs from the synced one, passes the server over (`assistant-mcp-tool-not-found`, `assistant-mcp-schema-changed`). | The review rule of synced MCP tools also holds for the tool that receives documents. | For Lars to review |
| D23 | The runtime calls the reading tool with `name`, `mediaType` and the base64 `content` of the file and gives the model the text blocks of the result, joined by new lines; `isError` or no text block is a failure. | MCP has no standard for reading a file (F3); this is the smallest contract a document-reading MCP server can offer. | For Lars to review |
| D24 | Readers of the application are asked in registration order, MCP servers by id, ordinal; the order of servers is not configurable. | Deterministic and the same on both providers; a reader setting per media type keeps overlaps rare. | For Lars to review |
| D25 | A reader that may not receive the file (classification, policy, headless use, consent, initiator, a connection that cannot be planned or reached, a missing or changed tool, a request refused before it left the host) is passed over and the next one is asked. A reader that received the file and failed ends it; the file never goes to a second reader. When every reader that declares the type passed over, the code of the first applies, such as `assistant-classification-not-permitted` or `assistant-mcp-consent-required`. | A document leaves the platform at most once and only to a reader that may have it; the code says what would change the outcome. | For Lars to review |
| D26 | Every failure of a reader (a failed result whatever its code, an exception, an MCP error result, no text) is `assistant-file-reader-failed`, HTTP 502, per file as in D8; the reader's messages are neither logged nor shown. | Only registered codes reach the front end (D9), and a reader's message may quote the document. | For Lars to review |
| D27 | The limits count a reader's text, one token per started four characters as retrieved context does, against `MaxFileTokensPerTurn` and the profile's input budget, instead of the page estimate (D6); a file is sent to a reader only while at least one token still fits. `MaxFileBytes` and `MaxFilePages` still apply to the file. | F-2 asks to count the returned text; a turn without room never sends a document out. | For Lars to review |
| D28 | `NhAssistantAuditEvent.FileReaderId` names the reader of `FileRead` and `FileRefused`: the reader id, or `mcp.<server>` with `McpConnectionOwner`; for a file a reader read, `FileHash` (and the thread's hash) is the SHA-256 of the file content the reader received, not of the text. | The audit proves which document left the platform and through which reader; a reader's text may differ per call. | For Lars to review |
| D29 | `INhAiChatExecutor` no longer adds the annotated tokens of reader text that sits directly in a message, which the caller already counts as message text; text carried in a tool result still counts. | An attachment a reader read was otherwise counted twice against the profile's input budget. | For Lars to review |
| D30 | The setting is stored on `AssistantMcpServer` in three nullable columns: the normalized media types separated by spaces (at most 16), the tool name (at most 128) and the classification name. Input `reader: null` keeps it, an empty `mediaTypes` removes it; field errors are `reader.mediaTypes` (`invalid`, `too-long`), `reader.toolName` (`required`, `invalid`, `not-found`) and `reader.maxClassification` (`required`, `invalid`) under `assistant-validation`. | Follows the server's other settings and the field-error conventions of the administration. | For Lars to review |
| D31 | The editor shows "Reading documents" only for a server that exists, chooses the tool from its synced tools and sends the setting with every save; the classification names reuse the skill classification texts. | A new server has no synced tools yet; one set of classification texts. | For Lars to review |
| D32 | A file a reader read is read again when a later turn opens it again (D12); an MCP reader receives it again. | The platform keeps no copies, also not of a reader's text (F6). | For Lars to review |
| D33 | A person's turn does not show a connection card for a personal OAuth reader; the file is refused with the code of the connection, such as `assistant-mcp-connection-required`. | Keeps F-2 to the reader; connecting stays in the preferences and the tool path. | For Lars to review |
| D34 | The marker before a reader's text says that a reading tool made the text of the file. | The model knows it reads an extraction, not the layout. | For Lars to review |

## Contracts for F-4

- **Reader of the sample:** implement `INhAssistantFileReader` in the sample for Word and Excel
  (`NhAiMediaTypes.Docx`, `NhAiMediaTypes.Xlsx`) with its own document library, and register it with
  `AddFileReader<T>("office-documents", reader => reader.AcceptMediaTypes(...).PermitDataClassifications(...))`
  next to the media-library file source; the platform packages stay free of document libraries
  (F10). The reader gets the file's bytes in `NhAssistantFileReadRequest.Content` and returns text or
  a failed result, which the turn reports as `assistant-file-reader-failed`.
- **MCP reader:** a document-reading MCP server offers one tool that takes `name`, `mediaType` and the
  base64 `content` and returns text blocks; an administrator syncs the server and sets `reader` with
  its media types and classification bound. NewHeap's own document reader server can follow this
  contract later.
- **Malicious document evaluation:** a reader's text reaches the model behind the external-file marker
  as untrusted retrieved data (`newheap.trust` `untrusted-retrieved`), never changes the tools the
  turn offers and is never logged or audited; the evaluation case can assert this on the model
  request, the tool list and `FileRead` with `FileReaderId` and `FileHash`, as
  `AssistantFileReaderTests` does with an instruction in the reader's text.

## Open questions

None; the questions of the first draft are answered as F8–F11.
