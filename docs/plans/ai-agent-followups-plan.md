# AI Agent Follow-ups Plan

This document records four follow-ups and one fix to the agents of `NewHeap.Platform.AI.Chat` and
`@newheap/platform-ai-chat` that Lars decided after the
[approval inbox overview](ai-inbox-overview-plan.md) was released (nuget-common 18.0.0, npm-ai-chat
0.17.0). It was written on 2026-10-22 on `feature/agent-followups`.

## Status

Built on 2026-10-22 in one lane of a merge train. Lars decided the five parts below (F1 to F5); the
open choices taken while building them are recorded as AF1 to AF11 for Lars to review.

| Part | Status |
| --- | --- |
| F1 | Built: the request's person is recognized by the platform's actor id in the three places. |
| F2 | Built: `NhAssistantRuleProposal.NotNeededKind` and `Explanations`. |
| F3 | Built: `OffBeforeStop` on the agent state, the admin DTO, the Angular models and the administration. |
| F4 | Built: the hand-over briefing names what became of the rules' proposals and the decisions they saw. |
| F5 | Built: the board gives the per-agent inbox its division. |

## Decided by Lars

- **F1.** Recognize the person of a request by the platform's actor id, not by the `NameIdentifier`
  claim, in the agent gate (`ResolveInitiatorAsync`), the consultation tools (`RequestPrincipalOf`) and
  the ASP.NET context contributor, with the same source that produced `InitiatorActorId` and the asker.
  NewHeap Identity hosts keep working exactly as now.
- **F2.** `NhAssistantRuleProposal` carries, for `NotNeeded`, the kind of the guard's outcome and the
  explanation per supported UI culture, reusing the platform's stale texts, next to `Explanation`.
- **F3.** `OffBeforeStop` on `NhAssistantAgentState` (true only while stopped and off before the stop),
  in the admin DTOs and Angular models, and the administration says for a stopped agent what releasing
  does, in every shipped language.
- **F4.** After a rule `HandOver`, the model's briefing names per proposal of the run its tool, object
  and outcome, and the recent rejections the rules saw with tool, date and kind; compact, bounded and
  within the privacy rules for what models may read.
- **F5.** `nh-assistant-agent-board` binds `[divisionId]` on the per-agent `nh-assistant-agent-inbox`.

## Decisions for Lars to review

- **AF1. The source of the request's actor id is the authenticated invocation context resolver.** The
  assistant names people by `INhAiAuthenticatedInvocationContextResolver.ResolveAsync(httpContext).ActorId`:
  every endpoint resolves its caller with it, and a conversation's owner, a run's initiator and a
  consultation's asker are that id. The AI libraries never read `INhActorContextAccessor`, so the three
  places now compare with the resolver's id (`NhAiRequestActor`, internal to
  `NewHeap.Platform.AI.AspNet.Common`). An application with its own identity model registers a resolver
  that returns its own actor ids, for example from its `INhActorContextAccessor`; the tests do exactly
  that. Reading `INhActorContextAccessor` in the AI libraries instead was rejected: it would be a second
  source that can disagree with the ids the assistant recorded.
- **AF2. Once per request, and re-entrancy.** The id is resolved at most once per request and kept in
  `HttpContext.Items`. The platform's own resolver remembers the id it resolved, so a NewHeap Identity
  request that already resolved its caller never resolves twice; an application's own resolver runs at
  most once more per request. While a resolver builds the request's own context, that context counts
  as the person's own only for the id the platform's resolver builds it for; while an application's
  resolver is asked by the check itself, the context it builds matches nobody, and only its actor id is
  used. The resolver comes from `HttpContext.RequestServices`, with the caller's scope as fallback for
  a hand-made context.
- **AF3. Issuer projections change too.** With `UseAuthenticatedClaims...` the actor id is `oidc:` plus
  a hash and never matched the claim, so the contributor skipped the request's own context. It now adds
  the correlation id, the active division and its capabilities there as well, which is the same fix.
  Recorded as a breaking change with a review action in the release notes. Keeping projection hosts as
  before would have kept the bug for them.
