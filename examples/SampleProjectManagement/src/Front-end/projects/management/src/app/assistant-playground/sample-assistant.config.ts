import { EnvironmentProviders, Injectable, inject, makeEnvironmentProviders, signal } from '@angular/core';
import { Title } from '@angular/platform-browser';
import { Router } from '@angular/router';
import {
  ClientContext,
  NH_ASSISTANT_ICONS,
  NhAssistantAccessPolicy,
  NhAssistantConfig,
  NhAssistantIconName,
  provideNhAssistant
} from '@newheap/platform-ai-chat';
import { SampleAuthService } from 'sample-project-management-common';
import { Observable, map } from 'rxjs';

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
      map(() => this.authService.isOnePermissionGranted([SAMPLE_ASSISTANT_PERMISSION]))
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
  link: 'ph ph-plugs-connected',
  page: 'ph ph-file-text'
};

/** The bearer token of the NewHeap session; runs in the assistant's injection context. */
export function sampleAssistantAccessToken(): string | null {
  return inject(SampleAuthService).getAuthorization()?.token || null;
}

/** Separates the portal's browser UI pointers by account; no token or page hints are stored. */
export function sampleAssistantStateScope(): string | null {
  const userId = inject(SampleAuthService).getAuthorization()?.user?.id;
  return userId ? `sample-portal:${userId}` : null;
}

/**
 * What the user has open. Pages that show an entity set it while they are active, for
 * example a project page with `{ type: 'project', id: 'PRJ-ALPHA' }`, and clear it when
 * they close.
 */
@Injectable({ providedIn: 'root' })
export class SampleAssistantPageContext {
  private readonly current = signal<ClientContext | null>(null);

  readonly value = this.current.asReadonly();

  set(context: ClientContext): void {
    this.current.set(context);
  }

  clear(): void {
    this.current.set(null);
  }
}

/**
 * Page context for each message: the entity page the user has open, otherwise the route
 * and title. Runs in the assistant's injection context. It only describes the screen;
 * the API still authorizes every tool call.
 */
export function sampleAssistantPageContext(): ClientContext | null {
  const page = inject(SampleAssistantPageContext).value();
  if (page) {
    return page;
  }

  return { route: inject(Router).url, title: inject(Title).getTitle() };
}

/**
 * Registers the assistant for the management portal: the `/api/assistant` endpoints,
 * the NewHeap session token, the page context, the permission-based access policy and the
 * sample icons.
 */
export function provideSampleAssistant(overrides: Partial<NhAssistantConfig> = {}): EnvironmentProviders {
  return makeEnvironmentProviders([
    provideNhAssistant({
      apiBaseUrl: '/api/assistant',
      getAccessToken: sampleAssistantAccessToken,
      getPageContext: sampleAssistantPageContext,
      getStateScope: sampleAssistantStateScope,
      accessPolicy: SampleAssistantAccessPolicy,
      ...overrides
    }),
    { provide: NH_ASSISTANT_ICONS, useValue: SAMPLE_ASSISTANT_ICONS }
  ]);
}
