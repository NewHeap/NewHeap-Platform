import { NhAssistantClientErrorCodes } from '../models/assistant-sse.models';

/**
 * Failure codes the assistant back-end sends in error bodies, SSE `error` events and tool
 * result codes (`NhAssistantErrorCodes`, `NhAssistantAdminErrorCodes` and the runtime codes
 * of `NewHeap.Platform.AI.Chat`). Every code needs a text under `nh-assistant.errors.<code>`
 * in each bundled language; the translation tests enforce that.
 */
export const NH_ASSISTANT_SERVER_ERROR_CODES: readonly string[] = [
  // Chat endpoints and turns
  'assistant-disabled',
  'assistant-conversation-not-found',
  'assistant-conversation-busy',
  'assistant-agent-not-found',
  'assistant-agent-forbidden',
  'assistant-message-invalid',
  'assistant-message-too-long',
  'assistant-message-duplicate',
  'assistant-title-invalid',
  'assistant-approval-not-found',
  'assistant-approval-not-pending',
  'assistant-approval-decision-invalid',
  'assistant-proposal-hash-mismatch',
  'assistant-approval-expired',
  'assistant-approval-invalid',
  'assistant-approval-rejected',
  'assistant-budget-exhausted',
  'assistant-tool-call-limit-reached',
  'assistant-tools-disabled',
  'assistant-turn-timeout',
  'assistant-model-unavailable',
  'assistant-turn-failed',
  'assistant-context-unavailable',
  'assistant-actor-mismatch',
  // Preferences and administration
  'assistant-validation',
  'assistant-instructions-too-long',
  'assistant-forbidden',
  'assistant-not-found',
  'assistant-context-not-found',
  'assistant-version-conflict',
  'assistant-agent-exists',
  'assistant-code-agent-not-deletable',
  'assistant-agent-not-code',
  'assistant-mcp-server-not-found',
  'assistant-mcp-server-exists',
  'assistant-mcp-tool-not-found',
  'assistant-mcp-unreachable',
  'assistant-mcp-unauthorized',
  'assistant-mcp-host-blocked',
  'assistant-mcp-schema-changed',
  'assistant-mcp-tool-disabled'
];

/** Every code the assistant UI can show: server codes and client-side transport codes. */
export const NH_ASSISTANT_ERROR_CODES: readonly string[] = [
  ...new Set([...NH_ASSISTANT_SERVER_ERROR_CODES, ...Object.values(NhAssistantClientErrorCodes)])
];
