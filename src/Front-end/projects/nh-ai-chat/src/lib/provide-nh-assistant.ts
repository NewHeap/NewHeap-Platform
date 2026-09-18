import { EnvironmentProviders, inject, makeEnvironmentProviders, provideEnvironmentInitializer } from '@angular/core';
import { NhAssistantTranslationMerger } from './i18n/nh-assistant-translations';
import {
  NH_ASSISTANT_ACCESS_POLICY,
  NH_ASSISTANT_CONFIG,
  NhAssistantAllowAllAccessPolicy,
  NhAssistantConfig
} from './nh-assistant.config';
import { NhAssistantAdminApiService } from './services/nh-assistant-admin-api.service';
import { NhAssistantApiService } from './services/nh-assistant-api.service';
import { NhAssistantPanelService } from './services/nh-assistant-panel.service';
import { NhAssistantStore } from './services/nh-assistant.store';
import { NhAssistantTransport } from './services/nh-assistant-transport';

/**
 * Registers the assistant client, store and panel for one application or route scope.
 * Call it once in the root providers; a route may call it again to host an isolated
 * assistant with its own configuration.
 */
export function provideNhAssistant(config: NhAssistantConfig): EnvironmentProviders {
  return makeEnvironmentProviders([
    { provide: NH_ASSISTANT_CONFIG, useValue: config },
    { provide: NH_ASSISTANT_ACCESS_POLICY, useClass: config.accessPolicy ?? NhAssistantAllowAllAccessPolicy },
    NhAssistantTransport,
    NhAssistantApiService,
    NhAssistantAdminApiService,
    NhAssistantStore,
    NhAssistantPanelService,
    ...(config.translations === 'host'
      ? []
      : [provideEnvironmentInitializer(() => inject(NhAssistantTranslationMerger).start())])
  ]);
}
