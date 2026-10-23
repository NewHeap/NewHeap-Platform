import { EnvironmentProviders } from '@angular/core';
import { provideNhAssistantAgentMetadataEditors } from '@newheap/platform-ai-chat/admin';
import { AssistantCostCenterEditorComponent } from './assistant-cost-center-editor.component';

/** SPM-313: the name of the API's cost-center entry of agent metadata. */
export const SAMPLE_COST_CENTER_ENTRY = 'cost-center';

/**
 * SPM-313: the portal's editors of the application's fields of agents, for the routes of the
 * assistant administration, against the real API and in the playground's mock: the cost-center entry
 * shows the portal's own editor in its slot of the agent editor.
 */
export function provideSampleAgentMetadataEditors(): EnvironmentProviders {
  return provideNhAssistantAgentMetadataEditors({
    name: SAMPLE_COST_CENTER_ENTRY,
    component: AssistantCostCenterEditorComponent,
    label: 'project.assistant-cost-center-title'
  });
}
