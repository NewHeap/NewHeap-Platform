import { NhAssistantClientErrorCodes } from '../models/assistant-sse.models';
import { NhAssistantError } from '../services/nh-assistant.store';
import { NhAssistantApiError, nhAssistantErrorMessageKey } from '../services/nh-assistant-transport';

/** Converts any failure into a user-facing code and translation key, never raw text. */
export function toNhAssistantError(error: unknown): NhAssistantError {
  if (error instanceof NhAssistantApiError) {
    return { code: error.code, messageKey: error.messageKey };
  }

  return { code: NhAssistantClientErrorCodes.server, messageKey: nhAssistantErrorMessageKey(NhAssistantClientErrorCodes.server) };
}

/** Translation keys for an error, from the most to the least specific. */
export function nhAssistantErrorKeys(error: NhAssistantError | null): string[] {
  return error ? [error.messageKey, nhAssistantErrorMessageKey(error.code), 'nh-assistant.errors.generic'] : [];
}

/** Dash-case identifiers as the contract requires for agent and MCP server ids. */
export const NH_ASSISTANT_DASH_CASE = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;
