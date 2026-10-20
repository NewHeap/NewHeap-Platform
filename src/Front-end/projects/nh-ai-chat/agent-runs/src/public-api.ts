/*
 * Public API surface of @newheap/platform-ai-chat/agent-runs.
 *
 * Agents that run on their own are a separate entry point, so hosts load the inbox and the
 * subject views only on the routes that show them and the chat panel stays small. The
 * components use `NhAssistantAgentRunApiService` from the `provideNhAssistant` scope and reload
 * by themselves on the agent-work updates of its `NhAssistantLiveService`.
 */
export * from './nh-assistant-agent-inbox.component';
export * from './nh-assistant-agent-thread-view.component';
export * from './nh-assistant-agent-proposals.component';
export * from './nh-assistant-run-timeline.component';
export * from './nh-assistant-agent-memory.component';
export * from './nh-assistant-agent-artifacts.component';
export * from './nh-assistant-agent-consultations.component';
export * from './nh-assistant-subject-suggestions.component';
export * from './nh-assistant-object-agents.component';
export * from './nh-assistant-run-result.component';

/*
 * Internal building blocks shared with the administration entry point; they live here so the chat's
 * initial bundle stays small. Not part of the public API; they may change without notice.
 */
export {
  NhAssistantRunNowDialogComponent as ɵNhAssistantRunNowDialogComponent,
  NH_ASSISTANT_RUN_NOW_MAX_MESSAGE as ɵNH_ASSISTANT_RUN_NOW_MAX_MESSAGE,
  type NhAssistantRunNowAgent as ɵNhAssistantRunNowAgent,
  type NhAssistantRunNowStarted as ɵNhAssistantRunNowStarted,
  type NhAssistantRunNowSubject as ɵNhAssistantRunNowSubject
} from './internal/nh-assistant-run-now-dialog.component';
export {
  NhAssistantSubjectPickerComponent as ɵNhAssistantSubjectPickerComponent,
  nhAssistantSubjectSearch as ɵnhAssistantSubjectSearch,
  type NhAssistantChosenSubject as ɵNhAssistantChosenSubject
} from './internal/nh-assistant-subject-picker.component';
