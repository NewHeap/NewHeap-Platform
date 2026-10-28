/**
 * The HTTP status of every failure code, as the server's one table maps them for every assistant
 * endpoint (chat, administration, agent runs, subjects, run histories, boards, MCP and OAuth). Each
 * code has exactly one status; a code the table does not name answers 400, as on the server. Every
 * failure of the mock API takes its status from here, so the mock never answers a code with a status
 * the server would not use.
 */
const statusTable: Readonly<Record<number, readonly string[]>> = {
  400: [
    'assistant-validation',
    'assistant-message-invalid',
    'assistant-message-too-long',
    'assistant-title-invalid',
    'assistant-approval-decision-invalid',
    'assistant-approval-expired',
    'assistant-approval-invalid',
    'assistant-subject-invalid',
    'assistant-service-account-not-found',
    'assistant-run-not-retryable',
    'assistant-run-filter-invalid',
    'assistant-board-scope-invalid',
    'assistant-feedback-invalid',
    'assistant-question-invalid',
    'assistant-question-answer-invalid',
    'assistant-trigger-filter-not-found',
    'assistant-replay-not-shadow',
    'assistant-evaluation-not-variant',
    'assistant-instructions-too-long',
    'assistant-push-subscription-invalid',
    'assistant-webhook-payload-invalid',
    'assistant-mcp-host-blocked',
    'assistant-mcp-secret-reference-invalid',
    'assistant-mcp-certificate-reference-invalid',
    'assistant-mcp-scope-not-allowed',
    'assistant-mcp-authorization-failed',
    'assistant-mcp-authorization-expired',
    'assistant-mcp-authorization-invalid',
    'assistant-mcp-pkce-unsupported',
    'assistant-mcp-client-registration-required',
    'assistant-mcp-oauth-not-configured',
    'assistant-file-reference-invalid',
    'assistant-file-upload-invalid',
    'assistant-file-too-many-pages',
    'assistant-file-count-limit',
    'assistant-file-token-limit'
  ],
  401: [
    'assistant-webhook-signature-invalid'
  ],
  403: [
    'assistant-agent-forbidden',
    'assistant-context-unavailable',
    'assistant-owner-required',
    'assistant-approval-forbidden',
    'assistant-question-forbidden',
    'assistant-turn-forbidden',
    'assistant-subject-access-denied',
    'assistant-approval-policy-denied',
    'assistant-four-eyes-required',
    'assistant-forbidden',
    'assistant-mcp-credential-mismatch',
    'assistant-mcp-initiator-only',
    'assistant-file-forbidden',
    'assistant-not-allowed-while-impersonating'
  ],
  404: [
    'assistant-disabled',
    'assistant-conversation-not-found',
    'assistant-agent-not-found',
    'assistant-metadata-entry-not-found',
    'assistant-instructions-asset-not-found',
    'assistant-approval-not-found',
    'assistant-question-not-found',
    'assistant-share-link-invalid',
    'assistant-participant-not-found',
    'assistant-directory-unavailable',
    'assistant-push-unavailable',
    'assistant-subject-not-found',
    'assistant-subject-type-unknown',
    'assistant-skill-not-found',
    'assistant-account-search-unavailable',
    'assistant-subject-search-unavailable',
    'assistant-skill-candidate-not-found',
    'assistant-run-not-found',
    'assistant-board-not-found',
    'assistant-artifact-not-found',
    'assistant-trigger-not-found',
    'assistant-consultation-not-found',
    'assistant-handoff-not-found',
    'assistant-package-not-linked',
    'assistant-package-not-found',
    'assistant-not-found',
    'assistant-context-not-found',
    'assistant-mcp-server-not-found',
    'assistant-mcp-tool-not-found',
    'assistant-files-unavailable',
    'assistant-file-not-found'
  ],
  409: [
    'assistant-conversation-busy',
    'assistant-message-duplicate',
    'assistant-approval-not-pending',
    'assistant-proposal-hash-mismatch',
    'assistant-participant-limit-reached',
    'assistant-no-accountable-owner',
    'assistant-agent-disabled',
    'assistant-agent-stopped',
    'assistant-agent-configured-off',
    'assistant-agent-paused',
    'assistant-skill-stopped',
    'assistant-skill-paused',
    'assistant-agent-does-not-work-on',
    'assistant-moved-to-shadow',
    'assistant-shadow-variant',
    'assistant-not-shadow-variant',
    'assistant-service-account-not-used',
    'assistant-consult-route-not-allowed',
    'assistant-consult-proposal-refused',
    'assistant-consult-asker-decides',
    'assistant-consult-proposal-moved',
    'assistant-consultation-in-code',
    'assistant-consultations-not-allowed',
    'assistant-handoffs-not-allowed',
    'assistant-handoff-in-code',
    'assistant-shadow-variants-not-allowed',
    'assistant-skill-in-code',
    'assistant-trigger-in-code',
    'assistant-skill-in-use',
    'assistant-configuration-limit',
    'assistant-run-settings-in-code',
    'assistant-run-settings-in-use',
    'assistant-agent-not-configured',
    'assistant-execution-not-uncertain',
    'assistant-proposal-stale',
    'assistant-memory-conflict',
    'assistant-replay-active',
    'assistant-feedback-not-accepted',
    'assistant-question-not-pending',
    'assistant-question-expired',
    'assistant-question-cancelled',
    'assistant-trigger-rate-limited',
    'assistant-version-conflict',
    'assistant-agent-exists',
    'assistant-code-agent-not-deletable',
    'assistant-agent-not-code',
    'assistant-metadata-read-only',
    'assistant-package-refused',
    'assistant-package-locked',
    'assistant-agent-instructions-unavailable',
    'assistant-mcp-server-exists',
    'assistant-mcp-headless-not-allowed',
    'assistant-mcp-consent-required',
    'assistant-mcp-connection-required',
    'assistant-mcp-reauthorization-required',
    'assistant-mcp-issuer-changed',
    'assistant-mcp-authorization-not-confirmed',
    'assistant-mcp-connection-not-supported',
    'assistant-mcp-secret-unreadable',
    'assistant-files-not-accepted'
  ],
  413: [
    'assistant-webhook-payload-too-large',
    'assistant-package-too-large',
    'assistant-file-too-large'
  ],
  415: [
    'assistant-file-type-not-supported',
    'assistant-file-type-not-accepted'
  ],
  422: [
    'assistant-file-upload-refused',
    'assistant-file-scan-refused'
  ],
  429: [
    'assistant-mcp-too-many-authorizations'
  ],
  502: [
    'assistant-mcp-unreachable',
    'assistant-mcp-unauthorized',
    'assistant-mcp-insufficient-scope',
    'assistant-mcp-delegated-token-unavailable',
    'assistant-mcp-discovery-failed',
    'assistant-mcp-token-request-failed'
  ],
  503: [
    'assistant-proposal-check-failed',
    'assistant-trigger-rolled-back',
    'assistant-mcp-secret-unavailable',
    'assistant-instructions-asset-unavailable',
    'assistant-file-unavailable',
    'assistant-file-upload-failed'
  ]
};

const statuses: ReadonlyMap<string, number> = buildStatuses();

/** The HTTP status the server answers a failure code with; 400 for a code the table does not name. */
export function mockStatusOf(code: string): number {
  return statuses.get(code) ?? 400;
}

/** Every code the table names, with its status (internal, for the library's own tests). */
export function mockStatusTable(): ReadonlyMap<string, number> {
  return statuses;
}

function buildStatuses(): ReadonlyMap<string, number> {
  const result = new Map<string, number>();
  for (const [status, codes] of Object.entries(statusTable)) {
    for (const code of codes) {
      // A code listed twice would have two statuses, which the server's table refuses at startup.
      if (result.has(code)) {
        throw new Error(`The mock status table lists '${code}' twice.`);
      }
      result.set(code, Number(status));
    }
  }
  return result;
}
