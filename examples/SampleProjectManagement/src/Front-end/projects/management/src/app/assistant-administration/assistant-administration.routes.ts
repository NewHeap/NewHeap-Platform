import { Routes } from '@angular/router';
import { AssistantAdministrationPageComponent } from './assistant-administration-page.component';
import { provideSampleAgentMetadataEditors } from './sample-agent-metadata-editors';

/**
 * The real assistant administration, loaded with its route. SPM-313: the portal's editor of the cost
 * center of agents shows in the slot of the `cost-center` entry of the agent editor.
 */
export const ASSISTANT_ADMINISTRATION_ROUTES: Routes = [
  {
    path: '',
    providers: [provideSampleAgentMetadataEditors()],
    component: AssistantAdministrationPageComponent
  }
];
