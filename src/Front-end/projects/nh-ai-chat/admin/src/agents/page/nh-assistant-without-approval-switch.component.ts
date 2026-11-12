import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  input,
  output,
  signal
} from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import {
  NhAssistantAdminApiService,
  NhAssistantError,
  RoutineCandidate,
  WithoutApprovalMode,
  WithoutApprovalSwitch,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantTokensDirective as NhAssistantTokensDirective,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';
import { maxStateReason } from '../agent-operations';
import { readMayChangeAgentRights } from '../agent-rights';
import { NhAssistantAgentRightsHintComponent } from '../nh-assistant-agent-rights-hint.component';
import { startNhAssistantAdminTranslations } from '../../i18n/nh-assistant-admin-translations';

let nextId = 0;

/**
 * One switch that lets an agent run a tool without approval (FB82): its mode and the changes it allows
 * now. It goes to shadow first, and only from shadow live; it goes back to shadow or off at any time.
 * Each change explains what it does and takes an optional note before it applies. With `candidate`, the
 * numbers of the view per kind of action, going live from a kind the numbers do not mark as a candidate
 * says which thresholds they miss before it applies; the administrator decides. The switched tool comes
 * back through `changed`. Going live lets the tool run without a person, so it takes the agent rights policy;
 * shadow and off stay open to everybody here.
 */
@Component({
  selector: 'nh-assistant-without-approval-switch',
  standalone: true,
  hostDirectives: [NhAssistantTokensDirective],
  imports: [TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent, NhAssistantAgentRightsHintComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'without-approval-switch',
    '[attr.data-without-approval-switch]': 'tool().toolId',
    '[attr.data-without-approval-switch-mode]': 'tool().mode'
  },
  template: `
    <span class="switch-line">
      <span class="pill" [class]="tone()" data-without-approval-status>{{ 'nh-assistant.admin.agent-page.rollout.without-approval.mode.' + tool().mode | translate }}</span>
      @if (applies()) {
        @for (mode of modes(); track mode) {
          <button
            type="button"
            [class.secondary]="mode !== 'live'"
            [class.primary]="mode === 'live'"
            [attr.data-without-approval-action]="mode"
            [attr.aria-describedby]="rightsLocked(mode) ? id + '-rights' : null"
            [disabled]="disabled() || busy() || change() !== null || rightsLocked(mode)"
            (click)="ask(mode)">
            {{ 'nh-assistant.admin.agent-page.rollout.without-approval.action.' + mode | translate }}
          </button>
        }
      }
    </span>
    @if (applies() && rightsLocked('live') && modes().includes('live')) {
      <nh-assistant-agent-rights-hint [hintId]="id + '-rights'" />
    }
    @if (change(); as mode) {
      <form class="change-form" [attr.data-without-approval-form]="mode" (submit)="$event.preventDefault(); apply()">
        <p class="muted">{{ 'nh-assistant.admin.agent-page.rollout.without-approval.explain.' + mode | translate: { tool: tool().toolId } }}</p>
        @if (mode === 'live' && candidate(); as numbers) {
          @if (!numbers.candidate) {
            <p class="message warning" data-without-approval-not-candidate>
              <nh-assistant-icon name="warning" />
              <span>
                {{ 'nh-assistant.admin.agent-page.rollout.without-approval.not-candidate' | translate }}
                @for (reason of numbers.unmetThresholds; track reason) {
                  <span class="tag" [attr.data-reason]="reason">{{ 'nh-assistant.admin.proposal-kinds.reasons.' + reason | translate }}</span>
                }
              </span>
            </p>
          }
        }
        <div class="field">
          <label [for]="id + '-reason'">{{ 'nh-assistant.admin.agents.state.note-label' | translate }}</label>
          <input
            type="text"
            autocomplete="off"
            [id]="id + '-reason'"
            [attr.aria-describedby]="id + '-reason-hint'"
            [maxLength]="maxReason"
            [value]="reason()"
            (input)="reason.set(value($event))" />
          <p class="hint" [id]="id + '-reason-hint'">{{ 'nh-assistant.admin.agents.state.note-hint' | translate }}</p>
        </div>
        <div class="card-actions">
          <button type="submit" class="primary" data-without-approval-confirm [disabled]="busy()">
            {{ 'nh-assistant.admin.agent-page.rollout.without-approval.confirm.' + mode | translate }}
          </button>
          <button class="secondary" type="button" (click)="cancel()">{{ 'nh-assistant.admin.cancel' | translate }}</button>
        </div>
        @if (error(); as failure) {
          <p class="form-error" role="alert" data-without-approval-error><nh-assistant-icon name="warning" />{{ errorKeys() | nhAssistantTranslate: failure.code }}</p>
        }
      </form>
    }
  `,
  styleUrls: ['../../nh-assistant-admin-shared.scss', './agent-page.scss']
})
export class NhAssistantWithoutApprovalSwitchComponent {
  private readonly admin = inject(NhAssistantAdminApiService);
  private readonly mayChangeRights = readMayChangeAgentRights();
  private subscription?: Subscription;

  /** The agent the switch belongs to. */
  readonly agentId = input.required<string>();
  /** The tool with the switch in effect. */
  readonly tool = input.required<WithoutApprovalSwitch>();
  /** Whether the agent does work of its own, where the switch can change. */
  readonly applies = input(true);
  /** The numbers of the kind for the agent, when the view per kind of action shows the switch. */
  readonly candidate = input<RoutineCandidate | null>(null);
  /** Another action of the page is busy. */
  readonly disabled = input(false);
  /** The tool with its new switch, after a change went through. */
  readonly changed = output<WithoutApprovalSwitch>();

  readonly id = `nh-assistant-without-approval-switch-${nextId++}`;
  readonly maxReason = maxStateReason;
  readonly change = signal<WithoutApprovalMode | null>(null);
  readonly reason = signal('');
  readonly busy = signal(false);
  readonly error = signal<NhAssistantError | null>(null);
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));

  /** The modes the switch may go to from where it is: shadow first, live only from shadow, off always. */
  readonly modes = computed<WithoutApprovalMode[]>(() => {
    switch (this.tool().mode) {
      case 'live':
        return ['shadow', 'off'];
      case 'shadow':
        return ['live', 'off'];
      default:
        return ['shadow'];
    }
  });

  readonly tone = computed(() => {
    switch (this.tool().mode) {
      case 'live':
        return 'warning';
      case 'shadow':
        return 'info';
      default:
        return '';
    }
  });

  constructor() {
    startNhAssistantAdminTranslations();
    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());
  }

  /** Whether the person may not put the switch in a mode, which only going live takes the rights for. */
  rightsLocked(mode: WithoutApprovalMode): boolean {
    return mode === 'live' && !this.mayChangeRights();
  }

  ask(mode: WithoutApprovalMode): void {
    if (this.rightsLocked(mode)) {
      return;
    }

    this.error.set(null);
    this.reason.set('');
    this.change.set(mode);
  }

  cancel(): void {
    this.change.set(null);
    this.error.set(null);
  }

  apply(): void {
    const mode = this.change();
    if (!mode) {
      return;
    }

    this.busy.set(true);
    this.error.set(null);
    this.subscription?.unsubscribe();
    const reason = this.reason().trim();
    this.subscription = this.admin
      .setAgentWithoutApproval(this.agentId(), this.tool().toolId, {
        mode,
        expectedVersion: this.tool().version,
        reason: reason.length > 0 ? reason : null
      })
      .subscribe({
        next: changed => {
          this.busy.set(false);
          this.change.set(null);
          this.changed.emit(changed);
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
}
