import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { AgentSummary } from '../../models/assistant-api.models';
import { NhAssistantTranslatePipe } from '../../internal/nh-assistant-translate.pipe';

let nextId = 0;

/**
 * Chooses the agent for new conversations. Agent names come from the host's translation
 * keys; agents created by an administrator carry literal text, which is shown as is.
 */
@Component({
  selector: 'nh-assistant-agent-picker',
  standalone: true,
  imports: [TranslatePipe, NhAssistantTranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <label class="visually-hidden" [for]="selectId">{{ 'nh-assistant.agent-picker.label' | translate }}</label>
    <select
      [id]="selectId"
      [disabled]="disabled() || agents().length < 2"
      [attr.title]="selected() ? ([selected()!.descriptionKey] | nhAssistantTranslate: selected()!.descriptionKey) : null"
      (change)="onChange($event)">
      @for (agent of agents(); track agent.id) {
        <option [value]="agent.id" [selected]="agent.id === selectedAgentId()">
          {{ [agent.displayNameKey] | nhAssistantTranslate: agent.displayNameKey }}
        </option>
      }
    </select>
    @if (selected(); as agent) {
      <span class="capability" [attr.data-can-mutate]="agent.canMutate">
        {{ (agent.canMutate ? 'nh-assistant.agent-picker.can-mutate' : 'nh-assistant.agent-picker.read-only') | translate }}
      </span>
    }
  `,
  styleUrl: './nh-assistant-agent-picker.component.scss'
})
export class NhAssistantAgentPickerComponent {
  readonly agents = input.required<readonly AgentSummary[]>();
  readonly selectedAgentId = input<string | null>(null);
  readonly disabled = input(false);
  readonly agentChange = output<string>();

  readonly selectId = `nh-assistant-agent-picker-${nextId++}`;
  readonly selected = computed(() => this.agents().find(agent => agent.id === this.selectedAgentId()) ?? null);

  onChange(event: Event): void {
    this.agentChange.emit((event.target as HTMLSelectElement).value);
  }
}
