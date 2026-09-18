import { Routes } from '@angular/router';
import { provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { AssistantPlaygroundAccess, AssistantPlaygroundAccessPolicy } from './assistant-playground-access';
import { ASSISTANT_PLAYGROUND_SCENARIO, PROJECT_ASSISTANT_ID } from './assistant-playground.scenario';
import { provideSampleAssistant } from './sample-assistant.config';

/**
 * The playground hosts its own assistant scope on the scripted mock API, so it works
 * without the assistant back-end. The root scope in `app.config.ts` uses the real API.
 */
export const ASSISTANT_PLAYGROUND_ROUTES: Routes = [
  {
    path: '',
    title: 'Assistant',
    providers: [
      AssistantPlaygroundAccess,
      provideSampleAssistant({
        accessPolicy: AssistantPlaygroundAccessPolicy,
        defaultAgentId: PROJECT_ASSISTANT_ID
      }),
      provideNhAssistantMockApi(ASSISTANT_PLAYGROUND_SCENARIO)
    ],
    loadComponent: () => import('./assistant-playground.component')
      .then(module => module.AssistantPlaygroundComponent)
  }
];
