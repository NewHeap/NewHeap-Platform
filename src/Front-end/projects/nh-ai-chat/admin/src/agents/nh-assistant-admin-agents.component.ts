import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  signal,
  viewChild
} from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Observable, Subscription, catchError, forkJoin, of } from 'rxjs';
import {
  AdminAgent,
  AgentShadowMode,
  AgentShadowState,
  AgentSkillState,
  AgentState,
  McpServer,
  NhAssistantAdminApiService,
  NhAssistantError,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  NhAssistantStore,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError,
  SetAgentStateRequest,
  ToolCatalogEntry
} from '@newheap/platform-ai-chat';
import { codeTextKeys, policyKeys, readableId, skillDescriptionKeys, skillNameKeys } from './agent-texts';
import { NhAssistantAdminAgentEditorComponent } from './nh-assistant-admin-agent-editor.component';
import { NhAssistantAgentEvaluationComponent } from './nh-assistant-agent-evaluation.component';
import { NhAssistantAgentFeedbackReportComponent } from './nh-assistant-agent-feedback-report.component';
import { NhAssistantAgentRunConfigurationComponent } from './nh-assistant-agent-run-configuration.component';
import { NhAssistantAccountPickerComponent } from './run-config/nh-assistant-account-picker.component';

let nextId = 0;

type AgentStateAction = SetAgentStateRequest['state'];

/** The longest reason the server stores with a state change. */
const maxStateReason = 500;

/**
 * Agent administration: code agents (override, reset) and agents created here (edit, delete),
 * and for every agent its run state with pause, stop and resume, its shadow mode and that of each
 * skill, and the technical account of an agent that answers to one, each with an optional reason.
 * Editing opens `nh-assistant-admin-agent-editor`; the feedback action opens
 * `nh-assistant-agent-feedback-report` for the agent, the evaluation action of an agent with
 * shadow variants opens `nh-assistant-agent-evaluation`, and the run configuration action opens
 * `nh-assistant-agent-run-configuration` for the skills, triggers and run settings defined as data.
 */
