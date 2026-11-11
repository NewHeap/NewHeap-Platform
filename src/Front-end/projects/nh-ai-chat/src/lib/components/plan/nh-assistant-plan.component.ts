import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { AgentPlan, PlanStepStatus } from '../../models/assistant-api.models';
import { NhAssistantIconComponent } from '../../internal/nh-assistant-icon.component';

let nextId = 0;

/**
 * The plan an agent keeps of its work (`update_plan`, `run.KeepPlan()` on the server): a compact
 * checklist with how many steps are done, the step in progress marked as the current one
 * (`aria-current="step"`) and completed steps checked and struck through. The plan is the agent's own
 * statement, model output: every step renders as plain text, never as markup or a link, under a label
 * that says it is the agent's. The step in progress pulses only when the person allows motion.
 */
@Component({
  selector: 'nh-assistant-plan',
  standalone: true,
  imports: [TranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-plan.component.html',
  styleUrl: './nh-assistant-plan.component.scss',
  host: {
    '[attr.data-finished]': 'finished() ? "true" : null'
  }
})
export class NhAssistantPlanComponent {
  /** The plan to show, as a plan part or a run's result carries it. */
  readonly plan = input.required<AgentPlan>();

  readonly titleId = `nh-assistant-plan-${nextId++}`;
  readonly total = computed(() => this.plan().steps.length);
  readonly completed = computed(() => this.plan().steps.filter(step => step.status === 'completed').length);
  readonly finished = computed(() => this.total() > 0 && this.completed() === this.total());

  statusKey(status: PlanStepStatus): string {
    return `nh-assistant.plan.status.${status}`;
  }
}
