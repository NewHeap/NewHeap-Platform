import { EnvironmentProviders, inject, makeEnvironmentProviders } from '@angular/core';
import {
  NH_ASSISTANT_FETCH,
  NH_ASSISTANT_MCP_AUTHORIZATION_WINDOW,
  NH_ASSISTANT_UPLOAD,
  NhAssistantLiveService
} from '@newheap/platform-ai-chat';
import { NH_ASSISTANT_MOCK_SCENARIO, NhAssistantMockBackend } from './nh-assistant-mock-backend';
import { NhAssistantMockLiveService } from './nh-assistant-mock-live';
import { NhAssistantMockScenario } from './nh-assistant-mock.models';

/**
 * Replaces the assistant transport with an in-memory back-end that plays `script`.
 * Register it after `provideNhAssistant(...)` in the same injector. Every client feature
 * (status, agents, conversations, streamed turns, approvals, cancellation, sharing, read
 * state, live updates, uploads with their progress and MCP authorizations) then works without a
 * server. Inject `NhAssistantMockBackend` to switch the feature flag, simulate a colleague or
 * inspect the received requests and uploads.
 */
export function provideNhAssistantMockApi(script: NhAssistantMockScenario): EnvironmentProviders {
  return makeEnvironmentProviders([
    { provide: NH_ASSISTANT_MOCK_SCENARIO, useValue: script },
    NhAssistantMockBackend,
    { provide: NH_ASSISTANT_FETCH, useFactory: () => inject(NhAssistantMockBackend).fetch },
    { provide: NH_ASSISTANT_UPLOAD, useFactory: () => inject(NhAssistantMockBackend).upload },
    { provide: NhAssistantLiveService, useClass: NhAssistantMockLiveService },
    { provide: NH_ASSISTANT_MCP_AUTHORIZATION_WINDOW, useFactory: () => inject(NhAssistantMockBackend).authorizationWindow }
  ]);
}
