# Platform Feedback on 33.0.0 Plan

An administrator of a consuming application asked the agent builder for an agent that counts the orders whose
status changed last week, lists them and warns when there are more than ten. The builder could not finish it.

- **No alert kind to raise.** An agent created in the administration cannot list alert kinds: only an agent in
  code does, with `Alerts(...)`. Without that list every alert is refused (`KindNotListed`), also from a rule
  script.
- **No way to set what the agent needs to read.** The agent needed:
  - a technical account;
  - accountability "technical account";
  - gateway reads.

  The builder keeps whose rights an agent uses outside its reach (RB-8, BA6), so the administrator had to find
  and set these in the screens.
- **It gave up too soon.**
  - The builder's catalog lists the gateway's four tools, but not the gateway's resources. The builder did not
    know that the order status history can be filtered by date and counted.
  - It did not say which setting was missing.
  - The application cannot give it guidance of its own; only the shared application context reaches it.

The application's own part is fixed in the application:
- an issuer for delegated read tokens;
- routing before authentication, so a token can be bound to its gateway action.

NewHeap already supports a host without endpoint routing (`AuthenticateForTargetAsync`), but nothing told the
application why every token was refused (`api-bridge-unauthenticated`).

The administrator's wish: "the builder must be able to do everything I can do myself".

It is a plan, not usage documentation. Once it is built, the guidance rules, the release notes and the sample
cases are authoritative.

## Status

Decided by Lars on 2026-11-04, all as advised. Not built yet: train 37 waits for "Bouw train 37".

## Decisions

| # | Decision |
|---|---|
| FB93 | **Alert kinds for agents of the administration.** An agent created in the administration lists the kinds of alert it may raise in its run settings, as data, out of every kind registered in code (`AddAlertKind`). The kind in code stays the ceiling: its texts, recipients and delivery; the administrator's kind settings apply as they do now. The administration's run settings form and the builder's `save-run-settings` take the list; the agent's rule scripts raise those kinds, and its model tool does so where the kind has a model template and the agent's switch allows it. NewHeap adds no platform kind of its own. |
| FB94 | **The builder can do what the administrator can, except secrets.** These include accountability, the technical account, delegation, gateway reads and elevated reads, the required policy, MCP servers and their organization grants, metadata, consultations, hand-offs, shadow variants, and removing and discarding. Each change is its own approval in the chat, through the same administration services and checks as the screens. A change of whose rights an agent uses, and every removal, gets a card that says so plainly; the application's four eyes apply where they already do. MCP credentials and keys are entered only in the screens, and nothing goes beyond what code allows. The builder searches technical accounts and people as the screens do. This replaces the limits of RB-8 and BA6. |
| FB95 | **The builder knows the application and does not give up.** (1) A read tool searches and describes the gateway's resources (`search-resources`, `describe-resource`) with the administrator's own rights, so the builder knows filters, sort fields and counting. (2) When something blocks the agent, such as gateway reads that are off, no technical account or no listed alert kind, the builder names the setting and sets it after an approval. (3) `AddAgentBuilder(options => options.UseGuidance(asset))` takes a text asset in code that only the builder reads, after the platform's rules and before the application context; it cannot change the builder's rules. |
| FB96 | **Gateway reads say why they fail.** A content-free warning, once per cause, names the cause: a delegated read token on a request whose target is not known yet (authentication before routing; the fix is `UseRouting` before `UseAuthentication`, or `AuthenticateForTargetAsync`), no issuer, a token the issuer refused, or a token the host's scheme rejected. The run's failed read names the same cause as a code, which the administrator and the builder can explain. |
| Train | **Train 37 with three lanes.** AL (FB93: run settings, migration, form, mock, sample) and GD (FB96) in parallel, then BW (FB94, FB95 and the builder's part of FB93) after AL. |

## Rules for all of it

- Compatible and opt-in where behavior changes; a breaking change gets a `Breaking change | Required action` row.
- Tests for each change, on both database providers where persistence changes; guidance where usage changes;
  release notes, the public API snapshot, English and Dutch texts, the mock API and specs.
- The sample shows it: the builder builds an application-wide weekly count through the gateway that raises an
  alert above a threshold, proven with the scripted chat client.
- Nothing names the consuming application.

## Lane choices

| # | Choice | Status |
|---|---|---|
| GD1 | FB96: new codes, not a reason next to `api-bridge-unauthenticated`. The failure contract is one stable code per outcome (`{success, code, message}`, no details), the run stores only that code (`ResultCode`) and the screens translate only the code; a reason would need a new envelope field and column (a migration) and still not reach the administrator, because a delegated read's result is shielded per viewer. `NhAiDelegatedReadFailureCodes`: `ai-delegated-read-target-unknown`, `-no-issuer`, `-issuer-refused`, `-rejected` (and `All`), with a `Breaking change \| Required action` row. | for Lars to review |
| GD2 | FB96: the warning is once per host and cause (a singleton of the application's services, so once per process in production), naming the first agent where one is known; the warnings of each occurrence (events 1 to 5) stay as they are. The causes are host configuration that every agent shares, and the request that detects authentication before routing knows no agent. New events 6 to 9 in the category of `NhAiDelegatedReadService`. | for Lars to review |
| GD3 | FB96: the run learns about authentication before routing in-process. When the restriction refuses a bridge call (its own invocation header) for want of a target, it watches the response; on `401` the host records the cause for that invocation id (bounded, taken once) and warns once, and the bridge's executor takes it on the call's `401`. No response header, so nothing changes on the wire and no token holder is told why. A call that another instance answered fails as `ai-delegated-read-rejected`, and that instance logs the unknown target. | for Lars to review |
| GD4 | FB96: a person who may not lend rights now (no fresh context under an access source, a changed responsible person, no person to read for) keeps `api-bridge-unauthenticated`: it is not a configuration fault and SPM-315 stays as it is. The missing-issuer check now runs before those person checks, so a host without an issuer always says so. | for Lars to review |
| GD5 | FB96: an issuer that fails (event 1) and one that refuses or misbinds its token (event 2) share `ai-delegated-read-issuer-refused`, as the decision names them as one cause; the events of each occurrence say which. A locked-out person thereby gets `ai-delegated-read-issuer-refused` instead of `api-bridge-unauthenticated`. | for Lars to review |
| GD6 | FB96: `AuthenticateForTargetAsync` stays internal. The warning and guidance name it as what the bridge's MVC filter does under `UseMvc`, and say that no middleware may authenticate the scheme first (the default authentication scheme does); no new public API to authenticate for a target. | for Lars to review |
| GD7 | FB96: the sample extends SPM-290 with a real run of the locked-out owner whose read fails with `ai-delegated-read-issuer-refused` in the model's result and the thread's call, next to the reads that work. The sample API routes before it authenticates, so authentication before routing, a missing issuer and a rejected token are proven in the library's tests (a host with `UseAuthentication` before `UseRouting`, and the `UseMvc` platform pipeline with the bearer scheme as default authentication scheme) instead of in a second, slow sample host. | for Lars to review |
| GD8 | FB96: the texts live under `nh-assistant.errors.<code>` (not `result-codes`): the tool call card falls back to them, and the run, rule script and composition views read only `errors`; the codes join `NH_ASSISTANT_SERVER_ERROR_CODES`. No mock change: these are tool result codes, never HTTP codes of the assistant API, and scripted mock turns already take any result code. The builder's instructions are left to lane BW (FB94/FB95 rewrite them); the builder reads the code and its message, which says what to fix. | for Lars to review |
