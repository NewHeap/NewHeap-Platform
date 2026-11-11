import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { AgentPlan, PlanStepStatus } from '../../models/assistant-api.models';
import { NhAssistantIconComponent } from '../../internal/nh-assistant-icon.component';

let nextId = 0;

/**
 * The checklist of `nh-assistant-plan` (internal), which loads it on first use: how many steps are
 * done, the step in progress marked as the current one (`aria-current="step"`) and completed steps
 * checked and struck through. Every step renders as plain text, never as markup or a link.
 */
@Component({
  selector: 'nh-assistant-plan-view',
  standalone: true,
  imports: [TranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-plan-view.component.html',
  styleUrl: './nh-assistant-plan-view.component.scss',
  host: {
    '[attr.data-finished]': 'finished() ? "true" : null'
  }
})
export class NhAssistantPlanViewComponent {
  readonly plan = input.required<AgentPlan>();

  readonly titleId = `nh-assistant-plan-${nextId++}`;
  readonly total = computed(() => this.plan().steps.length);
  readonly completed = computed(() => this.plan().steps.filter(step => step.status === 'completed').length);
  readonly finished = computed(() => this.total() > 0 && this.completed() === this.total());

  statusKey(status: PlanStepStatus): string {
    return `nh-assistant.plan.status.${status}`;
  }
}
