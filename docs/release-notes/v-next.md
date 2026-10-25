# v-next

## @newheap/platform-ai-chat: agent names in the administration and the region refusal

The agent editor's title, the import dialog's name of a package agent the host does not have yet, and the
names in the agent list, the export dialog, the run configuration and the flow all read an agent's name
through `agentDisplayName`: an agent in code shows its translation in the reader's language instead of
its translation key. New texts translate the server's `assistant-model-region-denied` error and the field
error `region-not-permitted` (English and Dutch).

| Breaking change | Required action |
|---|---|
| The new translation keys `nh-assistant.errors.assistant-model-region-denied` and `nh-assistant.admin.run-config.field-errors.region-not-permitted`. | An application that replaces the bundled translations adds both keys in each of its languages. |

## @newheap/platform-ai-chat: the question card and the skill of a message

A question an agent asked with `ask_question` (server option `AllowQuestions`) renders in the thread as
`NhAssistantQuestionCardComponent` (`nh-assistant-question-card`): one button per choice and a countdown
for the person it is addressed to, read-only with whom it waits for for everyone else, and the chosen
answer once answered. The models gain `QuestionPart` with `QuestionStatus` in `MessagePart`,
`Conversation.pendingQuestion`, `AnswerQuestionRequest`, `Message.skillId` and the tool call status
`awaiting-answer`; the stream and live events `question.asked` and `question.answered`
(`QuestionAskedEvent`, `QuestionAnsweredEvent`) follow in `applyNhAssistantEvent`, and
`applyNhAssistantQuestionAnswer` records an answer locally. `NhAssistantApiService.answerQuestion` posts an
answer and streams the resumed turn; `NhAssistantStore.answerQuestion` and `canAnswer` serve the addressee;
the thread emits `questionAnswer` (`NhAssistantQuestionAnswer`) and the panel answers. The composer's
`status` takes `waiting-for-answer`, and the tool call group the state `awaiting-answer`. The texts are in
English and Dutch, `NH_ASSISTANT_SERVER_ERROR_CODES` names the `assistant-question-*` codes, and the mock
API plays a `question` step (`NhAssistantMockQuestionStep`) and answers as the server does.

| Breaking change | Required action |
|---|---|
| `MessagePart`, `ToolCallStatus`, `NhAssistantSseEventMap`, the composer's `status` and `NhAssistantToolCallGroupState` gain question values. | Code that switches over them exhaustively handles `question`, `awaiting-answer`, `question.asked`, `question.answered` and `waiting-for-answer`. |
| New translation keys under `nh-assistant.question`, `nh-assistant.tool-call.status.awaiting-answer`, `nh-assistant.tool-group.awaiting-answer`, `nh-assistant.composer.status.waiting-for-answer` and `waiting-for-answer-by`, and `nh-assistant.errors.assistant-question-*`. | An application that replaces the bundled translations adds the keys in each of its languages. |

## @newheap/platform-ai-chat: the inbox status filter and the consultation limit

`AgentInboxFilter.status` (`AgentInboxStatus`: `pending`, `awaiting-application`, `execution-unknown`)
passes `status=` to `GET approvals/inbox`, and `AgentInbox.status` names the status the page lists; the
mock API serves both. `NH_ASSISTANT_SERVER_ERROR_CODES` gains `assistant-consult-turn-limit`, translated in
English and Dutch. In a chat, the approval card of an uncertain hand-off the user approved
(`ApprovalPart.canResolve`, with `executionResultCode` and `executionReportDueAt`) offers to hand it to the
application again or to mark it done: the card's `resolve` output (`ApprovalResolution`), the thread's
`approvalResolution` (`NhAssistantApprovalResolutionChoice`), `NhAssistantStore.resolveApproval` and
`NhAssistantApiService.resolveApproval` (`ResolveApprovalRequest`); the mock API resolves it as the server
does.

| Breaking change | Required action |
|---|---|
| The new translation keys `nh-assistant.errors.assistant-consult-turn-limit` and `nh-assistant.approval.resolve.explanation`, `hand-to-application`, `mark-resolved` and `resolving`. | An application that replaces the bundled translations adds the keys in each of its languages. |

## @newheap/platform-ai-chat: promoting a shadow variant in the administration

The shadow variants of an agent in the administration offer "Promote onto {agent}" for a data variant,
with a confirmation that says what the live agent keeps and an option to move the variant's skills. It
calls the new `NhAssistantAdminApiService.promoteShadowVariant(variantId, request)` with
`ShadowVariantPromotionRequest` and `ShadowVariantPromotion`, at the live agent's version and the variant's
(`ShadowVariant.version`). The mock API promotes as the server does. `AgentMetadataEntry` gains the
optional `indexedByEach` of `IndexByEach`, and `AgentMetadata` also its `indexValues`.

