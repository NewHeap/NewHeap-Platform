# NewHeap.Platform.AI.Chat.AGUI

An opt-in AG-UI (Agent-User Interaction Protocol) endpoint for the NewHeap assistant. AG-UI clients such
as CopilotKit, Blazor AI components or the .NET `AGUIChatClient` run a chat agent over the assistant's own
turn runner, tool authorization, approvals, budget and audit.

```csharp
services.AddNewHeapAssistant(assistant => assistant /* storage, agents, ... */);

app.MapNewHeapAssistant("/api/assistant");
app.MapNewHeapAssistantAGUI("/api/assistant/agui/project-assistant", "project-assistant");
```

- The endpoint requires the assistant access policy and the person's authentication; the request never
  names a person or an agent.
- `threadId` names the person's conversation with the agent: a GUID is the conversation id, any other id a
  conversation of the person's own. Only the newest message is read; the history is the assistant's.
- `tools` only selects client tools the application declared with `AddClientTool`, by id; `state`,
  `context` and `forwardedProps` are ignored.
- Proposals and questions end a run with an interrupt; a `resume` entry decides or answers it with the
  semantics of the assistant's decide and answer endpoints, bound to the proposal hash.

Packages that do not use AG-UI do not need this package or its `AGUI.*` dependencies. See the
`nh-ai-assistant` guidance rule and sample case SPM-245.
