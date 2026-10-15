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
import { Observable, Subscription, forkJoin } from 'rxjs';
import {
  AdminAgent,
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
import { NhAssistantAdminAgentEditorComponent } from './nh-assistant-admin-agent-editor.component';
import { NhAssistantAgentFeedbackReportComponent } from './nh-assistant-agent-feedback-report.component';

let nextId = 0;

type AgentStateAction = SetAgentStateRequest['state'];

/** The longest reason the server stores with a state change. */
const maxStateReason = 500;

/**
 * Agent administration: code agents (override, reset) and agents created here (edit, delete),
 * and for every agent its run state with pause, stop and resume, each with an optional reason.
 * Editing opens `nh-assistant-admin-agent-editor`; the feedback action opens
 * `nh-assistant-agent-feedback-report` for the agent.
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
    NhAssistantAgentFeedbackReportComponent
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
  readonly confirming = signal<{ agentId: string; action: 'delete' | 'reset' } | null>(null);
  readonly busyAgentId = signal<string | null>(null);
  /** The state change whose reason is being entered. */
  readonly changingState = signal<{ agentId: string; state: AgentStateAction } | null>(null);
  readonly stateReason = signal('');
  readonly maxReason = maxStateReason;
  readonly error = signal<NhAssistantError | null>(null);
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));

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
    this.subscription = forkJoin([this.admin.getAgents(), this.admin.getTools(), this.admin.getMcpServers()]).subscribe({
      next: ([agents, tools, servers]) => {
        this.agents.set(agents);
        this.tools.set(tools);
        this.servers.set(servers);
        this.loading.set(false);
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

  /** Opens the reason form of a state change. */
  askState(agent: AdminAgent, state: AgentStateAction): void {
    this.confirming.set(null);
    this.error.set(null);
    this.stateReason.set('');
    this.changingState.set({ agentId: agent.id, state });
    afterNextRender(() => this.reasonInput()?.nativeElement.focus(), { injector: this.injector });
  }

  isChangingState(agentId: string, state: AgentStateAction): boolean {
    const change = this.changingState();
    return change?.agentId === agentId && change.state === state;
  }

  applyState(agent: AdminAgent): void {
    const change = this.changingState();
    if (change?.agentId !== agent.id) {
      return;
    }

    const reason = this.stateReason().trim();
    this.run(
      agent.id,
      this.admin.setAgentState(agent.id, { state: change.state, reason: reason.length > 0 ? reason : null }),
      state => this.replace(withState(agent, state)));
  }

  value(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  confirm(agentId: string, action: 'delete' | 'reset'): void {
    this.changingState.set(null);
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
    this.confirming.set(null);
    this.changingState.set(null);
    this.error.set(null);
    this.actionSubscription?.unsubscribe();
    this.actionSubscription = request.subscribe({
      next: result => done(result),
      complete: () => {
        this.busyAgentId.set(null);
        this.refreshChat();
      },
      error: (error: unknown) => {
        this.busyAgentId.set(null);
        this.error.set(toNhAssistantError(error));
      }
    });
  }

  private replace(agent: AdminAgent): void {
    this.agents.update(agents => agents.map(item => item.id === agent.id ? agent : item));
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
    waitingInputs: state.waitingInputs
  };
}
