/*
 * Public API surface of @newheap/platform-ai-chat.
 */
export * from './lib/models/assistant-api.models';
export * from './lib/models/assistant-sse.models';
export * from './lib/models/assistant-admin.models';
export * from './lib/nh-assistant.config';
export * from './lib/provide-nh-assistant';
export * from './lib/i18n/nh-assistant-translations';
export * from './lib/services/nh-assistant-sse-parser';
export { NhAssistantStatusCodes } from './lib/services/nh-assistant-transport';
export * from './lib/services/nh-assistant-api.service';
export * from './lib/services/nh-assistant-admin-api.service';
export * from './lib/services/nh-assistant-reducer';
export * from './lib/services/nh-assistant.store';
export * from './lib/services/nh-assistant-panel.service';
export { NH_ASSISTANT_ICONS, NhAssistantIconName } from './lib/internal/nh-assistant-icon.component';
export * from './lib/components/launcher/nh-assistant-launcher.component';
export * from './lib/components/panel/nh-assistant-panel.component';
export * from './lib/components/conversation-list/nh-assistant-conversation-list.component';
export * from './lib/components/thread/nh-assistant-thread.component';
export * from './lib/components/composer/nh-assistant-composer.component';
export * from './lib/components/tool-call-card/nh-assistant-tool-call-card.component';
export * from './lib/components/approval-card/nh-assistant-approval-card.component';
export * from './lib/components/agent-picker/nh-assistant-agent-picker.component';
export * from './lib/components/preferences/nh-assistant-preferences.component';

/*
 * Internal building blocks shared with the secondary entry points. Not part of the public
 * API; they may change without notice.
 */
export { NhAssistantIconComponent as ɵNhAssistantIconComponent } from './lib/internal/nh-assistant-icon.component';
export { NhAssistantTranslatePipe as ɵNhAssistantTranslatePipe } from './lib/internal/nh-assistant-translate.pipe';
export {
  NH_ASSISTANT_DASH_CASE as ɵNH_ASSISTANT_DASH_CASE,
  nhAssistantErrorKeys as ɵnhAssistantErrorKeys,
  toNhAssistantError as ɵtoNhAssistantError
} from './lib/internal/nh-assistant-errors';