@Component({
  selector: 'nh-assistant-admin-agents',
  standalone: true,
  imports: [
    DatePipe,
    TranslatePipe,
    NhAssistantTranslatePipe,
    NhAssistantIconComponent,
    NhAssistantAdminAgentEditorComponent,
    NhAssistantAgentEvaluationComponent,
    NhAssistantAgentFeedbackReportComponent,
    NhAssistantAgentRunConfigurationComponent,
    NhAssistantAccountPickerComponent
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-admin-agents.component.html',
  styleUrl: '../nh-assistant-admin-shared.scss'
})
export class NhAssistantAdminAgentsComponent {
  private readonly admin = inject(NhAssistantAdminApiService);
  private readonly store = inject(NhAssistantStore);
  private readonly injector = inject(Injector);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly backButton = viewChild<ElementRef<HTMLButtonElement>>('back');
  private readonly reasonInput = viewChild<ElementRef<HTMLInputElement>>('reasonInput');
  private subscription?: Subscription;
  private actionSubscription?: Subscription;

  readonly id = `nh-assistant-admin-agents-${nextId++}`;
  readonly loading = signal(true);
  readonly loadFailed = signal(false);
  readonly agents = signal<AdminAgent[]>([]);
  readonly tools = signal<ToolCatalogEntry[]>([]);
  readonly servers = signal<McpServer[]>([]);
  /** `undefined`: list; `null`: new agent; an agent: editing that agent. */
  readonly editing = signal<AdminAgent | null | undefined>(undefined);
  /** The agent whose feedback report is shown instead of the list. */
  readonly reporting = signal<AdminAgent | null>(null);
  /** The agent whose comparison with its shadow variants is shown instead of the list. */
  readonly evaluating = signal<AdminAgent | null>(null);
  /** The agent whose run configuration is shown instead of the list. */
  readonly configuring = signal<AdminAgent | null>(null);
  readonly confirming = signal<{ agentId: string; action: 'delete' | 'reset' } | null>(null);
  readonly busyAgentId = signal<string | null>(null);
  /** The state change whose reason is being entered, of the agent or one of its skills. */
  readonly changingState = signal<{ agentId: string; skillId?: string; state: AgentStateAction } | null>(null);
  /** The shadow mode change whose reason is being entered, of the agent or one of its skills. */
  readonly changingShadow = signal<{ agentId: string; skillId?: string; mode: AgentShadowMode } | null>(null);
  /** The change of the technical account being entered: a new account, or the one in code. */
  readonly changingAccount = signal<{ agentId: string; mode: 'set' | 'code' } | null>(null);
  readonly accountId = signal('');
  /** Whether the application offers an account search for the technical account. */
  readonly accountSearch = signal(false);
  /** The names of the technical accounts in the list, by account id. */
  readonly accountNames = signal<ReadonlyMap<string, string>>(new Map());
  /** The agent whose last action failed, so its error shows on its card. */
  readonly errorAgentId = signal<string | null>(null);
  /** The run configuration opens with the guided setup, for an agent created to work on its own. */
  readonly setupOnOpen = signal(false);
  readonly stateReason = signal('');
  readonly maxReason = maxStateReason;
  readonly error = signal<NhAssistantError | null>(null);
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));
  readonly codeTextKeys = codeTextKeys;
  readonly skillNameKeys = skillNameKeys;
  readonly skillDescriptionKeys = skillDescriptionKeys;
  readonly policyKeys = policyKeys;
  readonly readableId = readableId;

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.subscription?.unsubscribe();
      this.actionSubscription?.unsubscribe();
    });
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.loadFailed.set(false);
    this.subscription?.unsubscribe();
    this.subscription = forkJoin([
      this.admin.getAgents(),
      this.admin.getTools(),
      this.admin.getMcpServers(),
      // The account search is a convenience; without the catalog the account is entered by id.
      this.admin.getRunCatalog().pipe(catchError(() => of(null)))
    ]).subscribe({
      next: ([agents, tools, servers, catalog]) => {
        this.agents.set(agents);
        this.tools.set(tools);
        this.servers.set(servers);
        this.accountSearch.set(catalog?.accountSearch === true);
        this.loading.set(false);
        this.loadAccountNames(agents);
      },
      error: () => {
        this.loading.set(false);
        this.loadFailed.set(true);
      }
    });
  }

  create(): void {
    this.error.set(null);
    this.editing.set(null);
  }

  edit(agent: AdminAgent): void {
    this.error.set(null);
    this.editing.set(agent);
  }

  /** Shows what people said when they rejected the agent's proposals. */
  openReport(agent: AdminAgent): void {
    this.error.set(null);
    this.confirming.set(null);
    this.reporting.set(agent);
    afterNextRender(() => this.backButton()?.nativeElement.focus(), { injector: this.injector });
  }

  /** The shadow variants of an agent, by id. */
  variantsOf(agent: AdminAgent): string[] {
    return this.agents()
      .filter(item => item.shadowOf === agent.id)
      .map(item => item.id);
  }

  /** Compares the agent with its shadow variants. */
  openEvaluation(agent: AdminAgent): void {
    this.error.set(null);
    this.confirming.set(null);
    this.evaluating.set(agent);
    afterNextRender(() => this.backButton()?.nativeElement.focus(), { injector: this.injector });
  }

  /**
   * Whether the agent can run on its own and so has a run configuration: an agent in code with runs,
   * or any agent created here, which gets run settings as data.
   */
  hasRunConfiguration(agent: AdminAgent): boolean {
    return agent.source === 'admin' || (agent.shadow !== null && agent.shadow !== undefined);
  }

  /** Shows the agent's skills, triggers and run settings defined as data. */
  openRunConfiguration(agent: AdminAgent, setup = false): void {
    this.setupOnOpen.set(setup);
    this.error.set(null);
    this.confirming.set(null);
    this.configuring.set(agent);
    afterNextRender(() => this.backButton()?.nativeElement.focus(), { injector: this.injector });
  }

  /** Returns to the list, refreshed, and to the run configuration action of the agent. */
  closeRunConfiguration(): void {
    const agentId = this.configuring()?.id;
    this.configuring.set(null);
    this.refreshAgents();
    if (agentId !== undefined) {
      afterNextRender(() => {
        const action = Array.from(this.host.nativeElement.querySelectorAll<HTMLButtonElement>('[data-run-config-agent]'))
          .find(item => item.dataset['runConfigAgent'] === agentId);
        action?.focus();
      }, { injector: this.injector });
    }
  }

  /** Returns to the list and to the evaluation action of the agent whose comparison was open. */
  closeEvaluation(): void {
    const agentId = this.evaluating()?.id;
    this.evaluating.set(null);
    if (agentId !== undefined) {
      afterNextRender(() => {
        const action = Array.from(this.host.nativeElement.querySelectorAll<HTMLButtonElement>('[data-evaluation-agent]'))
          .find(item => item.dataset['evaluationAgent'] === agentId);
        action?.focus();
      }, { injector: this.injector });
    }
  }

  /** Returns to the list and to the feedback action of the agent whose report was open. */
  closeReport(): void {
    const agentId = this.reporting()?.id;
    this.reporting.set(null);
    if (agentId !== undefined) {
      afterNextRender(() => {
        const host = this.host.nativeElement;
        const action = Array.from(host.querySelectorAll<HTMLButtonElement>('[data-feedback-agent]'))
          .find(item => item.dataset['feedbackAgent'] === agentId);
        action?.focus();
      }, { injector: this.injector });
    }
  }

  /** A new agent meant to work on its own continues in its run configuration's guided setup. */
  onWorkRequested(agent: AdminAgent): void {
    this.onSaved(agent);
    this.openRunConfiguration(agent, true);
  }

  /** The name of an account or person, or the id when the application does not know it. */
  accountName(actorId: string | null | undefined): string {
    return actorId ? this.accountNames().get(actorId) ?? actorId : '';
  }

  onSaved(agent: AdminAgent): void {
    this.agents.update(agents => agents.some(item => item.id === agent.id)
      ? agents.map(item => item.id === agent.id ? agent : item)
      : [...agents, agent]);
    this.editing.set(undefined);
    this.refreshChat();
  }

  /** The state changes the agent allows: none for an agent that is disabled in code. */
  stateActions(agent: AdminAgent): AgentStateAction[] {
    switch (agent.runState) {
      case 'active':
        return ['paused', 'stopped'];
      case 'paused':
        return ['active', 'stopped'];
      case 'stopped':
        return ['active'];
      default:
        return [];
    }
  }

  /** The state changes a skill allows. */
  skillActions(skill: AgentSkillState): AgentStateAction[] {
    switch (skill.runState) {
      case 'active':
        return ['paused', 'stopped'];
      case 'paused':
        return ['active', 'stopped'];
      default:
        return ['active'];
    }
  }

  /** Opens the reason form of a state change of the agent or, with a skill, of that skill. */
  askState(agent: AdminAgent, state: AgentStateAction, skill?: AgentSkillState): void {
    this.closeForms();
    this.changingState.set({ agentId: agent.id, skillId: skill?.skillId, state });
    afterNextRender(() => this.reasonInput()?.nativeElement.focus(), { injector: this.injector });
  }

  /**
   * The shadow modes an agent or skill can change to: the opposite of its own mode, and the mode
   * in code once an administrator overrode it. A shadow variant, and an agent without runs that
   * only chats, offer none.
   */
  shadowActions(shadow: AgentShadowState | null | undefined): AgentShadowMode[] {
    if (!shadow || shadow.locked) {
      return [];
    }
    const own = shadow.override ?? shadow.configured;
    const actions: AgentShadowMode[] = [own ? 'live' : 'shadow'];
    return shadow.override === null ? actions : [...actions, 'code'];
  }

  /** Opens the reason form of a shadow mode change of the agent or, with a skill, of that skill. */
  askShadow(agent: AdminAgent, mode: AgentShadowMode, skill?: AgentSkillState): void {
    this.closeForms();
    this.changingShadow.set({ agentId: agent.id, skillId: skill?.skillId, mode });
    afterNextRender(() => this.reasonInput()?.nativeElement.focus(), { injector: this.injector });
  }

  isChangingShadow(agentId: string, mode: AgentShadowMode, skillId?: string): boolean {
    const change = this.changingShadow();
    return change?.agentId === agentId && change.mode === mode && change.skillId === skillId;
  }

  applyShadow(agent: AdminAgent): void {
    const change = this.changingShadow();
    if (change?.agentId !== agent.id) {
      return;
    }

    const reason = this.stateReason().trim();
    const request = { mode: change.mode, reason: reason.length > 0 ? reason : null };
    if (change.skillId !== undefined) {
      this.run(
        agent.id,
        this.admin.setSkillShadow(agent.id, change.skillId, request),
        skill => this.replace(withSkill(agent, skill)));
      return;
    }

    this.run(
      agent.id,
      this.admin.setAgentShadow(agent.id, request),
      state => this.replace(withState(agent, state)));
  }

  /** Opens the form that changes the technical account, or returns to the one in code. */
  askAccount(agent: AdminAgent, mode: 'set' | 'code'): void {
    this.closeForms();
    this.changingAccount.set({ agentId: agent.id, mode });
    this.accountId.set('');
    afterNextRender(() => this.host.nativeElement.querySelector<HTMLElement>('[data-account-form] input')?.focus(), { injector: this.injector });
  }

  isChangingAccount(agentId: string, mode: 'set' | 'code'): boolean {
    const change = this.changingAccount();
    return change?.agentId === agentId && change.mode === mode;
  }

  applyAccount(agent: AdminAgent): void {
    const change = this.changingAccount();
    if (change?.agentId !== agent.id) {
      return;
    }

    const actorId = change.mode === 'set' ? this.accountId().trim() : null;
    if (actorId === '') {
      this.error.set({ code: 'assistant-validation', messageKey: 'nh-assistant.admin.run-config.field-errors.required' });
      this.errorAgentId.set(agent.id);
      return;
    }
    const reason = this.stateReason().trim();
    this.run(
      agent.id,
      this.admin.setServiceAccount(agent.id, { actorId, reason: reason.length > 0 ? reason : null }),
      state => this.replace(withState(agent, state)));
  }

  isChangingState(agentId: string, state: AgentStateAction, skillId?: string): boolean {
    const change = this.changingState();
    return change?.agentId === agentId && change.state === state && change.skillId === skillId;
  }

  applyState(agent: AdminAgent): void {
    const change = this.changingState();
    if (change?.agentId !== agent.id) {
      return;
    }

    const reason = this.stateReason().trim();
    const request = { state: change.state, reason: reason.length > 0 ? reason : null };
    if (change.skillId !== undefined) {
      this.run(
        agent.id,
        this.admin.setSkillState(agent.id, change.skillId, request),
        skill => this.replace(withSkill(agent, skill)));
      return;
    }

    this.run(
      agent.id,
      this.admin.setAgentState(agent.id, request),
      state => this.replace(withState(agent, state)));
  }

  value(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  confirm(agentId: string, action: 'delete' | 'reset'): void {
    this.changingState.set(null);
    this.changingShadow.set(null);
    this.changingAccount.set(null);
    this.confirming.set({ agentId, action });
  }

  isConfirming(agentId: string, action: 'delete' | 'reset'): boolean {
    const confirming = this.confirming();
    return confirming?.agentId === agentId && confirming.action === action;
  }

  reset(agent: AdminAgent): void {
    this.run(agent.id, this.admin.resetAgent(agent.id), updated => this.replace(updated));
  }

  remove(agent: AdminAgent): void {
    this.run<void>(agent.id, this.admin.deleteAgent(agent.id), () => {
      this.agents.update(agents => agents.filter(item => item.id !== agent.id));
    });
  }

  private run<T>(agentId: string, request: Observable<T>, done: (result: T) => void): void {
    this.busyAgentId.set(agentId);
    this.errorAgentId.set(agentId);
    this.confirming.set(null);
    this.changingState.set(null);
    this.changingShadow.set(null);
    this.changingAccount.set(null);
    this.error.set(null);
    this.actionSubscription?.unsubscribe();
    this.actionSubscription = request.subscribe({
      next: result => done(result),
      complete: () => {
        this.busyAgentId.set(null);
        this.refreshChat();
        this.loadAccountNames(this.agents());
      },
      error: (error: unknown) => {
        this.busyAgentId.set(null);
        this.error.set(toNhAssistantError(error));
      }
    });
  }

  /** Looks up the names of the technical accounts and of the people who changed an agent's state. */
  private loadAccountNames(agents: readonly AdminAgent[]): void {
    const ids = [...new Set(agents.flatMap(agent => [
      agent.serviceAccount?.actorId,
      agent.serviceAccount?.configuredActorId,
      agent.stateChangedBy,
      agent.shadow?.changedBy
    ]).filter((id): id is string => typeof id === 'string' && id.length > 0))];
    if (ids.length === 0) {
      return;
    }
    this.admin.getAccounts(ids).subscribe({
      next: accounts => this.accountNames.set(new Map(accounts.map(account => [account.actorId, account.displayName]))),
      error: () => undefined
    });
  }

  /** Closes the open confirmation and change forms before another opens; one is open at a time. */
  private closeForms(): void {
    this.confirming.set(null);
    this.error.set(null);
    this.stateReason.set('');
    this.accountId.set('');
    this.changingState.set(null);
    this.changingShadow.set(null);
    this.changingAccount.set(null);
  }

  private replace(agent: AdminAgent): void {
    this.agents.update(agents => agents.map(item => item.id === agent.id ? agent : item));
  }

  /**
   * Reloads the agents behind the visible list, for example their skills after the run
   * configuration changed; the list stays as it is when that fails.
   */
  private refreshAgents(): void {
    this.subscription?.unsubscribe();
    this.subscription = this.admin.getAgents().subscribe({
      next: agents => this.agents.set(agents),
      error: () => undefined
    });
    this.refreshChat();
  }

  /** Agent changes affect the agent picker of the panel. */
  private refreshChat(): void {
    void this.store.reloadStatus();
  }
}

function withState(agent: AdminAgent, state: AgentState): AdminAgent {
  return {
    ...agent,
    isEnabled: state.runState !== 'stopped',
    runState: state.runState,
    stateChangedAt: state.changedAt,
    stateChangedBy: state.changedBy,
    stateReason: state.reason,
    waitingInputs: state.waitingInputs,
    skills: state.skills ?? agent.skills,
    shadow: state.shadow ?? agent.shadow,
    serviceAccount: state.serviceAccount === undefined ? agent.serviceAccount : state.serviceAccount
  };
}

function withSkill(agent: AdminAgent, skill: AgentSkillState): AdminAgent {
  return {
    ...agent,
    skills: (agent.skills ?? []).map(item => item.skillId === skill.skillId ? skill : item)
  };
}
