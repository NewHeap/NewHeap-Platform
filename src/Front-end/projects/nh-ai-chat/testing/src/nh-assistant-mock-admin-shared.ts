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

/** A contract error response: `{ code, messageKey, errors? }`, never free text. */
export function mockFailure(status: number, code: string, errors?: Record<string, string[]>): NhAssistantMockResult {
  return { status, body: { code, messageKey: `nh-assistant.errors.${code}`, ...(errors ? { errors } : {}) } };
}

export function mockValidationFailure(field?: string): NhAssistantMockResult {
  return mockFailure(400, 'assistant-validation', field ? { [field]: ['invalid'] } : undefined);
}

/** A stable, non-cryptographic fingerprint; the mock only needs change detection. */
export function mockHashText(text: string): string {
  let hash = 0x811c9dc5;
  for (let index = 0; index < text.length; index++) {
    hash ^= text.charCodeAt(index);
    hash = Math.imul(hash, 0x01000193) >>> 0;
  }
  return `fnv1a:${hash.toString(16).padStart(8, '0')}`;
}
