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
  AgentAlertModel,
  AlertModelMode,
  AlertModelSwitch,
  NhAssistantAdminApiService,
  NhAssistantError,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantTokensDirective as NhAssistantTokensDirective,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';
import { NhAssistantAccountNames } from '../../nh-assistant-account-names';
import { maxStateReason } from '../agent-operations';
import { startNhAssistantAdminTranslations } from '../../i18n/nh-assistant-admin-translations';

let nextId = 0;

/**
 * The alert kinds the agent's language model may raise (FB87-9), on the agent's Rollout: each kind the
 * agent lists whose code lets a model raise it, with when it applies, the parameters the model fills and
 * its switch. A switch goes to shadow first, where the model's alerts reach nobody, and only from shadow
 * to people; it goes back to shadow or off at any time. Each change explains what it does and takes an
 * optional note before it applies, as running without approval does (FB82). An agent that lists no such
 * kind shows no section.
 */
@Component({
  selector: 'nh-assistant-agent-alert-model',
  standalone: true,
  hostDirectives: [NhAssistantTokensDirective],
  imports: [DatePipe, TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-agent-alert-model.component.html',
  styleUrls: ['../../nh-assistant-admin-shared.scss', './agent-page.scss']
})
export class NhAssistantAgentAlertModelComponent implements OnInit {
  private readonly admin = inject(NhAssistantAdminApiService);
  private loadSubscription?: Subscription;
  private changeSubscription?: Subscription;

  /** The agent whose kinds these are. */
  readonly agentId = input.required<string>();
  /** The names of the people who switched, once known. */
  readonly names = input.required<NhAssistantAccountNames>();
  /** Another action of the page is busy. */
  readonly disabled = input(false);

  readonly id = `nh-assistant-alert-model-${nextId++}`;
  readonly maxReason = maxStateReason;
  readonly loading = signal(true);
  readonly loadFailed = signal(false);
  readonly model = signal<AgentAlertModel | null>(null);
  readonly kinds = computed(() => this.model()?.kinds ?? []);
  /** The kind and mode whose change waits for its confirmation. */
  readonly change = signal<{ kindId: string; mode: AlertModelMode } | null>(null);
  readonly reason = signal('');
  readonly busy = signal(false);
  readonly error = signal<NhAssistantError | null>(null);
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));

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
    this.loadSubscription = this.admin.getAgentAlertModel(this.agentId()).subscribe({
      next: model => {
        this.model.set(model);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.loadFailed.set(true);
      }
    });
  }

  /** The modes a switch may go to from where it is: shadow first, live only from shadow, off always. */
  modesOf(kind: AlertModelSwitch): AlertModelMode[] {
    switch (kind.mode) {
      case 'live':
        return ['shadow', 'off'];
      case 'shadow':
        return ['live', 'off'];
      default:
        return ['shadow'];
    }
  }

  toneOf(kind: AlertModelSwitch): string {
    switch (kind.mode) {
      case 'live':
        return 'warning';
      case 'shadow':
        return 'info';
      default:
        return '';
    }
  }

  ask(kind: AlertModelSwitch, mode: AlertModelMode): void {
    this.error.set(null);
    this.reason.set('');
    this.change.set({ kindId: kind.kindId, mode });
  }

  cancel(): void {
    this.change.set(null);
    this.error.set(null);
  }

  apply(kind: AlertModelSwitch): void {
    const change = this.change();
    if (!change || change.kindId !== kind.kindId) {
      return;
    }

    this.busy.set(true);
    this.error.set(null);
    this.changeSubscription?.unsubscribe();
    const reason = this.reason().trim();
    this.changeSubscription = this.admin
      .setAgentAlertModel(this.agentId(), kind.kindId, {
        mode: change.mode,
        expectedVersion: kind.version,
        reason: reason.length > 0 ? reason : null
      })
      .subscribe({
        next: changed => {
          this.busy.set(false);
          this.change.set(null);
          const model = this.model();
          if (model) {
            this.model.set({ ...model, kinds: model.kinds.map(item => item.kindId === changed.kindId ? changed : item) });
          }
        },
        error: (error: unknown) => {
          this.busy.set(false);
          this.error.set(toNhAssistantError(error));
        }
      });
  }

  value(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  accountName(actorId: string | null | undefined): string {
    return this.names().nameOf(actorId);
  }

  accountTitle(actorId: string | null | undefined): string | null {
    return this.names().idTitleOf(actorId);
  }
}
