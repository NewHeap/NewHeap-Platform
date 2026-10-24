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
  output,
  signal,
  viewChild
} from '@angular/core';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { Observable, Subscription, catchError, forkJoin, map, of } from 'rxjs';
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
  NhAssistantPanelService,
  NhAssistantStore,
  nhAssistantErrorMessageKey,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError,
  RunCatalog,
  SetAgentStateRequest,
  ToolCatalogEntry
} from '@newheap/platform-ai-chat';
import {
  ɵNhAssistantRunNowDialogComponent as NhAssistantRunNowDialogComponent,
  type ɵNhAssistantRunNowAgent as NhAssistantRunNowAgent
} from '@newheap/platform-ai-chat/agent-runs';
import { NhAssistantAccountNames, NhAssistantAccountNamesErrorComponent } from '../nh-assistant-account-names';
import {
  codeTextKeys,
  policyKeys,
  readableId,
  runNowAgentOf,
  skillDescriptionKeys,
  skillNameKeys,
  warningKeys,
  WorksOnSummary,
  worksOnSummary
} from './agent-texts';
import { NhAssistantAdminAgentEditorComponent } from './nh-assistant-admin-agent-editor.component';
import { NhAssistantAgentEvaluationComponent } from './nh-assistant-agent-evaluation.component';
import { NhAssistantAgentFeedbackReportComponent } from './nh-assistant-agent-feedback-report.component';
import { NhAssistantAgentRunHistoryComponent } from './nh-assistant-agent-run-history.component';
import { NhAssistantAgentRunConfigurationComponent } from './nh-assistant-agent-run-configuration.component';
import { NhAssistantAccountPickerComponent } from './run-config/nh-assistant-account-picker.component';
import { NhAssistantAgentHandOffsComponent } from './run-config/nh-assistant-agent-hand-offs.component';
import { NhAssistantAgentShadowVariantsComponent } from './run-config/nh-assistant-agent-shadow-variants.component';
import { NhAssistantAgentPackageExportDialogComponent } from './agent-packages/nh-assistant-agent-package-export-dialog.component';
import { NhAssistantAgentPackageImportDialogComponent } from './agent-packages/nh-assistant-agent-package-import-dialog.component';
import { NhAssistantPackageLockComponent } from './agent-packages/nh-assistant-package-lock.component';
import { agentDefinitionLockOf, agentPackageOf, isPackageLocked } from './agent-packages/package-parts';

let nextId = 0;

type AgentStateAction = SetAgentStateRequest['state'];

/** A state change being entered, of the agent or one of its skills, with the state it changes from. */
interface StateChange {
  agentId: string;
  skillId?: string;
  state: AgentStateAction;
  from: AdminAgent['runState'] | AgentSkillState['runState'];
}

/** The longest reason the server stores with a state change. */
const maxStateReason = 500;

/** Refusals of a technical account that concern the account field itself. */
const accountRefusals = new Set(['assistant-service-account-not-found', 'assistant-service-account-not-used']);

/** The run catalog as `load` reads it: the catalog, or why it could not be read. */
interface CatalogRead {
  catalog: RunCatalog | null;
  error: NhAssistantError | null;
}

