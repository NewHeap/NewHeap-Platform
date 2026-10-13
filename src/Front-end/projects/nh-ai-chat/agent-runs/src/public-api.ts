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
