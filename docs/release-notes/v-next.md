# v-next

## @newheap/platform-ai-chat: consultations defined as data in the administration

The run configuration of an agent lists its consultations in a Consultations section: those in code
read only, and those defined as data, which administrators add, change and remove within the agent's
ceiling in code (`NhAssistantAdminApiService.saveDataConsultation` and `removeDataConsultation`), with
who may ask, the routes of its proposals, its limits, requests, MCP and the classification of its
answers. Each row names the agents that ask it, with `openAgent` to open their editors, and its problems;
the route switches show in the section for every agent with consultations, and the flow's when step
names them. `AgentRunConfiguration` carries `codeConsultations`, `dataConsultations` and
`consultationCeiling`, `RunCatalog` the application's `consultationCeiling`; the codes
`assistant-consultation-in-code`, `assistant-consultation-not-found` and
`assistant-consultations-not-allowed` and the field error `not-allowed` have English and Dutch texts. The
mock API plays consultations as data with `consultationsAsData`, `consultableAsData` and
`dataConsultations`.

| Breaking change | Required action |
|---|---|
| `RunConfigurationProblemKind` also allows `'consultation'`. | Add the value to an exhaustive `switch` over the kinds. |
| The route switches of an agent's consultations show inside the Consultations section for every agent with consultations, also one created in the administration, and the texts `code`, `not-allowed`, `not-allowed-hint`, `action.code`, `explain-change.code-on` and `explain-change.code-off` under `nh-assistant.admin.agents.consultation-routes` speak of the agent's consultations instead of its code; the error texts `assistant-consult-route-not-allowed`, `assistant-configuration-limit` and `assistant-run-settings-in-use` under `nh-assistant.errors` name consultations as well, and the first no longer speaks of consultations in code only. | Update your application's overrides of these translation keys. |

## @newheap/platform-ai-chat: the autonomy hint of the agent editor

| Breaking change | Required action |
|---|---|
| The text `nh-assistant.admin.agent-editor.autonomy-hint` says that `Propose` and `Simulate` propose changes in a consultation that may propose. | Update your application's override of this translation key. |

## @newheap/platform-ai-chat: the board's division and what releasing the kill switch does

`AdminAgent.offBeforeStop` and `AgentState.offBeforeStop` carry the server's field, and the agents
administration says for a stopped agent whether releasing the kill switch switches it back off or makes
it work again, in English and Dutch (`nh-assistant.admin.agents.state.stopped-was-off-hint` and
`stopped-was-on-hint`). The mock admin API serves `offBeforeStop` as the server does.

| Breaking change | Required action |
|---|---|
| The agent proposals that `nh-assistant-agent-board` shows on a board with `divisionId` are filtered on that division. | None; a board without `divisionId` shows the proposals of every division as before. |

## @newheap/platform-ai-chat: proposals of consultations

The approval card shows a consultation's proposal that the person decides in their own chat with the
consulted agent, the subject and its answer, and offers Leave for approvers when a route at the subject
applies; proposal views show who asked and the route; the agent thread view reads each pending
proposal's live decision right (`lookupProposals`), as the run result and the subject's work do, so the
person who asked sees the four-eyes reason instead of Approve and Reject, also as an approver; the run
configuration of agents in code switches the consultation routes; the mock API serves both routes.
Texts are in English and Dutch.

| Breaking change | Required action |
|---|---|
| `ApprovalDecision` also allows `'leave'` and `NhAssistantApprovalWaitingFor` also allows `'asker'`. | Add the new values to an exhaustive `switch` over these types. |

## @newheap/platform-ai-chat: application messages, upload types and the grouped inbox's description

`nh-assistant-thread` shows a message the application posted (`role: 'application'`) as the
application's, in chats and agent threads: a card with the sender, an "Application" tag, the title,
the Markdown text and a link that opens a route through the router or an `https` address and nothing
else (`Message.application`, `ApplicationMessageDetails`, `ApplicationMessageLink`), and the label
"Instruction for the assistant" for a message the assistant follows (`ApplicationMessageDetails.trusted`;
`trusted` of `NhAssistantMockBackend.postApplicationMessage`). Its new optional input
`writingMessageId`, which the panel fills from the new `NhAssistantStore.writingMessageId` (the answer
of the turn the tab streams or follows live), keeps the working indicator under the answer a turn still
writes, also when the application's or a colleague's message arrives after it; a custom layout that does
not pass it shows the indicator at the end of the thread, as before. The composer gives
a file without a usable type the accepted type of its extension by the server's rule, and its file
picker offers the extensions of every accepted type it knows, so `.csv` files are listed for
`text/csv`. With `grouped`, `nh-assistant-agent-inbox` describes the grouped order
(`nh-assistant.agent-runs.inbox.description-grouped`). The mock API posts application messages with
`NhAssistantMockBackend.postApplicationMessage` and derives upload types as the server does. Texts
are in English and Dutch.

| Breaking change | Required action |
|---|---|
| `Message.role` also allows `'application'` and `MessageAuthorKind` also allows `'application'`. | Add the new values to an exhaustive `switch` over these types. |
| A tab that streams a turn now shows the live `message.created` of the application's and other people's messages instead of ignoring them until the conversation loads again, and `applyNhAssistantLiveEvent` puts a message where its `sequence` puts it, after the answer a turn is still writing, instead of last. | Nothing for `NhAssistantStore` and the panel; a custom view that applies live events itself does so with `applyNhAssistantLiveEvent` while it streams, too, and leaves out the turn's own events and message. |
| `AgentUploads.mappedExtensions` (`AgentUploadExtension`) carries the extensions the application mapped on the server; the composer derives a file's type with them before the platform's table, also `text/xml` for `.xml` as `application/xml`, and its file picker offers them. The mock API derives alike and refuses text whose content starts as a binary format, as the server does. | None; give an agent of a mock scenario `mappedExtensions` when the server maps extensions. |
