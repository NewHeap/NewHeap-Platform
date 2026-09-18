import { EnvironmentProviders, makeEnvironmentProviders } from '@angular/core';
import {
  NH_ASSISTANT_ACCESS_POLICY,
  NH_ASSISTANT_CONFIG,
  NhAssistantAllowAllAccessPolicy,
  NhAssistantConfig
} from './nh-assistant.config';
import { NhAssistantApiService } from './services/nh-assistant-api.service';

/**
 * Registers the assistant client, store and panel for one application or route scope.
 * Call it once in the root providers; a route may call it again to host an isolated
 * assistant with its own configuration.
 */
export function provideNhAssistant(config: NhAssistantConfig): EnvironmentProviders {
  return makeEnvironmentProviders([
    { provide: NH_ASSISTANT_CONFIG, useValue: config },
    { provide: NH_ASSISTANT_ACCESS_POLICY, useClass: config.accessPolicy ?? NhAssistantAllowAllAccessPolicy },
    NhAssistantApiService
  ]);
}
