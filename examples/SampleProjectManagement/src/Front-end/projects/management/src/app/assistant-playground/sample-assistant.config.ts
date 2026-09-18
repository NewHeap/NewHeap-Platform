import { EnvironmentProviders, Injectable, inject, makeEnvironmentProviders } from '@angular/core';
import {
  NH_ASSISTANT_ICONS,
  NhAssistantAccessPolicy,
  NhAssistantConfig,
  NhAssistantIconName,
  provideNhAssistant
} from '@newheap/platform-ai-chat';
import { SampleAuthService } from 'sample-project-management-common';
import { Observable, distinctUntilChanged, map } from 'rxjs';

/** Application permission that grants the assistant, matching the API access policy. */
export const SAMPLE_ASSISTANT_PERMISSION = 'app.assistant.access';

/**
 * Shows the assistant only to users with the assistant permission. It re-evaluates when
 * the signed-in user changes, so the launcher disappears after sign-out.
 */
@Injectable()
export class SampleAssistantAccessPolicy implements NhAssistantAccessPolicy {
  private readonly authService = inject(SampleAuthService);

  canUse(): Observable<boolean> {
    return this.authService.authSubject.pipe(
      map(() => this.authService.isOnePermissionGranted([SAMPLE_ASSISTANT_PERMISSION])),
      distinctUntilChanged()
    );
  }
}

/** Phosphor icons from the sample's icon library for every assistant control. */
export const SAMPLE_ASSISTANT_ICONS: Record<NhAssistantIconName, string> = {
  assistant: 'ph ph-sparkle',
  close: 'ph ph-x',
  send: 'ph ph-paper-plane-right',
  stop: 'ph ph-stop',
  plus: 'ph ph-plus',
  list: 'ph ph-chats',
  trash: 'ph ph-trash',
  tool: 'ph ph-wrench',
  check: 'ph ph-check-circle',
  x: 'ph ph-x-circle',
  warning: 'ph ph-warning',
  'chevron-down': 'ph ph-caret-down',
  clock: 'ph ph-clock',
  settings: 'ph ph-sliders-horizontal',
  admin: 'ph ph-shield-check',
  edit: 'ph ph-pencil-simple',
  refresh: 'ph ph-arrows-clockwise',
  link: 'ph ph-plugs-connected'
};

/** The bearer token of the NewHeap session; runs in the assistant's injection context. */
export function sampleAssistantAccessToken(): string | null {
  return inject(SampleAuthService).getAuthorization()?.token || null;
}

/**
 * Registers the assistant for the management portal: the `/api/assistant` endpoints,
 * the NewHeap session token, the permission-based access policy and the sample icons.
 */
export function provideSampleAssistant(overrides: Partial<NhAssistantConfig> = {}): EnvironmentProviders {
  return makeEnvironmentProviders([
    provideNhAssistant({
      apiBaseUrl: '/api/assistant',
      getAccessToken: sampleAssistantAccessToken,
      accessPolicy: SampleAssistantAccessPolicy,
      ...overrides
    }),
    { provide: NH_ASSISTANT_ICONS, useValue: SAMPLE_ASSISTANT_ICONS }
  ]);
}
