import { Injectable } from '@angular/core';
import { NhBaseApiService } from '@newheap/platform-common';
import { Observable } from 'rxjs';
import { AssistantCompositionSettingMutateModel, AssistantCompositionSettingViewModel } from './assistant-composition-setting.models';

/**
 * SPM-371: the portal's own setting whether the assistant's agents use compositions and the script
 * tool. The assistant asks it at the start of every turn, so a change applies from the next one.
 */
@Injectable({ providedIn: 'root' })
export class AssistantCompositionSettingApiService extends NhBaseApiService {
  constructor() {
    super('assistant-composition-settings');
  }

  /** The stored settings, ordered by agent; an agent without one uses neither feature. */
  list(): Observable<AssistantCompositionSettingViewModel[]> {
    return this.apiService.get<AssistantCompositionSettingViewModel[]>(this.baseUrl);
  }

  /** Stores the setting of one agent, or of the MCP exposure (`mcp`). */
  save(agentId: string, model: AssistantCompositionSettingMutateModel): Observable<AssistantCompositionSettingViewModel> {
    return this.update<AssistantCompositionSettingViewModel>(encodeURIComponent(agentId), model);
  }
}
