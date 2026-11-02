import { DatePipe } from '@angular/common';
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
  WithoutApprovalSwitch,
  ɵNhAssistantIconComponent as NhAssistantIconComponent
} from '@newheap/platform-ai-chat';
import { NhAssistantAccountNames } from '../../nh-assistant-account-names';
import { startNhAssistantAdminTranslations } from '../../i18n/nh-assistant-admin-translations';
import { NhAssistantWithoutApprovalSwitchComponent } from './nh-assistant-without-approval-switch.component';

let nextId = 0;

/**
 * The kinds of action an agent may run without a person's approval (FB82), on the agent's Rollout: the
 * agent's tools whose code allows it, each with its switch (`nh-assistant-without-approval-switch`), who
 * switched it last and why. The view per kind of action offers the same switch next to the numbers.
 */
@Component({
  selector: 'nh-assistant-agent-without-approval',
  standalone: true,
  imports: [DatePipe, TranslatePipe, NhAssistantIconComponent, NhAssistantWithoutApprovalSwitchComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-agent-without-approval.component.html',
  styleUrls: ['../../nh-assistant-admin-shared.scss', './agent-page.scss']
})
export class NhAssistantAgentWithoutApprovalComponent implements OnInit {
  private readonly admin = inject(NhAssistantAdminApiService);
  private loadSubscription?: Subscription;

  /** The agent whose tools these are. */
  readonly agentId = input.required<string>();
  /** The names of the people who switched, once known. */
  readonly names = input.required<NhAssistantAccountNames>();
  /** Another action of the page is busy. */
  readonly disabled = input(false);

  readonly id = `nh-assistant-without-approval-${nextId++}`;
  readonly loading = signal(true);
  readonly loadFailed = signal(false);
  readonly switches = signal<AgentWithoutApproval | null>(null);
  readonly tools = computed(() => this.switches()?.tools ?? []);

  constructor() {
    startNhAssistantAdminTranslations();
    inject(DestroyRef).onDestroy(() => this.loadSubscription?.unsubscribe());
  }

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.loadFailed.set(false);
    this.loadSubscription?.unsubscribe();
    this.loadSubscription = this.admin.getAgentWithoutApproval(this.agentId()).subscribe({
      next: switches => {
        this.switches.set(switches);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.loadFailed.set(true);
      }
    });
  }

  /** A switch changed: the list shows the tool as the server answered it. */
  onChanged(changed: WithoutApprovalSwitch): void {
    const switches = this.switches();
    if (switches) {
      this.switches.set({
        ...switches,
        tools: switches.tools.map(item => item.toolId === changed.toolId ? changed : item)
      });
    }
  }

  accountName(actorId: string | null | undefined): string {
    return this.names().nameOf(actorId);
  }

  accountTitle(actorId: string | null | undefined): string | null {
    return this.names().idTitleOf(actorId);
  }
}
