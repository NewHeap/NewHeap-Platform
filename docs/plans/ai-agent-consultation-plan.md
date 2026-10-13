# Agent Consultation Plan

This document describes how an assistant, and an agent turn with a person's delegated rights,
consults another registered agent about a subject. Example: the general project assistant asks
the work-preparation agent "what is still missing for project X?" and uses the answer in its own
reply. It is a plan, not usage documentation: once the feature exists, the package READMEs, the
consumer guide and the sample case are authoritative.

It builds on [ai-autonomous-agent-runs-plan.md](ai-autonomous-agent-runs-plan.md), which listed
"multi-agent orchestration (agents calling agents)" as a non-goal. This plan lifts that non-goal
for two narrow patterns, a synchronous consultation and an asynchronous request, and keeps every
other kind of orchestration out of scope.

## Status

Decisions taken on 2026-10-13; see [Decision record](#decision-record). Implemented on
2026-10-13 with sample case SPM-278; the package READMEs, the guidance rule
`ai/nh-ai-agent-runs.md` and the consumer guide are authoritative from here on.

## Problem

- There is no agent-as-tool mechanism. A model only sees governed tools, and no tool runs
  another agent.
- `INhAssistantAgentTriggers.StartAsync` starts the other agent asynchronously, but only from
  application code; a chat cannot ask for it, and nobody tells the asker when the answer is there.
- `INhAiArtifacts`, working memory and run summaries are scoped to the thread of the running
  turn, so another agent's knowledge about a subject is unreachable from a chat.

## Goals

- **Consult (synchronous).** A chat assistant asks a registered agent a question about one subject
  and receives the answer as a tool result in the same turn. The consulted agent answers from what
  it knows about the subject (working memory, run summaries, artifacts, recent thread history) and
  its tools, with the asker's rights. It may leave deferred proposals in its own thread.
- **Request (asynchronous).** A chat assistant asks a registered agent to work on a subject. This
  starts an ordinary run in the agent's subject thread with the asker as initiator; the asker is
  notified when it ends.
- A consult never reveals more than the asker could read in the target thread themselves.
- Opt-in on both sides, bounded in cost and depth, audited, and identical with and without an
  HTTP request.

## Non-goals

- General multi-agent orchestration: planners, negotiation, shared scratchpads, fan-out.
- A consult that executes a change. Changes are only proposed and are decided by the subject's
  approvers.
- Posting the answer of a request back into the asking chat. The answer stays in the agent's
  thread; a later consult can read it from the run summary.

## Decision record

| # | Decision | Rationale |
|---|---|---|
| C1 | **Both** a synchronous consult and an asynchronous request, as two generated tools per consultable agent. | A consult answers now; a request lets the agent do real work in its own thread, with its own approvals. |
| C2 | A consult runs with the **asker's rights only** (delegated). The target agent's own grants, including `TriggerOnly` grants, never apply. Thread content the target obtained with its own grant is filtered out for an asker who could not have run that tool. A request is an ordinary run with the asker as initiator, with the same rights as `POST threads/start`. | No escalation through a consult; the existing restricted-result rule (D5) stays intact. |
| C3 | A consult may read and **propose**. Every non-read tool call becomes a **deferred** proposal in the target agent's subject thread, whatever the tool's own approval setting. Proposals carry their origin (consultation, asker, asking agent), execute with the asker's rights only, and are decided by the subject's approvers through the target agent's approval policy. No memory updates and no artifact saves in a consult. A request may do everything a run may do. | The target agent's expertise includes proposing work, but nothing happens without the subject's approvers, and a consult cannot launder the target's grants through a proposal. |
| C4 | Generated governed tools `assistant.consult.{agentId}` and `assistant.request.{agentId}` in an attested catalog, `AllowAgentGrant = false`, two keys: the target agent opts in with `AllowConsultation`, the asking agent selects the tool id. Exposure is configured per registration: default `Local \| Agent`; `Mcp` only when the registration asks for it. | Explicit consent on both sides; MCP clients get consults only when the application chooses that. |
| C5 | Limits are configurable with these defaults: 4 tool calls, 60 seconds (capped by the asking turn's deadline) and 4,000 answer characters per consult; depth 1 (`NhAssistantLimits.MaxConsultationDepth`, at most 3, with a refusal for any chain that already contains the target agent); consult usage counts toward the target agent's daily budget and inner tool calls toward the asker's daily tool budget; 50 consults per asker per day (`NhAssistantLimits.MaxConsultationsPerActorPerDay`). Requests use the existing run guardrails. | Every consult is a model turn; cost and recursion must be bounded by default and adjustable per application. |
| C6 | A consult leaves a **content-free consultation record** in the target thread's timeline: who, when, via which agent, outcome, usage and proposal count. The question and answer stay in the asking conversation and never enter the history of later runs. | Transparency for subject members without exposing the asker's conversation or injecting it into later runs. |
| C7 | Audit: `NhAssistantAuditEventKind.AgentConsulted` and `AgentRequested`; inner tool calls and proposals relay `ToolInvoked` and `ApprovalRequested` with the consultation id and depth (additive init-only fields). No question or answer text in audit. | Complete, content-free trail per consult. |
| C8 | The subject type is fixed per registration; the model supplies a `subjectId` and the question. The asker passes the tool's policy and, live through `INhAiSubjectAccessResolver`, has at least `Reader` for a consult (configurable), at least `Participant` before non-read tools are offered in a consult, and at least `Participant` for a request. Unknown subjects and missing access return the same code. | Mirrors the thread rules: readers read, participants type and therefore may cause proposals. No probing for subjects. |

## Design

### Shape

```mermaid
sequenceDiagram
    participant U as Person (chat)
    participant A as Asking turn
    participant T as assistant.consult.{agent}
    participant C as Consult runner
    participant W as Target agent (consult turn)
    participant TH as Target thread
    U->>A: "What is still missing for PRJ-12?"
    A->>T: {subjectId, question}
    T->>T: invoker + gate: asker passes the tool policy
    T->>C: consult(subject, question, asker, chain)
    C->>C: role >= Reader, agent enabled and not shadow, depth, budgets
    C->>W: consult instructions + continuity filtered for the asker + question
    W-->>C: reads with the asker's rights; non-read calls become proposals
    C->>TH: consultation record (+ deferred proposals)
    C-->>T: bounded answer, consultationId, proposal ids
    T-->>A: tool result
```

### Registration

```csharp
services.AddNewHeapAssistant(assistant => assistant
    .ConfigureAgent(SampleIntakeReviewerComposition.AgentId, agent => agent
        .AllowConsultation(ProjectSubjects.Type, consult => consult
            .Description("Knows the intake review of a project: checks done, last verdict and open points.")
            .Policy(SampleAssistantComposition.AccessPolicy)       // the asker must pass it
            .MinimumRole(NhAiSubjectRole.Reader)                    // live, per consult
            .Limits(maxToolCalls: 4, timeout: TimeSpan.FromSeconds(60), maxAnswerChars: 4_000)
            .AllowRequests()                                        // also generates assistant.request.*
            .Exposure(NhAiToolExposure.Local | NhAiToolExposure.Agent))));

// Asker: the project assistant selects the generated tools.
ToolSelectors: ["projects.*", "assistant.consult.sample-project-intake-reviewer.*"]
```

- `AllowConsultation` generates the tools in an attested catalog of `AI.Chat.AspNet`. The subject
  type is part of the id, so adding a second subject type later never renames a tool:

  | Tool | Effect | Input | Output |
  |---|---|---|---|
  | `assistant.consult.{agentId}.{subjectType}` | `ReadOnly` (the consult itself changes nothing; proposals wait for approvers) | `subjectId`, `question` | `answer`, `consultationId`, `proposalCount` |
  | `assistant.request.{agentId}.{subjectType}` | `IdempotentMutation` with required idempotency, approval not required (it starts a run, like `POST threads/start`) | `subjectId`, `message` | `runId`, `threadId`, `outcome` |

- One registration per subject type. The tool description comes from the registration, so the
  asking model knows when to consult.
- The tools return `{ success, data }` or `{ success: false, code, message }` to the model, so
  it can act on a refusal code; the invoker's audit record still classifies the outcome.
- An application whose `INhAiToolDiscoveryPolicy` allows only known tool prefixes must allow
  `assistant.consult.` and `assistant.request.`; the sample's policy shows it.

### Rights and filtering (C2, C8)

- The asker passes the tool policy through the normal invoker and gate: the request principal in
  a chat or MCP call, the delegated initiator in an agent turn. `AllowAgentGrant = false`, so an
  agent turn without a delegating initiator never sees the tools.
- Live role check with `INhAiSubjectAccessResolver.ResolveAsync`; a missing subject and missing
  access both return `assistant-consult-subject-unavailable`.
- The consult turn runs in an `NhAiAgentInvocationScope` of the target agent with `Grants = []`,
  `DelegateFromInitiator = true`, the asker as initiator and a new `Consultation` marker. The
  agent gate then allows only what the asker may run (`GrantSource = Delegated`).
- The consult's input is filtered for the asker with the thread view's rule. Replayed thread
  history never carries tool results (only the compact tool-call summary), so the filter applies
  to artifacts: an artifact saved by a call under an agent grant is left out of the continuity
  block and cannot be read unless the asker passes that tool's policies. Working memory and run
  summaries are included, as for any reader of the thread.

### Proposals from a consult (C3)

- Non-read tools of the target are offered only when the asker has `Participant` or more.
- An effect-policy decorator turns every allowed non-read call inside a consult into
  `RequireApproval`, whatever the registered effect policy decides.
- The proposal is stored in the target thread (created on demand) as a deferred approval with the
  target agent's policy for that tool, `ConsultationId`, `InitiatorActorId` = asker and
  `GrantSource = Delegated`. The model receives the usual "proposal recorded" result.
- Execution of a consult proposal binds the agent scope with `Grants = []` and delegation from the
  initiator, so it runs with the asker's rights at execution time or fails.
- Subject approvers are notified as for a run with proposals.

### Execution

- `NhAssistantConsultationService` (internal, `AI.Chat`) checks access, limits and budgets, gets
  or creates the target thread (it holds the consultation record and any proposals) and runs a
  consult turn through the turn engine: agent creation, governed functions, the interceptor and
  the audit relay, with a consult turn scope (target thread as conversation, asker as owner,
  consultation id as turn id).
- It never claims the target thread's turn lock, so a consult works while a run is active, and it
  writes no messages into the thread. Inner tool calls live in memory and audit; only proposed
  calls are stored, because a proposal needs its stored invocation.
- `memory_get` is offered; `memory_update` is not. `INhAiArtifacts.SaveAsync` refuses in a consult;
  `GetAsync` reads artifacts of the target thread that passed the filter.
- Consult tools are offered inside a consult only while the depth allows it, never for an agent
  already in the chain.

### Requests (C1)

- `assistant.request.{agentId}` fires the new built-in trigger `agent-request` through the
  trigger pipeline with the asker as initiator, the asking turn's tool invocation as dedupe key
  and `Queue` on conflict. The message is stored in the thread as the asker's message.
- When the run ends, `NhAssistantAgentNotificationKind.RequestAnswered` notifies the initiator by
  default, with a link to the thread.

### Record and audit (C6, C7)

- `AssistantConsultation` in `nhai` (SQL Server and PostgreSQL migrations): id, conversation id
  of the target thread, scope key, target agent, subject, asker, asking agent, asking
  conversation and turn, depth, status, failure code, tool calls, tokens, proposal count, created
  and ended at. `AssistantApproval` gains `ConsultationId`. Consults refused before access is
  established leave only an audit event.
- The target agent's daily usage query adds consultations; a per-asker count enforces the daily
  cap. `GET threads/{id}/consultations` lists them for subject readers, and the thread view shows
  them in its timeline.
- Subject retention deletes consultation rows with the subject's threads.

### Refusal codes

`assistant-consult-subject-unavailable`, `assistant-consult-agent-unavailable` (disabled, shadow
or not consultable), `assistant-consult-depth-exceeded`, `assistant-consult-budget-exhausted`,
`assistant-consult-limit-reached`, `assistant-consult-timeout`, `assistant-consult-failed`. All are
`TaskResult` failures of the tool, never exceptions.

## Package placement

| Package | Gains |
|---|---|
| `NewHeap.Platform.AI.Common` | `NhAiAgentInvocationScope.Consultation` (additive) |
| `NewHeap.Platform.AI.Chat` | `AllowConsultation` builder and options, limits, consult runner and scope, consult effect-policy decorator, `AssistantConsultation` entity and store, `AssistantApproval.ConsultationId`, audit kinds, `agent-request` trigger id, `RequestAnswered` notification |
| `NewHeap.Platform.AI.Chat.SqlServer`, `.PostgreSql` | `AgentConsultations` migration |
| `NewHeap.Platform.AI.Chat.AspNet` | Attested consult catalog and tool handlers, asker filter, consultation endpoint, request trigger, retention |
| `@newheap/platform-ai-chat` | Consultations in the thread timeline; origin on proposals |

## Delivery

- Focused tests in `NewHeap.Platform.AI.Chat.Tests`; real SQL Server and PostgreSQL tests for the
  consultation store, the usage query and the approval column.
- Sample case SPM-278 "The project assistant consults the intake reviewer": a project reader gets
  an answer built from the reviewer's memory, a non-member is refused, a participant's consult
  leaves a deferred proposal that a project approver decides, an agent-granted result stays
  restricted, and a request starts a run with the asker as initiator.
- Guidance: `ai/nh-ai-agent-runs.md` and `ai/nh-ai-assistant.md`; AI.Chat and AI.Chat.AspNet
  READMEs; `docs/release-notes/v-next.md` (new `nhai` migration).
- Verification: `npm run guidance:generate`, `guidance:snapshot`, `guidance:validate`, the sample
  `generate:samples` and `verify:samples`, and both relational providers.

## Risks

- **Paraphrased restricted content.** Filtering removes restricted results from the input, but
  working memory may hold conclusions the agent drew from elevated reads. Readers can see the
  memory today, so a consult does not widen access; agents with elevated reads still need a
  review of what they write to memory.
- **Prompt injection through the question.** The question is untrusted input to the target agent.
  The asker's rights bound what it can read, proposals wait for approvers, and the question never
  enters the target thread's history.
- **Cost.** Every consult is a model turn. Target budget, per-consult limits and the per-asker cap
  bound it.
- **MCP exposure.** An MCP client that may call a consult tool can run model turns. It is off by
  default and must be enabled per registration.