- **AF4. `InternalsVisibleTo` instead of a new public API.** `NewHeap.Platform.AI.AspNet.Common` now
  gives `NewHeap.Platform.AI.Chat.AspNet` access to its internals for the check, as the AI packages that
  are released together already do among each other. A public service (for example
  `INhAiRequestActor.IsRequestActorAsync`) would let applications ask the same question; not added.
- **AF5. The shape of the not-needed details.** `NotNeededKind` is `NhAiStaleKind?`, the enum the guard
  returns, so rules compare with the enum; `NhAssistantRuleDecision.Kind` stays the string of
  `NhAssistantStaleKinds`. `Explanations` is a non-null, case-insensitive `IReadOnlyDictionary<string,
  string>` from culture name to text, the run's culture first, empty for other outcomes and when the
  guard gives no explanation. It holds exactly the texts a stale proposal stores
  (`NhAssistantStaleExplanations.RenderTexts`, bounded by `MaxStaleExplanationsSerializedLength`), and
  `Explanation` is picked from it for the run's culture as before.
- **AF6. What the hand-over briefing says.** Per proposal of the run, in order: `- {tool} on this subject`
  or `at {type} {id}` for a placed one, then the outcome: recorded and waiting; recorded and superseding
  N pending proposals; recorded there for that object's approvers; already open; recently rejected with
  its date; not needed with its kind, the guard's reason code and its explanation (application text the
  model also reads in its own tool result, cut at 200 characters); not placed with its reason; refused
  with its code. A refused or not needed proposal names no object, because the rules' result does not
  say which it was. Then both kinds of decisions the rules were given (`RecentRejections` and
  `RecentInvalidations`), newest first: date, tool and "rejected by people" or "closed without a decision
  as {kind}". No proposal summaries and no feedback texts: the agent continuity block already gives the
  model what it may read of them and the section points there. At most 20 proposal lines and 10 decision
  lines, at most 6,000 characters and never more than the briefing's bound minus the title, with
  "… and N more" for the rest. The text is English, as the rest of the briefing.
- **AF7. Both decision kinds count as "rejections the rules saw".** The rule handler gets rejections and
  invalidations as `NhAssistantRuleDecision`; only invalidations have a kind, so listing both was the way
  to give "tool, when and kind".
- **AF8. `OffBeforeStop` and the hint.** `OffBeforeStop` is `false`, not missing, in every other state,
  also `configured-off`. The administration shows the hint on every stopped agent, also when it was not
  off ("releasing the kill switch makes it work again, as Resume does"), and none when an older server
  sends no field. "Resume" on a stopped agent keeps its explanation: it switches the agent on either way.
  The Dutch texts call the kill switch "noodstop"; the library had no Dutch term for it yet.
- **AF9. The board's division.** The board passes `divisionId()`, which is `null` for the board of the
  whole application; the inbox treats `null` as no filter, so nothing changes there.
- **AF10. Sample evidence.** F1: a new SPM-223 sample test shows an issuer-projected person's own request
  lending its rights to a request turn through the real gate, and another person's turn getting nothing.
  F3: the SPM-284 sample tests assert `OffBeforeStop` for the stopped closer (false) and for the planner's
  variant stopped while it is off (true), whose release puts it back off. F2 and F4 have no sample path:
  the sample's rule handlers check the project's state before they propose, so none meets a guard that
  checks at proposal time, and none proposes before handing over. Making one would mean giving
  `ProjectStatusProposalGuard` the proposal stage for every agent and letting the kickoff rules rely on
  it for an already active project. SPM-282 and SPM-285 name the new members with the library tests as
  evidence, as SPM-282 already did for `Explanation`. F5 has no sample change: the portal shows the board
  of the whole application only. Lars may want the kickoff variant as executable evidence.
- **AF11. Release notes.** Recorded as breaking: the contexts of projection and own-resolver hosts (AF3),
  the extra resolver call (AF2), the longer hand-over section that shares `MaxBriefingCharacters`, and
  the board's division filter. `OffBeforeStop` and the not-needed details are additions.

## Not done

- A public API for applications to ask whether a request is a person's own (AF4).
- A sample rule handler that meets a not-needed proposal or proposes before handing over (AF10).
