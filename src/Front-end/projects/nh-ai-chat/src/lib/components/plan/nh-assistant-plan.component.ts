import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { AgentPlan } from '../../models/assistant-api.models';
import { NhAssistantCardPlaceholderComponent } from '../../internal/nh-assistant-card-placeholder.component';
import { NhAssistantPlanViewComponent } from './nh-assistant-plan-view.component';

/**
 * The plan an agent keeps of its work (`update_plan`, `run.KeepPlan()` on the server): a compact
 * checklist with how many steps are done, the step in progress marked as the current one
 * (`aria-current="step"`) and completed steps checked and struck through. The plan is the agent's own
 * statement, model output: every step renders as plain text, never as markup or a link, under a label
 * that says it is the agent's. The step in progress pulses only when the person allows motion.
 *
 * Plans are rarely on screen, so the checklist loads on first use, in a chunk of its own. Until it has
 * arrived, and on the server, an empty box of about its height holds its place.
 */
@Component({
  selector: 'nh-assistant-plan',
  standalone: true,
  imports: [NhAssistantCardPlaceholderComponent, NhAssistantPlanViewComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @defer (on immediate) {
      <nh-assistant-plan-view [plan]="plan()" />
    } @placeholder {
      <nh-assistant-card-placeholder [height]="placeholderHeight()" />
    }
  `,
  styles: `
    :host {
      display: block;
    }
  `,
  host: {
    '[attr.data-finished]': 'finished() ? "true" : null'
  }
})
export class NhAssistantPlanComponent {
  /** The plan to show, as a plan part or a run's result carries it. */
  readonly plan = input.required<AgentPlan>();

  readonly total = computed(() => this.plan().steps.length);
  readonly completed = computed(() => this.plan().steps.filter(step => step.status === 'completed').length);
  readonly finished = computed(() => this.total() > 0 && this.completed() === this.total());
  /** The checklist's height with one line per step: its frame and header, then 30 pixels a step. */
  readonly placeholderHeight = computed(() => 46 + 30 * this.total());
}
