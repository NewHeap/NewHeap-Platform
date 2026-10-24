# v-next

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

## @newheap/platform-ai-chat: open the run of a notification

`NhAssistantPushService.openRun$` emits the run of a clicked notification about the proposals of a run,
so the application opens its inbox of that run, for example `nh-assistant-agent-inbox` with `[runId]`.
The bundled worker forwards the run with the click, and a click that opens a new window does so through
`#nh-assistant-run=<runId>.<conversationId>` (`NH_ASSISTANT_RUN_FRAGMENT`). Without a subscriber a click
opens the conversation in the panel as before.

| Breaking change | Required action |
|---|---|
| While the application subscribes to `NhAssistantPushService.openRun$`, a clicked notification about the proposals of a run emits its run there instead of opening the conversation in the panel. | Subscribe in the application shell and open the inbox of the run; copy `push/nh-assistant-push-worker.js` into the assets again, since only the new worker forwards the run. |

## @newheap/platform-ai-chat: a slot per entry of agent metadata in the agent editor

Below its form, the agent editor of `@newheap/platform-ai-chat/admin` shows the application's fields of an
existing agent (`nh-assistant-agent-metadata`): one slot per entry of the run catalog's `agentMetadata`,
with its title, where its value comes from (code, override or stored with the agent) and whether only
code changes it. The application renders its own editor in a slot: `provideNhAssistantAgentMetadataEditors`
maps an entry name to a standalone component, which injects `NhAssistantAgentMetadataSlot` for the value
and saves with `save` and `remove`, through `NhAssistantAdminApiService.saveAgentMetadata` and
`removeAgentMetadata` with the entry's version; there is no generic form. A change of an agent created
in the administration is its next version, which the editor's next save continues on; a new agent shows
its fields once it is saved. `AgentMetadata`, `AgentMetadataEntry`, `AgentMetadataRequest`,
`AdminAgentInput.metadata` and `getAgentMetadata` complete the client. The slot's frame and the codes
`assistant-metadata-entry-not-found` and `assistant-metadata-read-only` have English and Dutch texts. The
mock API takes `admin.agentMetadata` and the agents' `metadata` of its scenario.

| Breaking change | Required action |
|---|---|
| The agent editor shows a section of application fields below its form when the application registers entries of agent metadata. | Register an editor per entry with `provideNhAssistantAgentMetadataEditors`; an entry without one says that the application shows no editor. |

## @newheap/platform-ai-chat: proposals the application executes

The proposal cards of the runs view, the thread, the proposal views and the approval inbox show the new
status `awaiting-application` with the report deadline and a note that the application carries the
proposal out; an `execution-unknown` proposal the application executes offers "Hand to the application
again" next to "Mark as resolved" instead of "Run again". `AgentProposalStatus` and `ApprovalStatus` have
the value `awaiting-application`, `AgentProposal` the field `executionReportDueAt`, and
`AgentExecutionResolution` the value `hand-to-application`; the new texts have English and Dutch versions.
The mock API takes `executedByApplication`, `executionReportDueAt` and `executionResultCode` for the
proposals of its scenario and serves `POST approvals/{id}/resolve` as the server does.

| Breaking change | Required action |
|---|---|
| `AgentProposalStatus` and `ApprovalStatus` have the new value `awaiting-application`, and `AgentExecutionResolution` the value `hand-to-application`. | Let exhaustive switches and status maps of your own handle them. |
| The texts `nh-assistant.approval.status.awaiting-application`, `nh-assistant.agent-runs.proposal.status.awaiting-application`, `nh-assistant.agent-runs.proposal.hand-to-application`, `nh-assistant.agent-runs.proposal.unknown-application-hint`, `nh-assistant.agent-runs.proposal.awaiting-application-hint` and `nh-assistant.agent-runs.proposal.report-due` are new. | Add them to your application's translations when it overrides the library's texts (`translations: 'host'`). |

## @newheap/platform-ai-chat: instructions from the application's text assets

