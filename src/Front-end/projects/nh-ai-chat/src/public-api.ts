/*
 * Public API surface of @newheap/platform-ai-chat.
 */
export * from './lib/models/assistant-api.models';
export * from './lib/models/assistant-sse.models';
export * from './lib/models/assistant-live.models';
export * from './lib/models/assistant-admin.models';
export * from './lib/models/assistant-agent-run.models';
export * from './lib/nh-assistant.config';
export * from './lib/provide-nh-assistant';
export * from './lib/i18n/nh-assistant-translations';
export * from './lib/i18n/nh-assistant-error-codes';
export * from './lib/services/nh-assistant-sse-parser';
export { NhAssistantStatusCodes } from './lib/services/nh-assistant-transport';
export * from './lib/services/nh-assistant-api.service';
export * from './lib/services/nh-assistant-admin-api.service';
export * from './lib/services/nh-assistant-agent-run-api.service';
export * from './lib/services/nh-assistant-reducer';
export * from './lib/services/nh-assistant-page-context';
export * from './lib/services/nh-assistant.store';
export { NhAssistantLiveService, NhAssistantLiveState } from './lib/services/nh-assistant-live.service';
export { NhAssistantPushService, NhAssistantPushState } from './lib/services/nh-assistant-push.service';
export * from './lib/services/nh-assistant-panel.service';
export * from './lib/services/nh-assistant-subject-summary.service';
export * from './lib/services/nh-assistant-share-link';
export * from './lib/services/nh-assistant-mcp-authorization';
export * from './lib/services/nh-assistant-mcp-connect.service';
export { toNhAssistantError } from './lib/internal/nh-assistant-errors';
export { NH_ASSISTANT_ICONS, NhAssistantIconName } from './lib/internal/nh-assistant-icon.component';
export * from './lib/components/launcher/nh-assistant-launcher.component';
export * from './lib/components/panel/nh-assistant-panel.component';
export * from './lib/components/conversation-list/nh-assistant-conversation-list.component';
export * from './lib/components/thread/nh-assistant-thread.component';
export * from './lib/components/composer/nh-assistant-composer.component';
export * from './lib/components/tool-call-card/nh-assistant-tool-call-card.component';
export * from './lib/components/tool-call-group/nh-assistant-tool-call-group.component';
export * from './lib/components/approval-card/nh-assistant-approval-card.component';
export * from './lib/components/proposal-feedback/nh-assistant-proposal-feedback.component';
export * from './lib/components/agent-picker/nh-assistant-agent-picker.component';
export * from './lib/components/preferences/nh-assistant-preferences.component';
export * from './lib/components/preferences/nh-assistant-mcp-connections.component';
export * from './lib/components/connection-required/nh-assistant-connection-required.component';
export * from './lib/components/share/nh-assistant-share.component';
export * from './lib/components/activity/nh-assistant-activity.component';
export * from './lib/components/subject-badge/nh-assistant-subject-badge.component';

/*
 * Internal building blocks shared with the secondary entry points. Not part of the public
 * API; they may change without notice.
 */
export { NhAssistantIconComponent as ɵNhAssistantIconComponent } from './lib/internal/nh-assistant-icon.component';
export { NhAssistantTranslatePipe as ɵNhAssistantTranslatePipe } from './lib/internal/nh-assistant-translate.pipe';
export {
  nhAssistantReloadOnAgentWork as ɵnhAssistantReloadOnAgentWork,
  nhAssistantWatchAgentWork as ɵnhAssistantWatchAgentWork
} from './lib/internal/nh-assistant-agent-work';
export {
  NhAssistantPickerComponent as ɵNhAssistantPickerComponent,
  type NhAssistantPickerOption as ɵNhAssistantPickerOption
} from './lib/internal/nh-assistant-picker.component';
export { NhAssistantSubjectLinkComponent as ɵNhAssistantSubjectLinkComponent } from './lib/internal/nh-assistant-subject-link.component';
export { NhAssistantMarkdownPipe as ɵNhAssistantMarkdownPipe } from './lib/internal/nh-assistant-markdown.pipe';
// The upload media type rule, shared with the mock API of the testing entry point.
export { nhAssistantMediaTypeOf as ɵnhAssistantMediaTypeOf } from './lib/internal/nh-assistant-files';
export {
  NH_ASSISTANT_DASH_CASE as ɵNH_ASSISTANT_DASH_CASE,
  nhAssistantCodeKeys as ɵnhAssistantCodeKeys,
  nhAssistantErrorKeys as ɵnhAssistantErrorKeys,
  nhAssistantErrorOf as ɵnhAssistantErrorOf,
  // Kept for the secondary entry points and earlier callers; `toNhAssistantError` is public.
  toNhAssistantError as ɵtoNhAssistantError
} from './lib/internal/nh-assistant-errors';
