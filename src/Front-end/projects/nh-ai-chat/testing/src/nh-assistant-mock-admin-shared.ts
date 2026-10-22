import { mockStatusOf } from './nh-assistant-mock-status';

/** A mock response: status plus optional JSON body. */
export interface NhAssistantMockResult {
  status: number;
  body?: unknown;
}

/** Ids of agents and MCP servers. */
export const mockDashCase = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;

/** The longest instructions of an agent or the application context. */
export const mockMaxInstructions = 20_000;

/** The longest reason of a state or shadow change. */
export const mockMaxStateReason = 500;

/**
 * A contract error response: `{ code, messageKey, errors? }`, never free text, with the one status the
 * server's table gives the code (`mockStatusOf`). Field errors may come with any code.
 */
export function mockFailure(code: string, errors?: Record<string, string[]>): NhAssistantMockResult {
  return {
    status: mockStatusOf(code),
    body: { code, messageKey: `nh-assistant.errors.${code}`, ...(errors && Object.keys(errors).length > 0 ? { errors } : {}) }
  };
}

export function mockValidationFailure(field?: string): NhAssistantMockResult {
  return mockFailure('assistant-validation', field ? { [field]: ['invalid'] } : undefined);
}

/** A stable, non-cryptographic fingerprint; the mock only needs change detection. */
/**
 * The warnings of an agent or skill that classifies its model calls with `classification` on
 * `profile`: `classification-not-permitted` when the scenario lists what the profile and its
 * fallbacks permit and that does not include it, as the server reports it; none without a
 * classification of its own.
 */
export function mockClassificationWarnings(
  permitted: Record<string, readonly string[]> | undefined,
  profile: string | null | undefined,
  classification: string | null | undefined
): string[] {
  const allowed = profile ? permitted?.[profile] : undefined;
  return classification && allowed && !allowed.includes(classification) ? ['classification-not-permitted'] : [];
}

export function mockHashText(text: string): string {
  let hash = 0x811c9dc5;
  for (let index = 0; index < text.length; index++) {
    hash ^= text.charCodeAt(index);
    hash = Math.imul(hash, 0x01000193) >>> 0;
  }
  return `fnv1a:${hash.toString(16).padStart(8, '0')}`;
}