| Breaking change | Required action |
|---|---|
| New translation keys `nh-assistant.errors.assistant-not-shadow-variant` and `nh-assistant.admin.shadow-variants.promote`, `promote-named`, `promote-confirm.*` and `promoted`. | An application that replaces the bundled translations adds the keys in each of its languages. |

## @newheap/platform-common: modals for the keyboard and screen readers

`nh-modal` owns its accessibility. The holder is a dialog with `aria-modal="true"`, named by its title
through `aria-labelledby`, and the close button is a `type="button"` with the translated label
`nh-common.modal.close` (English and Dutch in the new `NH_COMMON_TRANSLATIONS`; an application's own
translation of the key wins). Opening moves focus to the content's `autofocus` element, else its first
control, else the close button or the dialog; content that renders its fields after loading gets its
first field focused once it appears, and `focusInitial()` does so again on request. Tab and Shift+Tab
stay inside the topmost modal and skip disabled and hidden controls; closing, also by a route change,
gives focus back to the element that opened the modal. Escape closes only the topmost modal that can
close: content with the new optional `INhModalComponent.onModalEscape` decides, and mutating and
confirmation content call `closeDialog` there. The `nh-modal-open` body class stays until the last open
modal closes.

| Breaking change | Required action |
|---|---|
| `NhModalMutateBaseTypeComponent` and `NhModalConfirmComponent` no longer listen to `document:keyup`; the shell handles Escape on keydown for the topmost modal and calls `onModalEscape`, so a stack closes from the top and `onKeyUp` (deprecated) is no longer called. | Override `onModalEscape` or `closeDialog` instead of `onKeyUp`, and remove own Escape listeners and focus traps from modal content. |
| A modal opened with `closeable: false` no longer closes on Escape, also with mutating or confirmation content. | Offer an explicit action in such a modal when people must be able to leave it. |
| The modal takes focus when it opens and gives it back when it closes. | Remove code that focused a field after opening a modal unless it chooses another field; mark the field with `autofocus` instead. |

## @newheap/platform-ai-chat: documents with relative links and diagrams

The new standalone `nh-assistant-document` (`NhAssistantDocumentComponent`) renders untrusted Markdown,
such as the files of a skill or knowledge bundle, with the assistant's safety defaults: inline HTML as
text, images as their alt text, and only links to its own headings, `https:`, `http:` and `mailto:`
addresses or other documents of the bundle, which `siblingPaths` lists and relative links resolve
against from `path`. `linkClick` (`NhAssistantDocumentLinkEvent`) reports a link without navigating to a
sibling, `headingsChange` (`NhAssistantDocumentHeading`) the headings, and `scrollToHeading` scrolls to
one. An optional `NhAssistantDiagramRenderer` the application supplies, and loads lazily inside
`render`, draws fenced blocks of its languages; the document sanitizes the SVG, shows it in a shadow
root, and shows a block it cannot draw as source with the reason. No package dependency was added.

| Breaking change | Required action |
|---|---|
| New translation keys `nh-assistant.document.diagram-failed`, `diagram-no-reason` and `diagram-not-svg`. | An application that replaces the bundled translations adds the keys in each of its languages. |

## newheap-platform: a public website and Angular pins from the peer range

The consumer bootstrap and inspector accept the profile `public-website`: an API with a public Angular
front end, without sign-in (the bootstrap refuses `--authentication` with it) and with persistence only
when chosen. Its manifest records `frontend: "public"`, and the inspector's validate mode drops the
portal's identity store, API base and modal requirements for it. The Angular framework pins of both
front-end profiles are now the lowest version that every `@angular/*` peer range of
`@newheap/platform-common` accepts (today 20.3.28, which the old pin 20.3.27 did not satisfy), read from
the new `npmPeerDependencies` of `distribution.json` and the install lock.

| Breaking change | Required action |
|---|---|
| The bootstrap writes Angular 20.3.28 instead of 20.3.27 into `src/Front-end/package.json` and refuses to overwrite a different file. | Before rerunning the bootstrap in a consumer bootstrapped earlier, raise its Angular packages to the generated pins; `@newheap/platform-common` requires `^20.3.28` anyway. |
| A front-end bootstrap needs `npmPeerDependencies` in the compatibility metadata. | Reinstall the skill from this plugin version before bootstrapping a front end. |
