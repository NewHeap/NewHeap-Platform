# Agent Account Capabilities Plan

This document records a fix of agent tool authorization in `NewHeap.Platform.AI.AspNet.Common`,
`NewHeap.Platform.AI.Common` and `NewHeap.Platform.AI.Chat`, reported by a consuming application on
NuGet 19.0.0: a technical account that is an ordinary user, with the rights of its work, must be able to
run an agent's tools reliably. It is a plan, not usage documentation.

## Status

Decided by Lars on 2026-10-23: TA1 to TA3, every one as recommended. Not built yet.

## Problem

1. **The source of rights ignores capabilities.** `NhAiAgentInvocationGate.DecideAsync` chooses
   `NhAiGrantSource.Delegated` when the initiator satisfies every `AuthorizationPolicies` of the tool, and
   `NhAiGrantSource.Accountable` when the agent acts with its account's rights and the account satisfies
   them. The capabilities then come from `GrantCapabilitiesAsync`, without checking that they cover the
   tool's `RequiredCapabilities`, so `NhAiToolInvoker` refuses the call on a capability and the agent's
   grant is never tried. An agent in code whose account has the tool's policy but not the policy behind
   one of its capabilities fails, while the same agent with an account without rights succeeds through
   its grant; an agent created in the administration has no grant, so its tools work only when the
   account holds every capability policy.
2. **The conversation names a missing capability `ai-tool-authorization-denied`.** The invoker returns
   `ai-tool-capability-denied`, `ai-tool-effect-denied` or `ai-tool-approval-delegation-invalid`, and the
   gate its own code, but audits each as `NhAiOutcomeKind.AuthorizationDenied` without a result code.
   `NhAssistantToolOutcome.FromAudit` turns that into `ai-tool-authorization-denied` (and an approval
   delegation refusal into `ai-tool-execution-evidence-invalid`), which the conversation's tool call, the
   assistant's audit and the model receive.
3. **A capability used as a gate for people.** The application asked whether NewHeap should let a
   capability grant count only for people.

## Decisions

| # | Decision |
|---|---|
| TA1 | **A source of rights counts only when it covers the tool's capabilities, for the initiator and the account alike.** `Delegated` and `Accountable` are chosen only when the principal satisfies the tool's policies and its capabilities cover `RequiredCapabilities`; otherwise the decision continues with the next source, as it already does when a policy is missing (initiator, then account, then the agent's grant). When no source fits and one failed only on a capability, the refusal carries `ai-tool-capability-denied`. This lends no new rights: a source that misses a policy already falls through today. |
| TA2 | **The real code everywhere.** Every refusal of the invoker records its own code as the audit's result code (the gate's code, `ai-tool-capability-denied`, `ai-tool-effect-denied`, `ai-tool-approval-delegation-invalid`), and `NhAssistantToolOutcome.FromAudit` uses it before the outcome kind, so the conversation's tool call, the assistant's audit and the model see it. The release notes list it for applications that filter on `ai-tool-authorization-denied`. |
| TA3 | **Guidance, no new feature.** A capability on a tool names the right the tool itself needs (such as reading logs); who may use the assistant is the `AccessPolicy` and the MCP policy, not a capability on a tool; tools that only make sense for a person are not exposed to agents. The guidance rule on AI tools and agent rights says so. |

## Evidence to build

- Library tests for TA1 on both decisions (initiator, account): an account with the tool's policy but
  without a capability policy falls through to the agent's grant in code, and is refused with
  `ai-tool-capability-denied` for an agent without a grant; an account with both runs as `Accountable`.
- Library tests for TA2: the tool call and the audit carry each code.
- A sample case of agent accounts (the operations account of SPM-296/SPM-298) extended with an account
  that has a tool's policy but not its capability.
