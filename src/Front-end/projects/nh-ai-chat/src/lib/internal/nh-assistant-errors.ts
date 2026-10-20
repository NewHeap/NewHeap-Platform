import { NhAssistantClientErrorCodes } from '../models/assistant-sse.models';
import { NhAssistantError } from '../services/nh-assistant.store';
import { NhAssistantApiError, nhAssistantErrorMessageKey } from '../services/nh-assistant-transport';

/** The text every error falls back to when neither its key nor its code has one. */
const genericErrorKey = 'nh-assistant.errors.generic';

/** Converts any failure into a user-facing code and translation key, never raw text. */
export function toNhAssistantError(error: unknown): NhAssistantError {
  if (error instanceof NhAssistantApiError) {
    return Object.keys(error.fieldErrors).length > 0
      ? { code: error.code, messageKey: error.messageKey, fieldErrors: error.fieldErrors }
      : { code: error.code, messageKey: error.messageKey };
  }

  return nhAssistantErrorOf(NhAssistantClientErrorCodes.server);
}

/** The error of a code without a message key of its own, such as a code a callback page reported. */
export function nhAssistantErrorOf(code: string): NhAssistantError {
  return { code, messageKey: nhAssistantErrorMessageKey(code) };
}

/** Translation keys for an error, from the most to the least specific. */
export function nhAssistantErrorKeys(error: NhAssistantError | null): string[] {
  return error ? [error.messageKey, nhAssistantErrorMessageKey(error.code), genericErrorKey] : [];
}

/**
 * Translation keys for a bare failure code, such as a run's `failureCode`: the registered text of
 * the code, then the generic text. Empty without a code.
 */
export function nhAssistantCodeKeys(code: string | null | undefined): string[] {
  return code ? [nhAssistantErrorMessageKey(code), genericErrorKey] : [];
}

/** Dash-case identifiers as the contract requires for agent and MCP server ids. */
export const NH_ASSISTANT_DASH_CASE = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;
