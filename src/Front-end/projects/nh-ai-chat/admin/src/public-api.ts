/*
 * Public API surface of @newheap/platform-ai-chat/admin.
 *
 * The administration page is a separate entry point so hosts load it only on their lazy
 * admin route and the chat panel stays small.
 */
export * from './nh-assistant-admin.component';
export * from './context/nh-assistant-admin-context.component';
export * from './agents/nh-assistant-admin-agents.component';
export * from './agents/nh-assistant-admin-agent-editor.component';
export * from './agents/metadata/nh-assistant-agent-metadata-slot';
export * from './agents/metadata/nh-assistant-agent-metadata.component';
export * from './agents/nh-assistant-agent-evaluation.component';
export * from './agents/nh-assistant-agent-feedback-report.component';
export * from './agents/nh-assistant-agent-run-history.component';
export * from './agents/nh-assistant-agent-run-configuration.component';
export * from './mcp/nh-assistant-admin-mcp-servers.component';
export * from './mcp/nh-assistant-admin-mcp-server-editor.component';
export * from './mcp/nh-assistant-admin-mcp-tools.component';
