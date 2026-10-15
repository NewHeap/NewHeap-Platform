# Subject Briefings and Run Budgets Plan

This document is the architecture and implementation plan for two cost and quality levers of
agent runs: a briefing that the application supplies about the subject at the start of every
turn, and a token ceiling per run. It is phase A2 of the
[agent roles roadmap](ai-agent-roles-plan.md). It was checked against `main` at `9ab24f20` on
2026-10-15.

## Status

Implemented on 2026-10-15, with sample case SPM-285. The choices are part of the
[decision record](#decision-record) and the [answered questions](#answered-questions). The consumer
guide `ai-agent-runs` and the sample are authoritative from now on; the implementation deviates from
the plan where [Implementation notes](#implementation-notes) says so.

## Implementation notes

- **Where briefings apply.** Headless runs (purpose `run`, with the run's inputs), people's turns
  in a thread (`thread`) and consultations (`consultation`). A turn that continues after an inline
  approval continues the earlier turn's messages and gets no new briefing; rule handlers get none.
- **Format.** Each section is a `<section title="...">` element inside `<subject-briefing>`;
  `<` and `>` in titles and texts become `‹` and `›`, control characters other than line breaks
  become spaces. The bound counts the characters of titles and texts; a section that does not fit
  ends the briefing, so a first section larger than the bound leaves only the note.
- **Providers** are registered as an enumerable scoped service; a later registration for the same
  subject type wins, as for subject access resolvers.
- **The ceiling is an allowance per execution.** The run operation passes what is left of
  `MaxTokensPerRun` to each turn; the interceptor refuses the next tool call once the turn used it,
  which ends the model loop before the next model call, and no closing answer follows. Because the
  allowance belongs to one execution of the run, a resumed run, or a run that continues after an
  inline approval, starts with a new allowance.
- **Waiting messages.** When the allowance is used after a completed turn and messages wait in the
  thread, the run fails with `assistant-run-token-limit` instead of starting another turn; a resume
  handles them.
- **No text in a language.** The library has no server-side translations, so the thread message of
  a run at its ceiling gets no note; the run timeline shows the failure code through
  `nh-assistant.errors.assistant-run-token-limit`.
- **The sample** briefs every agent about projects and sets the ceiling on the intake reviewer, not
  on the project planner, which phase B introduces. The briefing reports the project closer's
  completion check, which its rules use as well.

## Problem

- **The model starts almost blind.** A run receives its instructions, the trigger's message and
  payload, the turn facts of `INhAssistantTurnContextProvider` (at most 20 facts of 300
  characters, about the user and the moment, not about the subject) and the continuity block
  (working memory, run summaries, rejections, overtaken proposals). Everything else about the
  subject it gathers with tools: every tool round is another full model call, with the instructions,
  tools and history again.
- **Rules and the model do not share what the rules saw.** A rule handler that hands over passes
  one reason string; the facts it checked are gone.
- **A run has no ceiling.** The model profile bounds each call (`MaxInputTokens`,
  `MaxOutputTokens`) and the calls per turn (`MaxCalls`), and the agent's daily budget is checked at
  run start and settled afterwards. Nothing stops one run that keeps calling tools within those
  bounds; with hundreds of events a day, a few such runs dominate the cost.

## Goals

- The application gives each agent turn a compact, bounded sketch of the subject, as data, from the
  same domain logic its rules use.
- One run stops when it used its token ceiling, keeps what it did, and says why.

## Non-goals

- Retrieval or embeddings; the application decides what the sketch contains.
- Storing briefings; they are built per turn and not persisted.
- Ceilings per skill; phase B adds them on top of the agent's ceiling.

## Design

### Briefing provider

```csharp
public interface INhAssistantSubjectBriefingProvider
{
    /// <summary>The subject type it briefs, as registered with AddSubjectType.</summary>
    string SubjectType { get; }

    ValueTask<NhAssistantSubjectBriefing?> GetBriefingAsync(
        NhAssistantBriefingContext context,
        CancellationToken cancellationToken);
}

public sealed record NhAssistantBriefingContext(
    string AgentId,
    NhAiSubjectInfo Subject,
    string Purpose,                                   // run, thread or consultation
    IReadOnlyList<NhAssistantRuleInput> Inputs);      // the run's events; empty for people's turns

public sealed record NhAssistantSubjectBriefing(IReadOnlyList<NhAssistantBriefingSection> Sections);

public sealed record NhAssistantBriefingSection(string Title, string Text);
```

- Register one provider per subject type with `assistant.AddSubjectBriefing<TProvider>()`. It runs
  in the turn's service scope, without a person for headless runs, and reads the application's own
  data directly, like a rule handler.
- **Where it applies:** headless model turns, the model part of a rule run that hands over, people's
  turns in the subject's thread and consultations about the subject. Rule handlers read the
  application's data themselves and get no briefing.
- **How the model sees it:** a `<subject-briefing>` block after the instructions and before the
  continuity block, introduced as data supplied by the application, not instructions. Titles are at
  most 80 characters; markup characters are neutralized like the other data blocks.
- **Bounds:** `NhAssistantLimits.MaxBriefingCharacters`, 8,000 by default and at most 32,000.
  Sections are kept in order until the bound is reached; the rest are left out with a note. The
  briefing counts against the history budget of the turn.
- **Failures:** a provider that throws or takes longer than `BriefingTimeout` (5 seconds by
  default) leaves the turn without a briefing; the log names the exception type only.
- **Rules as suppliers:** the provider can call the same domain checks as the agent's rules and
  return their findings as a section, so rules signal and the model decides. A rule handler's
  hand-over reason becomes a "Rules" section of the briefing in that run instead of a separate
  system message.

### Token ceiling per run

- `ConfigureAgent(id, agent => agent.MaxTokensPerRun(60_000))`, input and output tokens together,
  across every model call of one run, including a rule run's model part. No ceiling by default.
- **Enforcement:** the turn runner counts usage per streamed update. When the run reaches its
  ceiling, it ends the model loop before the next model call; a call in progress finishes, so the
  last call can cross the ceiling by its own size.
- **Outcome:** the run ends as `failed` with failure class `budget` and code
  `assistant-run-token-limit`. Proposals it already recorded stay; its message in the thread ends
  with a localized note that the run stopped at its budget. Approvers may resume or restart it, as
  any failed run.
- **Consultations** of the agent use the same ceiling for their one turn.

## Decision record

Proposed on 2026-10-15; decided the same day.

| # | Decision | Rationale |
|---|---|---|
| D1 | One briefing provider per subject type, application code with direct data access. | The application knows what matters; providers reuse the rules' domain checks. |
| D2 | Sections of title and text, bounded to 8,000 characters by default. | Structure without a schema per application; a predictable cost per call. |
| D3 | Applies to headless turns, thread turns and consultations, not to rule handlers. | Every model turn gains; rules read data directly. |
| D4 | A failing or slow provider never fails the turn. | The briefing is an aid, not a precondition. |
| D5 | The hand-over reason of rules joins the briefing. | One place where the model reads what the application found. |
| D6 | `MaxTokensPerRun` per agent, enforced between model calls, ending the run as failed with `assistant-run-token-limit`. | A bounded worst case per event; people see and can resume it. |
| D7 | No ceiling by default. | Existing agents behave as before; the guide recommends one. |

## Delivery phases

1. **Library.** The provider contracts and registration, the briefing block in run, thread and
   consultation turns, the limits, the ceiling with its failure code and the localized note.
2. **Client.** The failure code's text in the run timeline (`nh-assistant.errors.assistant-run-token-limit`).
3. **Evidence and documentation.** SPM-285, the guidance rule and consumer guide `ai-agent-runs`,
   release notes.

## Verification

On SQL Server and PostgreSQL where persistence is involved:

- A headless run, a thread turn and a consultation each send the briefing once, after the
  instructions, as data; a rule run that hands over sends the rules' reason inside it.
- A briefing over the limit is cut at a section boundary with a note; a throwing or slow provider
  leaves the turn without a briefing and the turn succeeds.
- A run with a ceiling stops before the next model call once it reached the ceiling, fails with
  `assistant-run-token-limit`, keeps its proposals and can be resumed; without a ceiling nothing
  changes.

Sample case SPM-285: the project planner receives a briefing of the project (open and overdue
tasks, the last status change and what its rules found), proposes from it without reading tasks
through tools, and a second run that keeps searching stops at its ceiling.

## Risks

- **Stale or wrong briefings** mislead the model as much as they help it. The guide asks providers
  to state facts with their dates and to leave judgement to the model.
- **Sensitive data** in a briefing reaches the model provider. Providers follow the agent's data
  classification like tools do; the guide says so.

## Answered questions

Answered on 2026-10-15:

- **Briefing size?** 8,000 characters by default, at most 32,000.
- **A default run ceiling?** No; the guide and the sample set one, and applications choose a value
  that fits their models.
- **How does a run end at its ceiling?** As failed with failure class `budget` and code
  `assistant-run-token-limit`, resumable by an approver.
