import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  computed,
  inject,
  input,
  signal
} from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import {
  AgentWithoutApproval,
  NhAssistantAdminApiService,
  ɵNhAssistantTokensDirective as NhAssistantTokensDirective,
  RoutineCandidate,
  WithoutApprovalSwitch
} from '@newheap/platform-ai-chat';
import { startNhAssistantAdminTranslations } from '../../i18n/nh-assistant-admin-translations';
import { NhAssistantWithoutApprovalSwitchComponent } from './nh-assistant-without-approval-switch.component';

/**
 * The switch that lets the candidate's agent run its kind of action without approval (FB82), as the
 * view per kind of action shows it in an agent's row: the agent's switch for the tool, next to the
 * numbers that do or do not make it a candidate. Only a tool whose code allows it
 * (`AllowWithoutApproval`) shows a switch, and only for an agent that does work of its own; any other row
 * says that its kind always asks for approval. Pass it as the view's `candidateActions`, as the
 * administration page does:
 * `<ng-template #actions let-candidate><nh-assistant-without-approval-action [candidate]="candidate" /></ng-template>`.
 */
@Component({
  selector: 'nh-assistant-without-approval-action',
  standalone: true,
  hostDirectives: [NhAssistantTokensDirective],
  imports: [TranslatePipe, NhAssistantWithoutApprovalSwitchComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '[attr.data-without-approval-action-for]': 'candidate().agentId + "/" + candidate().toolId'
  },
  template: `
    @if (tool(); as current) {
      <nh-assistant-without-approval-switch
        [agentId]="candidate().agentId"
        [tool]="current"
        [applies]="switches()?.applies === true"
        [candidate]="candidate()"
        (changed)="onChanged($event)" />
    } @else if (switches() || failed()) {
      <span class="muted" data-without-approval-unavailable>{{ 'nh-assistant.admin.agent-page.rollout.without-approval.unavailable' | translate }}</span>
    }
  `,
  styleUrls: ['../../nh-assistant-admin-shared.scss']
})
export class NhAssistantWithoutApprovalActionComponent implements OnInit {
  private readonly admin = inject(NhAssistantAdminApiService);
  private subscription?: Subscription;

  /** The agent's numbers of the kind, from the view per kind of action. */
  readonly candidate = input.required<RoutineCandidate>();

  readonly switches = signal<AgentWithoutApproval | null>(null);
  readonly failed = signal(false);
  readonly tool = computed(() => this.switches()?.tools.find(item => item.toolId === this.candidate().toolId) ?? null);

  constructor() {
    startNhAssistantAdminTranslations();
    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());
  }

  ngOnInit(): void {
    this.subscription = this.admin.getAgentWithoutApproval(this.candidate().agentId).subscribe({
      next: switches => this.switches.set(switches),
      // An agent that no longer exists, or that the server cannot read, offers no switch.
      error: () => this.failed.set(true)
    });
  }

  onChanged(changed: WithoutApprovalSwitch): void {
    const switches = this.switches();
    if (switches) {
      this.switches.set({ ...switches, tools: switches.tools.map(item => item.toolId === changed.toolId ? changed : item) });
    }
  }
}
