# Files in Model Turns Plan

This document describes how a file, such as a PDF, reaches a model turn of the assistant: as a
chat attachment that a person uploads, and as the result of a tool. It is a plan, not usage
documentation: once the feature exists, the package READMEs, the consumer guide and the sample
case are authoritative.

## Status

Decisions F1–F11 taken by Lars on 2026-10-22; see [Decision record](#decision-record). Not
implemented. Phase 1 builds on the agent "off" state and the data classification per agent and
skill, which are being built on `feat/agent-off-state`.

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

## Open questions

None; the questions of the first draft are answered as F8–F11.
