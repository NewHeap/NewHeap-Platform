import * as publicApi from '../../public-api';
import { NhAssistantApiError } from '../services/nh-assistant-transport';

/**
 * The error helpers that the release notes name for consumers are exported from the main entry
 * point: the server failure codes, every code the UI can show and the conversion of a failure into a
 * code and translation key. The internal alias stays for earlier callers.
 */
describe('@newheap/platform-ai-chat public API', () => {
  it('exports the error codes and toNhAssistantError, and keeps the internal alias', () => {
    expect(publicApi.NH_ASSISTANT_SERVER_ERROR_CODES).toContain('assistant-budget-exhausted');
    expect(publicApi.NH_ASSISTANT_ERROR_CODES).toEqual(jasmine.arrayContaining([...publicApi.NH_ASSISTANT_SERVER_ERROR_CODES]));
    expect(publicApi.ɵtoNhAssistantError).toBe(publicApi.toNhAssistantError);

    const error = publicApi.toNhAssistantError(new NhAssistantApiError(409, 'assistant-conversation-busy', 'nh-assistant.errors.assistant-conversation-busy'));

    expect(error).toEqual({ code: 'assistant-conversation-busy', messageKey: 'nh-assistant.errors.assistant-conversation-busy' });
  });
});
