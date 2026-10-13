import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { NhAssistantIconComponent } from '../../internal/nh-assistant-icon.component';
import { AgentSubjectSummary } from '../../models/assistant-agent-run.models';
import { NhAssistantSubjectSummaryService } from '../../services/nh-assistant-subject-summary.service';

/**
 * A compact count of the agent work on one subject, for example on each row of a project list.
 * It shows nothing while the subject has no work. Otherwise it shows the number of proposals the
 * viewer may decide in the accent color, or, when they may decide none, a neutral count of the
 * open proposals, and a small working indicator while an agent works on the subject. Its tooltip
 * and accessible name say both counts. Badges on one page share one summary request per tick and
 * refresh on live agent-work updates of their subject; see `NhAssistantSubjectSummaryService`.
 */
@Component({
  selector: 'nh-assistant-subject-badge',
  standalone: true,
  imports: [TranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (work(); as summary) {
      @let proposals = proposalsKey() ? (proposalsKey()! | translate: { toDecide: summary.proposalsToDecide, open: summary.openProposals }) : '';
      @let working = summary.runActive ? ('nh-assistant.subject-badge.working' | translate) : '';
      @let label = proposals && working ? proposals + ' ' + working : proposals || working;
      <span
        class="subject-badge"
        [attr.data-state]="summary.proposalsToDecide > 0 ? 'decide' : summary.openProposals > 0 ? 'open' : 'working'"
        [attr.title]="label">
        <nh-assistant-icon name="assistant" />
        @if (count() > 0) {
          <span class="count" aria-hidden="true">{{ countLabel() }}</span>
        }
        @if (summary.runActive) {
          <span class="working" aria-hidden="true"></span>
        }
        <span class="visually-hidden">{{ label }}</span>
      </span>
    }
  `,
  styleUrl: './nh-assistant-subject-badge.component.scss'
})
export class NhAssistantSubjectBadgeComponent {
  private readonly summaries = inject(NhAssistantSubjectSummaryService);
  private readonly summary = signal<AgentSubjectSummary | null>(null);

  /** The application's kind of subject, for example `project`. */
  readonly subjectType = input.required<string>();
  readonly subjectId = input.required<string>();

  /** The summary while the subject has work to show; `null` otherwise. */
  readonly work = computed(() => {
    const summary = this.summary();
    return summary && (summary.openProposals > 0 || summary.runActive) ? summary : null;
  });
  readonly count = computed(() => {
    const summary = this.work();
    if (!summary) {
      return 0;
    }
    return summary.proposalsToDecide > 0 ? summary.proposalsToDecide : summary.openProposals;
  });
  readonly countLabel = computed(() => this.count() > 99 ? '99+' : String(this.count()));
  readonly proposalsKey = computed(() => {
    const summary = this.work();
    if (!summary || summary.openProposals === 0) {
      return null;
    }
    return summary.proposalsToDecide > 0 ? 'nh-assistant.subject-badge.to-decide' : 'nh-assistant.subject-badge.open';
  });

  constructor() {
    effect(onCleanup => {
      const subjectType = this.subjectType();
      const subjectId = this.subjectId();
      const subscription = untracked(() =>
        this.summaries.summary(subjectType, subjectId).subscribe(summary => this.summary.set(summary)));
      onCleanup(() => subscription.unsubscribe());
    });
  }
}
