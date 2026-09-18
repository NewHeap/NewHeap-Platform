import { EnvironmentProviders, inject, makeEnvironmentProviders } from '@angular/core';
import { NH_ASSISTANT_FETCH } from '@newheap/platform-ai-chat';
import { NH_ASSISTANT_MOCK_SCENARIO, NhAssistantMockBackend } from './nh-assistant-mock-backend';
import { NhAssistantMockScenario } from './nh-assistant-mock.models';

/**
 * Replaces the assistant transport with an in-memory back-end that plays `script`.
 * Register it after `provideNhAssistant(...)` in the same injector. Every client feature
 * (status, agents, conversations, streamed turns, approvals, cancellation) then works
 * without a server. Inject `NhAssistantMockBackend` to switch the feature flag or to
 * inspect the received requests.
 */
export function provideNhAssistantMockApi(script: NhAssistantMockScenario): EnvironmentProviders {
  return makeEnvironmentProviders([
    { provide: NH_ASSISTANT_MOCK_SCENARIO, useValue: script },
    NhAssistantMockBackend,
    { provide: NH_ASSISTANT_FETCH, useFactory: () => inject(NhAssistantMockBackend).fetch }
  ]);
}