When the run catalog reports `instructionAssets`, the skill form and the agent editor of
`@newheap/platform-ai-chat/admin` ask where the instructions come from: "Written here" or "The
application's text". With the application's text an administrator picks one of the listed texts, or
types its id and version when the application lists none, reads it read-only with its hash and saves
the reference; a newer version of the same text is pointed out and used only once saved. A saved
reference that no longer resolves shows its problem in the form, in the skill's card and, for an agent,
in the agent list. `NhAssistantAdminApiService.listInstructionAssets` and `getInstructionAsset`,
`InstructionAssetReference`, `InstructionAssetProblem`, `InstructionAssetSummary`, `InstructionAssetList`,
`InstructionAssetText`, `instructionsAsset` of `DataSkill`, `DataSkillRequest`, `DataSkillVersion` and
`AdminAgentInput`, `AdminAgent.instructionsProblem` and `RunCatalog.instructionAssets` complete the client.
The texts have English and Dutch versions. The mock API takes `admin.instructionAssets` of its scenario
and changes them with `NhAssistantMockBackend.setInstructionAsset` and `removeInstructionAsset`.

| Breaking change | Required action |
|---|---|
| The texts under `nh-assistant.admin.instruction-assets`, the problems `asset-*` and `no-asset-source` under `nh-assistant.admin.run-config.problems.code`, and the errors `assistant-instructions-asset-not-found`, `assistant-instructions-asset-unavailable` and `assistant-agent-instructions-unavailable` are new. | Add them to your application's translations when it overrides the library's texts (`translations: 'host'`). |

## @newheap/platform-ai-chat: trigger candidates and start states in the administration

The trigger form of `@newheap/platform-ai-chat/admin` asks how a change of a saved trigger takes effect:
"Apply at once" (the default) or "Evaluate in shadow next to the live version first" (`apply: 'shadow'`).
The run configuration then shows the trigger's candidate with what it changes against the live version,
its problem when it no longer fits the code, the comparison of both versions and their shared events,
promote and discard after a confirmation, and every trigger's history.
`NhAssistantAdminApiService.getDataTriggerVersions`, `promoteTriggerCandidate`, `discardTriggerCandidate`
and `compareTriggerCandidate`, `DataTriggerVersion`, `TriggerCandidateRequest`, `TriggerComparison`,
`DataTrigger.candidate`, `createdAt` and `createdBy`, `DataTriggerRequest.apply`, and `triggerVersion` and
`triggerCandidate` of a run complete the client. The agent editor offers "Start in shadow" for a new
agent that runs on its own (`startsInShadow`), and the consultation form "Start off" (`startsOff`, or a
note when the ceiling reports `startsOff`); the Consultations section marks a consultation that is off and
switches it on and off with `setDataConsultationState` (`DataConsultationState`,
`DataConsultationStateRequest`, `DataConsultation.off`). The texts have English and Dutch versions, and
the mock API keeps trigger versions, candidates, their history and the consultation state as the server
does.

| Breaking change | Required action |
|---|---|
| `RunConfigurationProblemKind` has the new value `trigger-candidate`. | Let code that switches over the kinds of run configuration problems handle it. |
| The texts `nh-assistant.admin.agent-editor.starts-in-shadow` and `starts-in-shadow-hint`, `nh-assistant.admin.run-config.problems.kind.trigger-candidate`, the texts under `nh-assistant.admin.run-config.triggers` for `saved-candidate`, `apply`, `apply-live`, `apply-shadow`, `versions` and `candidate`, the texts under `nh-assistant.admin.run-config.consultations` for `off-badge`, `off-explain`, `switch-on`, `switch-off`, `switched-on` and `switched-off` and their named variants, `form.starts-off`, `form.starts-off-hint` and `form.starts-off-ceiling`, and the error `assistant-trigger-candidate-not-found` are new. | Add them to your application's translations when it overrides the library's texts (`translations: 'host'`). |

## @newheap/platform-ai-chat: hand-offs and shadow variants as data

Below the run configuration, `NhAssistantAdminAgentsComponent` of `@newheap/platform-ai-chat/admin` shows
an agent's hand-offs (`nh-assistant-agent-hand-offs`) and shadow variants
(`nh-assistant-agent-shadow-variants`). The hand-offs list those in code read only and those defined as
data with their version, sources, daily limit, generated tool and problem; the form adds and changes one
with only the kinds of object and sources the ceiling allows. The shadow variants list those in code and
as data, and "New shadow variant" creates one from the agent's definition and continues in its run
configuration. `NhAssistantAdminApiService.getAgentHandOffs`, `saveDataHandOff`, `removeDataHandOff` and
`getShadowVariants`, `AgentHandOffs`, `CodeHandOff`, `DataHandOff`, `DataHandOffRequest`, `HandOffCeiling`,
`HandOffAllowance`, `AgentShadowVariants`, `ShadowVariant` and `AdminAgentInput.shadowOf` complete the
client. The texts have English and Dutch versions. The mock API takes `admin.handOffsAsData`,
`admin.shadowVariantsAsData` and `codeHandOffs` of its agents and serves the new routes as the server does.

