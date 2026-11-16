import { ApplicationRef, Component, ErrorHandler, Injectable, inject } from '@angular/core';
import { provideRouter } from '@angular/router';
import {
  NH_ASSISTANT_FETCH,
  NhAssistantAccessPolicy,
  NhAssistantFetch,
  NhAssistantLauncherComponent,
  NhAssistantLiveService,
  NhAssistantPanelComponent,
  NhAssistantStore,
  provideNhAssistant
} from '@newheap/platform-ai-chat';
import { NhAssistantAdminComponent } from '@newheap/platform-ai-chat/admin';
import { NhAssistantMockBackend, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';

const errors: unknown[] = [];
let fetchCount = 0;

@Injectable()
class CompatibilityAccessPolicy implements NhAssistantAccessPolicy {
  canUse(): boolean {
    return true;
  }
}

@Component({
  selector: 'sample-assistant-compatibility',
  imports: [NhAssistantLauncherComponent, NhAssistantPanelComponent, NhAssistantAdminComponent],
  template: '<nh-assistant-launcher /><nh-assistant-panel /><nh-assistant-admin />'
})
export class AssistantCompatibilityComponent {}

export const assistantCompatibilityProviders = [
  provideRouter([]),
  { provide: ErrorHandler, useValue: { handleError: (error: unknown) => errors.push(error) } },
  provideNhAssistant({
    apiBaseUrl: '/api/assistant',
    getAccessToken: () => null,
    accessPolicy: CompatibilityAccessPolicy,
    liveUpdates: false
  }),
  provideNhAssistantMockApi({
    agents: [{ id: 'projects', version: 1, displayNameKey: 'Project assistant', descriptionKey: 'Projects', canMutate: true }],
    turns: [],
    admin: { context: 'Compatibility sample', agents: [], tools: [], mcpServers: [] }
  }),
  // Exercise the real service without requiring a SignalR server for this package smoke check.
  NhAssistantLiveService,
  {
    provide: NH_ASSISTANT_FETCH,
    useFactory: (): NhAssistantFetch => {
      const backend = inject(NhAssistantMockBackend);
      return (input, init) => {
        fetchCount++;
        return backend.fetch(input, init);
      };
    }
  }
];

/** The release matrix runs this sample against packed packages, without source path mappings. */
export async function verifyAssistantCompatibility(app: ApplicationRef): Promise<void> {
  window.addEventListener('unhandledrejection', event => errors.push(event.reason));
  window.addEventListener('error', event => errors.push(event.error));
  await app.injector.get(NhAssistantStore).initialize();
  const live = app.injector.get(NhAssistantLiveService);
  await live.connect(null);
  if (live.state() !== 'off' || fetchCount === 0) {
    throw new Error('The live service or fetch override was not initialized.');
  }

  const deadline = Date.now() + 20000;
  async function waitFor(selector: string): Promise<HTMLElement> {
    while (Date.now() < deadline) {
      if (errors.length > 0) {
        throw new Error(errors.map(String).join('\n'));
      }
      const element = document.querySelector<HTMLElement>(selector);
      if (element) {
        return element;
      }
      await new Promise(resolve => setTimeout(resolve, 25));
    }
    throw new Error(`Assistant did not render ${selector}.`);
  }

  const launcher = await waitFor('nh-assistant-launcher button');
  launcher.click();
  await waitFor('[role="dialog"] nh-assistant-composer');
  await waitFor('nh-assistant-admin [role="tablist"]');
  await app.whenStable();
  if (errors.length > 0) {
    throw new Error(errors.map(String).join('\n'));
  }
  document.body.dataset['compatibility'] = 'passed';
}
