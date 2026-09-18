/*
 * Public API surface of @newheap/platform-ai-chat.
 */
export * from './lib/models/assistant-api.models';
export * from './lib/models/assistant-sse.models';
export * from './lib/nh-assistant.config';
export * from './lib/provide-nh-assistant';
export * from './lib/i18n/nh-assistant-translations';
export * from './lib/services/nh-assistant-sse-parser';
export * from './lib/services/nh-assistant-api.service';
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
