import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  input,
  output,
  signal
} from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Observable, Subscription } from 'rxjs';
import {
  AdminAgent,
  AgentShadowMode,
  AgentSkillState,
  NhAssistantAdminApiService,
  NhAssistantError,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';
import {
  AgentStateAction,
  agentStateActions,
  agentStatus,
  maxStateReason,
  ownShadowModeKey,
  shadowActionKey,
  shadowActions,
  shadowExplainKey,
  skillStateActions,
  skillStateExplainKey,
  skillStatus,
  stateKey,
  statusTone,
  withSkill,
  withState
} from '../agent-operations';
import { readableId, skillNameKeys } from '../agent-texts';
import { startNhAssistantAdminTranslations } from '../../i18n/nh-assistant-admin-translations';

let nextId = 0;

/** A change being confirmed: a run state with the state it changes from, or a shadow mode. */
type Change = { kind: 'state'; state: AgentStateAction; from: string } | { kind: 'shadow'; mode: AgentShadowMode };

/**
 * The status of an agent or, with `skill`, of one of its skills, as a button that opens the changes
 * it allows now: pause, switch off, stop and resume (switch on for one that is off), and its shadow
 * mode. Each change explains what it does and takes an optional reason before it applies; the
 * answer comes back through `changed` as the agent with its new state. `kinds: 'shadow'` offers the
 * shadow mode only, as the rollout does. An agent disabled in code shows its status without changes.
 */
@Component({
  selector: 'nh-assistant-agent-state-control',
  standalone: true,
  imports: [TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-agent-state-control.component.html',
  styleUrls: ['../../nh-assistant-admin-shared.scss', './agent-page.scss']
})
export class NhAssistantAgentStateControlComponent {
  private readonly admin = inject(NhAssistantAdminApiService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);
  private subscription?: Subscription;

  readonly agent = input.required<AdminAgent>();
  /** The skill whose status this is; `null` for the agent's own. */
  readonly skill = input<AgentSkillState | null>(null);
  /** `all` offers run states and the shadow mode; `shadow` only the shadow mode. */
  readonly kinds = input<'all' | 'shadow'>('all');
  /** Another action of the page is busy. */
  readonly disabled = input(false);
  /** The agent with its new state, after a change went through. */
  readonly changed = output<AdminAgent>();

  readonly id = `nh-assistant-state-control-${nextId++}`;
  readonly maxReason = maxStateReason;
  readonly open = signal(false);
  readonly change = signal<Change | null>(null);
  readonly reason = signal('');
  readonly busy = signal(false);
  readonly error = signal<NhAssistantError | null>(null);
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));

  readonly status = computed(() => {
    const skill = this.skill();
    return skill ? skillStatus(skill) : agentStatus(this.agent());
  });
  readonly tone = computed(() => statusTone(this.status()));
  readonly from = computed(() => this.skill()?.runState ?? this.agent().runState);
  readonly stateActions = computed<AgentStateAction[]>(() => {
    if (this.kinds() === 'shadow' || this.agent().runState === 'configured-off') {
      return [];
    }
    const skill = this.skill();
    return skill ? skillStateActions(skill) : agentStateActions(this.agent());
  });
  readonly shadowModes = computed<AgentShadowMode[]>(() => {
    const agent = this.agent();
    if (agent.runState === 'configured-off' || agent.chatOnly) {
      return [];
    }
    const skill = this.skill();
    return shadowActions(skill ? skill.shadow : agent.shadow);
  });
  readonly hasChanges = computed(() => this.stateActions().length > 0 || this.shadowModes().length > 0);
  /** The name of the skill, for the labels of its changes. */
  readonly skillName = computed(() => {
    const skill = this.skill();
    return skill ? { keys: skillNameKeys(this.agent().id, skill.skillId), fallback: readableId(skill.skillId) } : null;
  });

  readonly stateKey = stateKey;
  readonly shadowActionKey = shadowActionKey;

  constructor() {
    startNhAssistantAdminTranslations();
    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());
  }

  toggle(): void {
    this.open.update(open => !open);
    this.change.set(null);
    this.error.set(null);
    if (this.open()) {
      afterNextRender(() => this.host.nativeElement.querySelector<HTMLElement>('.menu-item')?.focus(), { injector: this.injector });
    }
  }

  /** Escape closes the menu or the form and returns the focus to the status button. */
  close(): void {
    if (!this.open() && this.change() === null) {
      return;
    }
    this.open.set(false);
    this.change.set(null);
    this.focusToggle();
  }

  askState(state: AgentStateAction): void {
    this.ask({ kind: 'state', state, from: this.from() });
  }

  askShadow(mode: AgentShadowMode): void {
    this.ask({ kind: 'shadow', mode });
  }

  /** The hint of a state change in the menu: what it does to the agent's work. */
  hintKey(state: AgentStateAction): string {
    return `nh-assistant.admin.agents.menu.${this.from() === 'off' && state === 'active' ? 'switch-on' : state}`;
  }

  /** The explanation of the change being confirmed. */
  explainKey(change: Change): string {
    if (change.kind === 'shadow') {
      return shadowExplainKey(this.agent(), change.mode, this.skill() !== null);
    }
    return this.skill() ? skillStateExplainKey(change.from, change.state) : stateKey('explain', change.from, change.state);
  }

  /** The mode the agent or skill was set up with, which a return to its own setting restores. */
  readonly ownModeKey = computed(() => ownShadowModeKey(this.agent(), this.skill()?.skillId));

  confirmKey(change: Change): string {
    return change.kind === 'shadow'
      ? `nh-assistant.admin.agents.shadow.confirm.${change.mode}`
      : stateKey('confirm', change.from, change.state);
  }

  apply(): void {
    const change = this.change();
    if (!change || this.busy()) {
      return;
    }

    const agent = this.agent();
    const skill = this.skill();
    const reason = this.reason().trim().length > 0 ? this.reason().trim() : null;
    if (change.kind === 'shadow') {
      const request = { mode: change.mode, reason };
      if (skill) {
        this.run(this.admin.setSkillShadow(agent.id, skill.skillId, request), state => withSkill(agent, state));
      } else {
        this.run(this.admin.setAgentShadow(agent.id, request), state => withState(agent, state));
      }
      return;
    }

    const request = { state: change.state, reason };
    if (skill) {
      this.run(this.admin.setSkillState(agent.id, skill.skillId, request), state => withSkill(agent, state));
    } else {
      this.run(this.admin.setAgentState(agent.id, request), state => withState(agent, state));
    }
  }

  cancel(): void {
    this.change.set(null);
    this.focusToggle();
  }

  value(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  private ask(change: Change): void {
    this.open.set(false);
    this.error.set(null);
    this.reason.set('');
    this.change.set(change);
    afterNextRender(() => this.host.nativeElement.querySelector<HTMLInputElement>('.change-form input')?.focus(), { injector: this.injector });
  }

  private run<T>(request: Observable<T>, updated: (result: T) => AdminAgent): void {
    this.busy.set(true);
    this.error.set(null);
    this.subscription?.unsubscribe();
    this.subscription = request.subscribe({
      next: result => {
        this.busy.set(false);
        this.change.set(null);
        this.reason.set('');
        this.changed.emit(updated(result));
        this.focusToggle();
      },
      error: (error: unknown) => {
        this.busy.set(false);
        this.error.set(toNhAssistantError(error));
      }
    });
  }

  private focusToggle(): void {
    afterNextRender(() => this.host.nativeElement.querySelector<HTMLElement>('.toggle')?.focus(), { injector: this.injector });
  }
}