| Breaking change | Required action |
|---|---|
| `RunConfigurationProblemKind` has the new value `hand-off`. | Let exhaustive switches and maps of your own handle it. |
| The texts under `nh-assistant.admin.hand-offs` and `nh-assistant.admin.shadow-variants`, the kind `hand-off` and the codes `outside-ceiling`, `unknown-source`, `unknown-live-agent`, `live-agent-not-live` and `too-many-variants` under `nh-assistant.admin.run-config.problems`, and the errors `assistant-handoffs-not-allowed`, `assistant-handoff-in-code`, `assistant-handoff-not-found` and `assistant-shadow-variants-not-allowed` are new. | Add them to your application's translations when it overrides the library's texts (`translations: 'host'`). |
| The text `nh-assistant.errors.assistant-run-settings-in-use` now names hand-offs among what keeps an agent's run settings in use, and the mock API refuses to remove the run settings of an agent that accepts a hand-off defined as data. | Update the text in your application's translations when it overrides the library's texts. |

## @newheap/platform-ai-chat: agent packages

The agents page of `NhAssistantAdminAgentsComponent` in `@newheap/platform-ai-chat/admin` imports and
exports agent packages. "Export as package" on an agent opens a dialog with that agent chosen: the
agents to export, the package id, version and description (empty for the server's defaults), per part
of each agent whether the package locks it as a whole or by its lockable fields, and every reference to
an agent outside the selection with "Add"; "Download package" saves `agent-package-{id}.json`.
"Import package" takes a file or pasted JSON, checks it per agent and part (`created`, `changed` with
"changed locally", `unchanged`, `kept-local`, `left-behind` or `refused` with its code and field errors),
asks the technical account of each agent whose run settings answer to one, applies changed skills and
triggers live or as candidates in shadow, relinks kept-local parts on request, offers to take over an
agent the host has with a package agent's id and no package (`relink` of the whole agent) instead of
refusing it, and imports all or nothing. A linked agent shows its package on its card with "Unlink the agent"; the agent editor, the
forms of run settings, skills, triggers, consultations and hand-offs and the metadata slots show the
fields a package locks read-only with a lock and the package's id and version, the lists offer "Unlink"
per part with a reason, and removing a locked part, deleting a linked agent and resetting a locked
definition are disabled with the reason. `NhAssistantAdminApiService.exportAgentPackage`,
`previewAgentPackageImport`, `importAgentPackage` and `unlinkAgentPackage` with `AgentPackage` and its
part records, `AgentPackageExport`, `AgentPackagePreview`, `AgentPackageImport`, `AgentPackagePart`,
`AgentPackageUnlink`, `PackagePartLink`, `AgentPackageLink`, `NH_ASSISTANT_PACKAGE_LOCK_FIELDS` and the
`package` link on `AdminAgent`, `AgentRunSettings`, `DataSkill`, `DataTrigger`, `DataConsultation`,
`DataHandOff` and `AgentMetadata` complete the client. The texts have English and Dutch versions. The
mock API exports, previews and imports packages with the server's rules, refuses locked fields in every
save path and takes `admin.agentPackages` as packages its host imported.

| Breaking change | Required action |
|---|---|
| `NhAssistantIconName` has the new icon `lock`. | Add a class for it to complete `Record<NhAssistantIconName, string>` maps of `NH_ASSISTANT_ICONS`. |
| `NH_ASSISTANT_SERVER_ERROR_CODES` has `assistant-package-format-unsupported`, `assistant-package-too-large`, `assistant-package-refused`, `assistant-package-locked` and `assistant-package-not-linked`, and the field errors have `locked`. | Let code that maps assistant codes handle them; add their texts when your application overrides the library's texts (`translations: 'host'`). |
| The texts under `nh-assistant.admin.packages` are new. | Add them to your application's translations when it overrides the library's texts. |
| `NhAssistantAgentMetadataSlot.readOnly` is also `true` while the agent's package locks the entry's value. | Keep your editor of agent metadata read-only while `readOnly` is `true`. |
