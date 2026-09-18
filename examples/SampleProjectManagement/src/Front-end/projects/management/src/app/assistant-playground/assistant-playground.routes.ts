import { Routes } from '@angular/router';
import { provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { AssistantPlaygroundAccess, AssistantPlaygroundAccessPolicy } from './assistant-playground-access';
import {
  ASSISTANT_PLAYGROUND_ADMIN_ROUTE,
  ASSISTANT_PLAYGROUND_SCENARIO,
  PROJECT_ASSISTANT_ID
} from './assistant-playground.scenario';
import { provideSampleAssistant } from './sample-assistant.config';

/**
 * The playground hosts its own assistant scope on the scripted mock API, so it works
 * without the assistant back-end. The root scope in `app.config.ts` uses the real API.
 */
export const ASSISTANT_PLAYGROUND_ROUTES: Routes = [
  {
    path: '',
    providers: [
      AssistantPlaygroundAccess,
      provideSampleAssistant({
        accessPolicy: AssistantPlaygroundAccessPolicy,
        defaultAgentId: PROJECT_ASSISTANT_ID,
        adminRoute: ASSISTANT_PLAYGROUND_ADMIN_ROUTE
      }),
      provideNhAssistantMockApi(ASSISTANT_PLAYGROUND_SCENARIO)
    ],
    children: [
      {
        path: '',
        title: 'Assistant',
        loadComponent: () => import('./assistant-playground.component')
          .then(module => module.AssistantPlaygroundComponent)
      },
      {
        // The same scope as the playground, so administration changes show up in its panel.
        path: 'admin',
        title: 'Assistant administration',
        loadComponent: () => import('./assistant-admin-page.component')
          .then(module => module.AssistantAdminPageComponent)
      }
    ]
  }
];
