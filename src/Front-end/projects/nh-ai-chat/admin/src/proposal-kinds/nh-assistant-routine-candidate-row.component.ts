import { DatePipe, PercentPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import {
  ɵNhAssistantTokensDirective as NhAssistantTokensDirective,
  RoutineCandidate,
  RoutineCandidateThresholds
} from '@newheap/platform-ai-chat';
import { agentDisplayName } from '../agents/agent-texts';
import { startNhAssistantAdminTranslations } from '../i18n/nh-assistant-admin-translations';
import { nhAssistantDurationText } from './nh-assistant-decision-time';

/**
 * One agent's row in the view per kind of action: the agent, its decisions of the kind in the
 * period, the rejection rate, how long people took to decide and whether the numbers make the kind a
 * candidate for running without approval, or which thresholds they miss. It is a table row
 * (`<tr nhAssistantRoutineCandidateRow>`) inside `nh-assistant-admin-proposal-kinds`. With
 * `withActions` it renders a last cell with the content the view projects into it, such as a control
 * that acts on the candidate; the row itself only shows numbers.
 */
@Component({
  selector: 'tr[nhAssistantRoutineCandidateRow]',
  standalone: true,
  hostDirectives: [NhAssistantTokensDirective],
  imports: [DatePipe, PercentPipe, TranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'agent-row',
    '[attr.data-agent-id]': 'candidate().agentId',
    '[attr.data-tool-id]': 'candidate().toolId',
    '[attr.data-candidate]': 'candidate().candidate'
  },
  template: `
    <th scope="row" [attr.data-label]="'nh-assistant.admin.proposal-kinds.columns.agent' | translate">
      <span class="agent">
        <span>{{ name() }}</span>
        @if (name() !== candidate().agentId) {
          <span class="mono muted">{{ candidate().agentId }}</span>
        }
      </span>
    </th>
    <td class="number" [attr.data-label]="'nh-assistant.admin.proposal-kinds.columns.decisions' | translate">{{ candidate().numbers.decisions }}</td>
    <td class="number" [attr.data-label]="'nh-assistant.admin.proposal-kinds.columns.approved' | translate">{{ candidate().numbers.approved }}</td>
    <td class="number" [attr.data-label]="'nh-assistant.admin.proposal-kinds.columns.rejected' | translate">{{ candidate().numbers.rejected }}</td>
    <td class="number" [attr.data-label]="'nh-assistant.admin.proposal-kinds.columns.rejection-rate' | translate">
      @if (candidate().numbers.rejectionRate === null) {
        <span class="muted">{{ 'nh-assistant.admin.proposal-kinds.no-decisions' | translate }}</span>
      } @else {
        {{ candidate().numbers.rejectionRate | percent: '1.0-1' }}
      }
    </td>
    <td class="number" data-median [attr.data-label]="'nh-assistant.admin.proposal-kinds.columns.median' | translate">
      @if (median(); as text) {
        {{ text.key | translate: text.params }}
      } @else {
        <span class="muted">{{ 'nh-assistant.admin.proposal-kinds.no-decisions' | translate }}</span>
      }
    </td>
    <td class="number" data-percentile-90 [attr.data-label]="'nh-assistant.admin.proposal-kinds.columns.percentile-90' | translate">
      @if (percentile90(); as text) {
        {{ text.key | translate: text.params }}
      } @else {
        <span class="muted">{{ 'nh-assistant.admin.proposal-kinds.no-decisions' | translate }}</span>
      }
    </td>
    <td data-status [attr.data-label]="'nh-assistant.admin.proposal-kinds.columns.candidate' | translate">
      @if (candidate().candidate) {
        <span class="badge success" [attr.title]="'nh-assistant.admin.proposal-kinds.candidate-hint' | translate">
          {{ 'nh-assistant.admin.proposal-kinds.candidate' | translate }}
        </span>
      } @else {
        <ul class="badges" [attr.aria-label]="'nh-assistant.admin.proposal-kinds.unmet-label' | translate">
          @for (reason of candidate().unmetThresholds; track reason) {
            <li>
              <span class="badge" [attr.data-reason]="reason" [attr.title]="reasonHint(reason)">
                {{ 'nh-assistant.admin.proposal-kinds.reasons.' + reason | translate }}
              </span>
            </li>
          }
        </ul>
        @if (candidate().numbers.lastRejectedAt; as lastRejectedAt) {
          <span class="muted last-rejected">{{ 'nh-assistant.admin.proposal-kinds.last-rejected' | translate: { date: (lastRejectedAt | date: 'mediumDate') } }}</span>
        }
      }
    </td>
    @if (withActions()) {
      <td class="actions" [attr.data-label]="'nh-assistant.admin.proposal-kinds.columns.actions' | translate">
        <ng-content />
      </td>
    }
  `,
  styleUrls: ['../nh-assistant-admin-shared.scss', './nh-assistant-routine-candidate-row.component.scss']
})
export class NhAssistantRoutineCandidateRowComponent {
  private readonly translate = inject(TranslateService);

  /** The agent's numbers of the kind, with whether they make it a candidate. */
  readonly candidate = input.required<RoutineCandidate>();
  /** The thresholds the server applied, for the explanation of a threshold the numbers miss. */
  readonly thresholds = input.required<RoutineCandidateThresholds>();
  /** Renders a last cell with the projected content, for an action on the candidate. */
  readonly withActions = input(false);

  readonly name = computed(() => {
    const candidate = this.candidate();
    if (!candidate.agentDisplayName || !candidate.agentSource) {
      return candidate.agentId;
    }

    return agentDisplayName(this.translate, { source: candidate.agentSource, displayName: candidate.agentDisplayName });
  });
  readonly median = computed(() => {
    const time = this.candidate().numbers.decisionTime;
    return time ? nhAssistantDurationText(time.medianSeconds) : null;
  });
  readonly percentile90 = computed(() => {
    const time = this.candidate().numbers.decisionTime;
    return time ? nhAssistantDurationText(time.percentile90Seconds) : null;
  });

  constructor() {
    startNhAssistantAdminTranslations();
  }

  /** What a missed threshold asks, with the threshold the server applied. */
  reasonHint(reason: string): string {
    const thresholds = this.thresholds();
    return this.translate.instant(`nh-assistant.admin.proposal-kinds.reason-hints.${reason}`, {
      count: thresholds.minimumDecisions,
      rate: Math.round(thresholds.minimumApprovalRate * 1000) / 10,
      days: thresholds.quietPeriodDays,
      maximum: thresholds.maximumStaleOrFailedAfterApproval
    }) as string;
  }
}
