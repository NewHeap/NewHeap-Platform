# v-next

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: chain callers and context bindings the application stores

Links between applications that the application reviews while it runs no longer need a restart
(FB59). Register an `INhAssistantMcpLinks` with `UseMcpLinks<TLinks>()`: its `ListAsync` returns an
`NhAssistantMcpLinkList` with the chain callers whose consultation chains the application continues
and the context bindings of tools on administrator-connected MCP servers, as the application reviewed
and stores them. They count next to `ContinueMcpChain` and `AddMcpContextBinding`, which behave as
before. The assistant reads the source in a scope of its own and reuses its answer for
`NewHeap:AI:Assistant:Mcp:LinkCacheDuration` (`NhAssistantMcpOptions.LinkCacheDuration`, 30 seconds by
default, from zero to 5 minutes, on the application's clock), so a link the application adds or
revokes counts within that time. The application owns who may create a link.

Every stored entry is checked as the same registration in code is checked at startup. An entry that
registration would refuse is left out with a warning that names its reason
(`NhAssistantMcpLinkProblems`), and a stored entry never grants more than a registration could: a
stored binding applies only to a tool of an administrator-connected server under the binding's server
id and URL, never to a code-owned import, and a server id and tool or an issuer and client that code
registers keep their code registration. A source that throws or does not answer within
`NhAssistantMcpLinkList.ReadTimeLimit` (10 seconds) counts as no stored links until the next read,
while the code registrations still count. Adding or revoking a stored binding changes the contract of
that tool, so a proposal of it that waits for a decision no longer runs and the model proposes it
again; a call whose stored binding was revoked after its turn imported the tool stops with
`ai-mcp-binding-failed`. SPM-266 and SPM-366 show both kinds of link in the sample.