/**
 * Agent administration: code agents (override, reset) and agents created here (edit, delete),
 * and for every agent and skill its run state with pause, switch off, stop and resume (switch on
 * for one that is off), its shadow mode and that of each
 * skill, and the technical account of an agent that answers to one, each with an optional reason.
 * Editing opens `nh-assistant-admin-agent-editor`; the feedback action opens
 * `nh-assistant-agent-feedback-report` for the agent, the evaluation action of an agent with
 * shadow variants opens `nh-assistant-agent-evaluation`, and the run configuration action opens
 * `nh-assistant-agent-run-configuration` for the skills, triggers and run settings defined as data.
 * "Run now" on an agent that runs on its own opens the run-now dialog: the whole application (or a
 * division of it) or an object of one of its types, and an optional instruction; the receipt opens
 * the agent's conversation in the assistant panel. "Runs" opens `nh-assistant-agent-run-history`, the
 * agent's runs with their results. When the run catalog cannot be read, the page says why with its
 * code and offers to read it again; until then it offers no "Run now" and the editor leaves what an
 * agent works on and its groups as they are. The run configuration opens with the agent's flow, whose
 * entry points open the editor, the runs and the technical account here and return to the run
 * configuration afterwards; `openMcpServers` asks the host to show the MCP servers. "Import package"
 * opens the import dialog of agent packages, and "Export as package" on an agent the export dialog
 * with that agent chosen. An agent that belongs to a package says so on its card with "Unlink the
 * agent"; it cannot be deleted until it is unlinked, nor reset while its package locks fields of
 * its definition.
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
    NhAssistantAgentRunHistoryComponent,
    NhAssistantAgentRunConfigurationComponent,
    NhAssistantAccountPickerComponent,
    NhAssistantRunNowDialogComponent,
    NhAssistantAccountNamesErrorComponent,
    NhAssistantAgentHandOffsComponent,
    NhAssistantAgentShadowVariantsComponent,
    NhAssistantAgentPackageExportDialogComponent,
    NhAssistantAgentPackageImportDialogComponent,
    NhAssistantPackageLockComponent
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-admin-agents.component.html',
  styleUrl: '../nh-assistant-admin-shared.scss'
})
export class NhAssistantAdminAgentsComponent {
  private readonly admin = inject(NhAssistantAdminApiService);
  private readonly store = inject(NhAssistantStore);
  private readonly panel = inject(NhAssistantPanelService);
  private readonly translate = inject(TranslateService);
  private readonly injector = inject(Injector);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly backButton = viewChild<ElementRef<HTMLButtonElement>>('back');
  private readonly reasonInput = viewChild<ElementRef<HTMLInputElement>>('reasonInput');
  private subscription?: Subscription;
  private actionSubscription?: Subscription;
  private catalogSubscription?: Subscription;

  readonly id = `nh-assistant-admin-agents-${nextId++}`;
  readonly loading = signal(true);
  readonly loadFailed = signal(false);
  readonly agents = signal<AdminAgent[]>([]);
  readonly tools = signal<ToolCatalogEntry[]>([]);
  readonly servers = signal<McpServer[]>([]);
  /** The run catalog: object types by name, policies and divisions; `null` when it could not be read. */
  readonly catalog = signal<RunCatalog | null>(null);
  /** Why the run catalog could not be read; `null` once it was. */
  readonly catalogError = signal<NhAssistantError | null>(null);
  readonly catalogErrorKeys = computed(() => nhAssistantErrorKeys(this.catalogError()));
  /** The run catalog is being read again after a failure. */
  readonly catalogLoading = signal(false);
  /** `undefined`: list; `null`: new agent; an agent: editing that agent. */
  readonly editing = signal<AdminAgent | null | undefined>(undefined);
  /** The agent whose feedback report is shown instead of the list. */
  readonly reporting = signal<AdminAgent | null>(null);
  /** The agent whose comparison with its shadow variants is shown instead of the list. */
  readonly evaluating = signal<AdminAgent | null>(null);
  /** The agent whose run configuration is shown instead of the list. */
  readonly configuring = signal<AdminAgent | null>(null);
  /** The agent whose runs are shown instead of the list. */
  readonly viewingRuns = signal<AdminAgent | null>(null);
  /**
   * The agent whose run configuration opened the editor or the runs from its flow; closing them
   * returns to its run configuration instead of the list.
   */
  readonly returnTo = signal<string | null>(null);
  /** The flow of a run configuration asks to show the MCP servers, whose background use it explains. */
  readonly openMcpServers = output<void>();
  readonly confirming = signal<{ agentId: string; action: 'delete' | 'reset' } | null>(null);
  readonly busyAgentId = signal<string | null>(null);
  /** The state change whose reason is being entered, of the agent or one of its skills. */
  readonly changingState = signal<StateChange | null>(null);
  /** The shadow mode change whose reason is being entered, of the agent or one of its skills. */
  readonly changingShadow = signal<{ agentId: string; skillId?: string; mode: AgentShadowMode } | null>(null);
  /** The change of the technical account being entered: a new account, or the one in code. */
  readonly changingAccount = signal<{ agentId: string; mode: 'set' | 'code' } | null>(null);
  readonly accountId = signal('');
  /** Why the account of the open account form was refused, shown at the account field. */
  readonly accountError = signal<NhAssistantError | null>(null);
  /** The text of the account field's error: the refused field value, then the code. */
  readonly accountErrorKeys = computed(() => {
    const error = this.accountError();
    const value = error?.fieldErrors?.['actorId']?.[0];
    return value ? [`nh-assistant.admin.run-config.field-errors.${value}`, ...nhAssistantErrorKeys(error)] : nhAssistantErrorKeys(error);
  });
  /** Whether the application offers an account search for the technical account. */
  readonly accountSearch = signal(false);
  /**
   * The names of the technical accounts and of the people who changed an agent, once known, and why
   * they are not.
   */
  readonly accountNameLookup = new NhAssistantAccountNames();
  /** The names of the technical accounts in the list, by account id. */
  readonly accountNames = computed(() => this.accountNameLookup.names());
  /** The agent whose last action failed, so its error shows on its card. */
  readonly errorAgentId = signal<string | null>(null);
  /** The agent whose export dialog is open, chosen in it; `null` while it is closed. */
  readonly exporting = signal<AdminAgent | null>(null);
  /** Whether the import dialog of agent packages is open. */
  readonly importing = signal(false);
  readonly agentPackageOf = agentPackageOf;
  /** The agent the run-now dialog runs; `null` while it is closed. */
  readonly runningNow = signal<AdminAgent | null>(null);
  /** The agent of the run-now dialog as the dialog needs it: its name and what it works on. */
  readonly runNowAgent = computed<NhAssistantRunNowAgent | null>(() => {
    const agent = this.runningNow();
    if (!agent) {
      return null;
    }
    const keys = codeTextKeys(agent.source, agent.displayName);
    const translated = keys.length > 0 ? this.translate.instant(keys[0]) as string : null;
    return runNowAgentOf(agent, translated && translated !== keys[0] ? translated : agent.displayName);
  });
  /** The run configuration opens with the guided setup, for an agent created to work on its own. */
  readonly setupOnOpen = signal(false);
  readonly stateReason = signal('');
  readonly maxReason = maxStateReason;
  readonly error = signal<NhAssistantError | null>(null);
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));
  readonly codeTextKeys = codeTextKeys;
  readonly skillNameKeys = skillNameKeys;
  readonly warningKeys = warningKeys;
  readonly skillDescriptionKeys = skillDescriptionKeys;
  readonly policyKeys = policyKeys;
  readonly readableId = readableId;

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.subscription?.unsubscribe();
      this.actionSubscription?.unsubscribe();
      this.catalogSubscription?.unsubscribe();
    });
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.loadFailed.set(false);
    this.subscription?.unsubscribe();
    this.catalogSubscription?.unsubscribe();
    this.catalogLoading.set(false);
    this.subscription = forkJoin([
      this.admin.getAgents(),
      this.admin.getTools(),
      this.admin.getMcpServers(),
      // The agents stay usable without the catalog; the page says why it is missing.
      this.readCatalog()
    ]).subscribe({
      next: ([agents, tools, servers, read]) => {
        this.agents.set(agents);
        this.tools.set(tools);
        this.servers.set(servers);
        this.applyCatalog(read);
        this.loading.set(false);
        this.loadAccountNames(agents);
      },
      error: () => {
        this.loading.set(false);
        this.loadFailed.set(true);
      }
    });
  }

  /** Reads the run catalog again after it could not be read. */
  retryCatalog(): void {
    this.catalogLoading.set(true);
    this.catalogSubscription?.unsubscribe();
    this.catalogSubscription = this.readCatalog().subscribe(read => {
      this.catalogLoading.set(false);
      this.applyCatalog(read);
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

  /**
   * Whether the agent can be run now: it runs on its own, so it has a run configuration, it is
   * neither stopped nor disabled in code, and the run catalog names what it can run on. An agent
   * that is off still runs for a person. The server decides the rest when the run starts.
   */
  canRunNow(agent: AdminAgent): boolean {
    return this.catalog() !== null
      && this.hasRunConfiguration(agent)
      && (agent.runState === 'active' || agent.runState === 'paused' || agent.runState === 'off');
  }

  /** Opens the export dialog with the agent chosen. */
  exportPackage(agent: AdminAgent): void {
    this.closeForms();
    this.exporting.set(agent);
  }

  /** Opens the import dialog of agent packages. */
  importPackage(): void {
    this.closeForms();
    this.importing.set(true);
  }

  /** A package was imported: its agents and their parts are read again. */
  onPackageImported(): void {
    this.refreshAgents();
  }

  /** The agent or a part of it was unlinked from its package. */
  onPackageUnlinked(): void {
    this.refreshAgents();
  }

  /** A package the agent belongs to keeps it from being deleted until it is unlinked. */
  deleteLocked(agent: AdminAgent): boolean {
    return agentPackageOf(agent) !== null;
  }

  /** A reset would give fields the package locks the code's values, which the server refuses. */
  resetLocked(agent: AdminAgent): boolean {
    return isPackageLocked(agentDefinitionLockOf(agent));
  }

  /** Opens the run-now dialog of the agent. */
  runNow(agent: AdminAgent): void {
    this.closeForms();
    this.runningNow.set(agent);
  }

  /** Opens the conversation the run works in, in the assistant panel. */
  openConversation(conversationId: string): void {
    this.panel.open(conversationId);
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
    this.returnTo.set(null);
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

  /** Shows the agent's runs with their results. */
  openRuns(agent: AdminAgent): void {
    this.error.set(null);
    this.confirming.set(null);
    this.viewingRuns.set(agent);
    afterNextRender(() => this.backButton()?.nativeElement.focus(), { injector: this.injector });
  }

  /**
   * Returns to the run configuration whose flow opened the runs, or to the list and the runs action of
   * the agent whose runs were open.
   */
  closeRuns(): void {
    const agentId = this.viewingRuns()?.id;
    this.viewingRuns.set(null);
    if (this.returnToRunConfiguration(agentId)) {
      return;
    }
    if (agentId !== undefined) {
      afterNextRender(() => {
        const action = Array.from(this.host.nativeElement.querySelectorAll<HTMLButtonElement>('[data-runs-agent]'))
          .find(item => item.dataset['runsAgent'] === agentId);
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
    this.returnTo.set(null);
    this.onSaved(agent);
    this.openRunConfiguration(agent, true);
  }

  /** The flow of the agent's run configuration opens the editor; saving or cancelling returns there. */
  editFromRunConfiguration(agent: AdminAgent): void {
    this.configuring.set(null);
    this.returnTo.set(agent.id);
    this.edit(agent);
  }

  /**
   * A consultation of the agent's run configuration opens the editor of an agent that asks it, whose
   * tool selectors are the consultation's second key; saving or cancelling returns to the run
   * configuration it came from.
   */
  askerFromRunConfiguration(agent: AdminAgent, askerId: string): void {
    const asker = this.agents().find(item => item.id === askerId);
    if (!asker) {
      return;
    }
    this.configuring.set(null);
    this.returnTo.set(agent.id);
    this.edit(asker);
  }

  /**
   * A data shadow variant created in the agent's run configuration continues in its own run
   * configuration's guided setup, since it receives the live agent's events only with run settings.
   */
  onVariantCreated(variant: AdminAgent): void {
    this.returnTo.set(null);
    this.onSaved(variant);
    this.openRunConfiguration(variant, true);
  }

  /** The run configuration of a data shadow variant of the agent whose run configuration is open. */
  variantFromRunConfiguration(variantId: string): void {
    const variant = this.agents().find(item => item.id === variantId);
    if (!variant) {
      return;
    }
    this.returnTo.set(null);
    this.openRunConfiguration(variant);
  }

  /** The flow of the agent's run configuration opens its runs; going back returns there. */
  runsFromRunConfiguration(agent: AdminAgent): void {
    this.configuring.set(null);
    this.returnTo.set(agent.id);
    this.openRuns(agent);
  }

  /** The flow of an agent in code opens the form that replaces its technical account on its card. */
  accountFromRunConfiguration(agent: AdminAgent): void {
    this.configuring.set(null);
    this.returnTo.set(null);
    this.refreshAgents();
    this.askAccount(agent, 'set');
  }

  /** The editor saved: back to the run configuration that opened it, or to the list. */
  onEditorSaved(agent: AdminAgent): void {
    this.onSaved(agent);
    this.returnToRunConfiguration(agent.id, agent);
  }

  /** The editor closed without saving: back to the run configuration that opened it, or to the list. */
  onEditorCancelled(): void {
    const agentId = this.editing()?.id;
    this.editing.set(undefined);
    this.returnToRunConfiguration(agentId);
  }

  /**
   * Opens the run configuration again when its flow, or one of its consultations, opened what closes
   * now; `false` otherwise. The editor of an agent that asks a consultation returns to the consulted
   * agent's run configuration.
   */
  private returnToRunConfiguration(agentId: string | undefined, saved?: AdminAgent): boolean {
    const returnTo = this.returnTo();
    this.returnTo.set(null);
    if (returnTo === null || agentId === undefined) {
      return false;
    }
    const agent = saved?.id === returnTo ? saved : this.agents().find(item => item.id === returnTo);
    if (!agent) {
      return false;
    }
    this.openRunConfiguration(agent);
    return true;
  }

  /** What the agent works on, with its object types named as the run catalog names them. */
  worksOn(agent: AdminAgent): WorksOnSummary & { only: 'all' | 'types' | 'application' } {
    const summary = worksOnSummary(agent, this.catalog()?.subjectTypes);
    return {
      ...summary,
      only: summary.all ? 'all' : summary.types.length > 0 ? 'types' : 'application'
    };
  }

  /** The groups the agent is in, named as the run catalog names them; `null` for none. */
  groupNames(agent: AdminAgent): string | null {
    const groups = this.catalog()?.agentGroups ?? [];
    const names = (agent.groups ?? []).map(id => groups.find(group => group.id === id)?.name ?? id);
    return names.length > 0 ? names.join(', ') : null;
  }

  /** The name of an account or person, or the id when the application does not know it. */
  accountName(actorId: string | null | undefined): string {
    return this.accountNameLookup.nameOf(actorId);
  }

  /** The account ID as the tooltip of a name shown in its place. */
  accountTitle(actorId: string | null | undefined): string | null {
    return this.accountNameLookup.idTitleOf(actorId);
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
        return ['paused', 'off', 'stopped'];
      case 'paused':
        return ['active', 'off', 'stopped'];
      case 'off':
        return ['active', 'stopped'];
      case 'stopped':
        return ['active', 'off'];
      default:
        return [];
    }
  }

  /** The state changes a skill allows. */
  skillActions(skill: AgentSkillState): AgentStateAction[] {
    switch (skill.runState) {
      case 'active':
        return ['paused', 'off', 'stopped'];
      case 'paused':
        return ['active', 'off', 'stopped'];
      case 'off':
        return ['active', 'stopped'];
      default:
        return ['active'];
    }
  }

  /**
   * The key of a state change's label, explanation or confirmation: making an agent or skill that is
   * off active again switches it on, which replays nothing, unlike a resume.
   */
  stateKey(part: 'action' | 'explain' | 'confirm', from: string, action: AgentStateAction): string {
    return `nh-assistant.admin.agents.state.${part}.${from === 'off' && action === 'active' ? 'switch-on' : action}`;
  }

  /** The explanation of a state change of one skill. */
  skillStateExplainKey(change: StateChange): string {
    return `nh-assistant.admin.agents.skills.explain.${change.from === 'off' && change.state === 'active' ? 'switch-on' : change.state}`;
  }

  /** Opens the reason form of a state change of the agent or, with a skill, of that skill. */
  askState(agent: AdminAgent, state: AgentStateAction, skill?: AgentSkillState): void {
    this.closeForms();
    this.changingState.set({ agentId: agent.id, skillId: skill?.skillId, state, from: skill?.runState ?? agent.runState });
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

  /**
   * The label of a shadow action. An agent created here has no code: returning to its "code" mode
   * means returning to the mode it was set up with.
   */
  shadowActionKey(agent: AdminAgent, mode: AgentShadowMode): string {
    return mode === 'code' && agent.source !== 'code'
      ? 'nh-assistant.admin.agents.shadow.action.own'
      : `nh-assistant.admin.agents.shadow.action.${mode}`;
  }

  /** The explanation of a shadow change of the agent or one of its skills. */
  shadowExplainKey(agent: AdminAgent, mode: AgentShadowMode, skill: boolean): string {
    const group = skill ? 'skill-explain' : 'explain';
    return `nh-assistant.admin.agents.shadow.${group}.${mode === 'code' && agent.source !== 'code' ? 'own' : mode}`;
  }

  /** The mode the agent, or one of its skills, was set up with: what returning to it means. */
  ownShadowModeKey(agent: AdminAgent, skillId?: string): string {
    const shadow = skillId === undefined
      ? agent.shadow
      : agent.skills?.find(skill => skill.skillId === skillId)?.shadow;
    return shadow?.configured ? 'nh-assistant.admin.agents.shadow.mode.shadow' : 'nh-assistant.admin.agents.shadow.mode.live';
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

  /** A changed account clears the error of the one before. */
  onAccountChange(actorId: string): void {
    this.accountId.set(actorId);
    this.accountError.set(null);
  }

  isChangingAccount(agentId: string, mode: 'set' | 'code'): boolean {
    const change = this.changingAccount();
    return change?.agentId === agentId && change.mode === mode;
  }

  /**
   * Changes the technical account. The form stays open while the server decides, so a missing or
   * refused account shows at the account field, which takes the focus.
   */
  applyAccount(agent: AdminAgent): void {
    const change = this.changingAccount();
    if (change?.agentId !== agent.id || this.busyAgentId() !== null) {
      return;
    }

    const actorId = change.mode === 'set' ? this.accountId().trim() : null;
    if (actorId === '') {
      this.accountError.set({
        code: 'assistant-validation',
        messageKey: nhAssistantErrorMessageKey('assistant-validation'),
        fieldErrors: { actorId: ['required'] }
      });
      this.focusAccount();
      return;
    }

    const reason = this.stateReason().trim();
    this.busyAgentId.set(agent.id);
    this.errorAgentId.set(agent.id);
    this.error.set(null);
    this.accountError.set(null);
    this.actionSubscription?.unsubscribe();
    this.actionSubscription = this.admin.setServiceAccount(agent.id, { actorId, reason: reason.length > 0 ? reason : null }).subscribe({
      next: state => this.replace(withState(agent, state)),
      complete: () => {
        this.busyAgentId.set(null);
        this.changingAccount.set(null);
        this.stateReason.set('');
        this.refreshChat();
        this.loadAccountNames(this.agents());
      },
      error: (error: unknown) => {
        this.busyAgentId.set(null);
        const failure = toNhAssistantError(error);
        if (failure.fieldErrors?.['actorId'] || accountRefusals.has(failure.code)) {
          this.accountError.set(failure);
          this.focusAccount();
        } else {
          this.error.set(failure);
        }
      }
    });
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
    this.accountNameLookup.load(agents.flatMap(agent => [
      agent.serviceAccount?.actorId,
      agent.serviceAccount?.configuredActorId,
      agent.stateChangedBy,
      agent.shadow?.changedBy
    ]));
  }

  /** The run catalog, or why it could not be read; it never errors. */
  private readCatalog(): Observable<CatalogRead> {
    return this.admin.getRunCatalog().pipe(
      map(catalog => ({ catalog, error: null })),
      catchError((error: unknown) => of({ catalog: null, error: toNhAssistantError(error) })));
  }

  private applyCatalog(read: CatalogRead): void {
    this.catalog.set(read.catalog);
    this.catalogError.set(read.error);
    this.accountSearch.set(read.catalog?.accountSearch === true);
  }

  private focusAccount(): void {
    afterNextRender(() => this.host.nativeElement.querySelector<HTMLElement>('[data-account-form] input')?.focus(), { injector: this.injector });
  }

  /** Closes the open confirmation and change forms before another opens; one is open at a time. */
  private closeForms(): void {
    this.confirming.set(null);
    this.error.set(null);
    this.accountError.set(null);
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
    offBeforeStop: state.offBeforeStop,
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
