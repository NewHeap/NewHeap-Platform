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
  NhAssistantError,
  WithoutApprovalMode,
  WithoutApprovalSwitch,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';
import { NhAssistantAccountNames } from '../../nh-assistant-account-names';
import { maxStateReason } from '../agent-operations';
import { startNhAssistantAdminTranslations } from '../../i18n/nh-assistant-admin-translations';

/** A switch being confirmed: the tool and the mode it goes to. */
interface WithoutApprovalChange {
  toolId: string;
  mode: WithoutApprovalMode;
}

let nextId = 0;

/**
 * The kinds of action an agent may run without a person's approval (FB82): the agent's tools whose code
 * allows it, each with its switch. A switch goes to shadow first, where proposals still wait for people
 * and the platform records which ones would have run on their own, and only from shadow to live; it can
 * go back to shadow or off at any time, which applies to the agent's next call. Each change explains what
 * it does and takes an optional note before it applies.
 */
@Component({
  selector: 'nh-assistant-agent-without-approval',
  standalone: true,
  imports: [DatePipe, TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-agent-without-approval.component.html',
  styleUrls: ['../../nh-assistant-admin-shared.scss', './agent-page.scss']
})
export class NhAssistantAgentWithoutApprovalComponent implements OnInit {
  private readonly admin = inject(NhAssistantAdminApiService);
  private loadSubscription?: Subscription;
  private changeSubscription?: Subscription;

  /** The agent whose tools these are. */
  readonly agentId = input.required<string>();
  /** The names of the people who switched, once known. */
  readonly names = input.required<NhAssistantAccountNames>();
  /** Another action of the page is busy. */
  readonly disabled = input(false);

  readonly id = `nh-assistant-without-approval-${nextId++}`;
  readonly maxReason = maxStateReason;
  readonly loading = signal(true);
  readonly loadFailed = signal(false);
  readonly switches = signal<AgentWithoutApproval | null>(null);
  readonly change = signal<WithoutApprovalChange | null>(null);
  readonly reason = signal('');
  readonly busy = signal(false);
  readonly error = signal<NhAssistantError | null>(null);
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));
  readonly tools = computed(() => this.switches()?.tools ?? []);

  constructor() {
    startNhAssistantAdminTranslations();
    inject(DestroyRef).onDestroy(() => {
      this.loadSubscription?.unsubscribe();
      this.changeSubscription?.unsubscribe();
    });
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

  /** The modes a switch may go to from where it is: shadow first, live only from shadow, off always. */
  modesFrom(tool: WithoutApprovalSwitch): WithoutApprovalMode[] {
    switch (tool.mode) {
      case 'live':
        return ['shadow', 'off'];
      case 'shadow':
        return ['live', 'off'];
      default:
        return ['shadow'];
    }
  }

  tone(mode: WithoutApprovalMode): string {
    switch (mode) {
      case 'live':
        return 'warning';
      case 'shadow':
        return 'info';
      default:
        return '';
    }
  }

  ask(tool: WithoutApprovalSwitch, mode: WithoutApprovalMode): void {
    this.error.set(null);
    this.reason.set('');
    this.change.set({ toolId: tool.toolId, mode });
  }

  cancel(): void {
    this.change.set(null);
    this.error.set(null);
  }

  apply(tool: WithoutApprovalSwitch): void {
    const current = this.change();
    if (!current || current.toolId !== tool.toolId) {
      return;
    }

    this.busy.set(true);
    this.error.set(null);
    this.changeSubscription?.unsubscribe();
    const reason = this.reason().trim();
    this.changeSubscription = this.admin
      .setAgentWithoutApproval(this.agentId(), tool.toolId, {
        mode: current.mode,
        expectedVersion: tool.version,
        reason: reason.length > 0 ? reason : null
      })
      .subscribe({
        next: changed => {
          this.busy.set(false);
          this.change.set(null);
          const switches = this.switches();
          if (switches) {
            this.switches.set({
              ...switches,
              tools: switches.tools.map(item => item.toolId === changed.toolId ? changed : item)
            });
          }
        },
        error: (error: unknown) => {
          this.busy.set(false);
          this.error.set(toNhAssistantError(error));
        }
      });
  }

  accountName(actorId: string | null | undefined): string {
    return this.names().nameOf(actorId);
  }

  accountTitle(actorId: string | null | undefined): string | null {
    return this.names().idTitleOf(actorId);
  }

  value(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }
}
