# v-next

## NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.AspNet, NewHeap.Platform.AI.Chat.SqlServer and NewHeap.Platform.AI.Chat.PostgreSql: a tool selection per consultation

`AllowConsultation(subjectType, consult => consult.Tools("projects.search", ...))` names the tools the
consulted agent is offered in that consultation, by id or `prefix.*` selector; each must be covered by
the agent's own selectors, or startup fails, as for a skill's tools. The consulted model sees only those
tools, a call of another tool runs nothing and returns the new code
`assistant-consult-tool-not-selected`, and a consultation tool of another agent (`assistant.consult.*`,
`assistant.request.*`) is offered only when a selector names it; effects, routes and the depth limit
apply as before. A consultation defined as data stores its selection as `ToolSelectors` of
`NhAssistantDataConsultationRequest` and `NhAssistantDataConsultationDto`, which also reports
`ToolSelectorsInEffect`: saving refuses a selector outside the agent's selectors with
`toolSelectors: not-allowed`, and a selector the agent's selectors no longer cover drops on load with the
new problem `tools-not-allowed`, leaving the consultation without any tool when none is left, never
with every tool of the agent. `NhAssistantConsultationOptions.ToolSelectors` and
`NhAssistantCodeConsultationDto.ToolSelectors` show the selection; a change of it is the consultation's
next version. Without a selection a consultation offers every tool of the agent, as before, and the
consult tool's contract does not change.

| Breaking change | Required action |
|---|---|
| The `ConsultationToolSelectors` migrations add the nullable column `AssistantAgentConsultationDefinition.ToolSelectorsJson` on SQL Server and PostgreSQL. | Apply the assistant migrations before the new version starts (`RunMigrations`, or your deployment's migration step). |
| `NhAssistantErrorCodes` has the new code `assistant-consult-tool-not-selected` (HTTP 400), and `NhAssistantRunConfigurationProblems` the new problem `tools-not-allowed`. | Let code that maps assistant codes or consultation problems handle values it does not know. |

## @newheap/platform-ai-chat: a tool selection per consultation

The consultation form of the run configuration offers the agent's own tool selectors as check boxes
under Advanced, none checked offering every tool of the agent; a saved tool the agent's selectors no
longer cover stays checked with the reason until it is cleared, and `toolSelectors: not-allowed` shows at
the tools. Each row of the Consultations section names the consultation's tools. `DataConsultation`
(`toolSelectors`, `toolSelectorsInEffect`), `DataConsultationRequest` and `CodeConsultation` carry
`toolSelectors`; the problem `tools-not-allowed`, its field error and the code
`assistant-consult-tool-not-selected` have English and Dutch texts. The mock API applies the same checks
and takes `consultationTools` for consultations in code.

| Breaking change | Required action |
|---|---|
| The text `nh-assistant.admin.run-config.consultations.form.advanced` names the tools as well. | Update your application's override of this translation key. |
