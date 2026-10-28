import { DatePipe, DecimalPipe, NgTemplateOutlet } from '@angular/common';
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
  model,
  output,
  signal,
  viewChild
} from '@angular/core';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { Observable, Subscription } from 'rxjs';
import {
  AdminAgent,
  McpServer,
  NhAssistantAdminApiService,
  NhAssistantError,
  NhAssistantPanelService,
  NhAssistantStore,
  RunCatalog,
  ShadowVariantPromotion,
  ToolCatalogEntry,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';
import {
  ɵNhAssistantRunNowDialogComponent as NhAssistantRunNowDialogComponent,
  type ɵNhAssistantRunNowAgent as NhAssistantRunNowAgent
} from '@newheap/platform-ai-chat/agent-runs';
import { NhAssistantAccountNames } from '../../nh-assistant-account-names';
import { NH_ASSISTANT_AGENT_TABS, NhAssistantAgentTab } from '../../nh-assistant-admin-location';
import {
  agentDisplayName,
  codeTextKeys,
  policyKeys,
  runNowAgentOf,
  warningKeys,
  WorksOnSummary,
  worksOnSummary
} from '../agent-texts';
import { agentWork, hasRunConfiguration, offeredInChat, runsOnItsOwn, skillsInShadow } from '../agent-operations';
import { NhAssistantAdminAgentEditorComponent, NhAssistantAgentEditorSection, nhAssistantSelectorMatches } from '../nh-assistant-admin-agent-editor.component';
import { NhAssistantAgentMetadataComponent } from '../metadata/nh-assistant-agent-metadata.component';
import { NhAssistantAgentCompositionActivityComponent } from '../nh-assistant-agent-composition-activity.component';
import { NhAssistantAgentFeedbackReportComponent } from '../nh-assistant-agent-feedback-report.component';
import { NhAssistantAgentRunHistoryComponent } from '../nh-assistant-agent-run-history.component';
import { NhAssistantAgentRunConfigurationComponent, NhAssistantRunConfigurationSection } from '../nh-assistant-agent-run-configuration.component';
import { NhAssistantAgentHandOffsComponent } from '../run-config/nh-assistant-agent-hand-offs.component';
import { AgentFlowEntry } from '../run-config/agent-flow';
import { NhAssistantPackageLockComponent } from '../agent-packages/nh-assistant-package-lock.component';
import { agentDefinitionLockOf, agentPackageOf, isPackageLocked } from '../agent-packages/package-parts';
import { NhAssistantAgentAccountControlComponent } from './nh-assistant-agent-account-control.component';
import { NhAssistantAgentRolloutComponent, RolloutReview } from './nh-assistant-agent-rollout.component';
import { NhAssistantAgentStateControlComponent } from './nh-assistant-agent-state-control.component';
import { startNhAssistantAdminTranslations } from '../../i18n/nh-assistant-admin-translations';

/** A section of Build, which its index scrolls to. */
export type AgentBuildSection = 'profile' | 'tools' | 'work' | 'skills' | 'triggers' | 'connected' | 'guardrails';

let nextId = 0;

/**
 * One agent's page. A fixed header names the agent, says where it comes from and holds what an
 * administrator does with it now: its status with the changes it allows, "Test in chat" for an agent
 * offered in the chat, "Run now" for an agent that runs on its own, and "More" with export as a
 * package, reset to code and delete. Below it four tabs:
 *
 * - Overview: the agent's flow from what it works on to its results, its status in detail and its
 *   technical account, or a summary for an agent that only chats.
 * - Build: one page of sections, each a summary that opens its form: the profile, the tools, the
 *   guided setup of an agent that only chats, the skills, the triggers, the agents it is connected to
 *   by consultations and hand-offs, and its guardrails.
 * - Rollout: everything that is not live yet, with its shadow variants and their evaluation.
 * - Activity: the agent's runs, the feedback on its proposals and what its compositions and scripts ran.
 *
 * The flow's entry points open their section of Build; a candidate in Rollout opens its comparison
 * there. Chat-only agents have no rollout and no runs.
 */
@Component({
  selector: 'nh-assistant-agent-page',
  standalone: true,
  imports: [
    DatePipe,
    DecimalPipe,
    NgTemplateOutlet,
    TranslatePipe,
    NhAssistantTranslatePipe,
    NhAssistantIconComponent,
    NhAssistantAdminAgentEditorComponent,
    NhAssistantAgentRunConfigurationComponent,
    NhAssistantAgentHandOffsComponent,
    NhAssistantAgentRunHistoryComponent,
    NhAssistantAgentFeedbackReportComponent,
    NhAssistantAgentCompositionActivityComponent,
    NhAssistantPackageLockComponent,
    NhAssistantRunNowDialogComponent,
    NhAssistantAgentAccountControlComponent,
    NhAssistantAgentRolloutComponent,
    NhAssistantAgentStateControlComponent,
    NhAssistantAgentMetadataComponent
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-agent-page.component.html',
  styleUrls: ['../../nh-assistant-admin-shared.scss', './agent-page.scss']
})
export class NhAssistantAgentPageComponent {
  private readonly admin = inject(NhAssistantAdminApiService);
  private readonly store = inject(NhAssistantStore);
  private readonly panel = inject(NhAssistantPanelService);
  private readonly translate = inject(TranslateService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);
  private readonly buildConfiguration = viewChild<NhAssistantAgentRunConfigurationComponent>('buildConfiguration');
  private readonly accountControl = viewChild(NhAssistantAgentAccountControlComponent);
  private actionSubscription?: Subscription;

  readonly agent = input.required<AdminAgent>();
  readonly agents = input<readonly AdminAgent[]>([]);
  readonly tools = input<readonly ToolCatalogEntry[]>([]);
  readonly servers = input<readonly McpServer[]>([]);
  readonly catalog = input<RunCatalog | null>(null);
  /** The run catalog could not be read: what depends on it is not offered. */
  readonly catalogUnavailable = input(false);
  readonly accountSearch = input(false);
  readonly names = input.required<NhAssistantAccountNames>();
  /** The open tab. */
  readonly tab = model<NhAssistantAgentTab>('overview');
  /** Opens Build with the guided setup, for an agent that was just created to work on its own. */
  readonly startSetup = input(false);

  /** Back to the list of agents. */
  readonly back = output<void>();
  /** The agent changed: its definition, state, skills or technical account. */
  readonly agentChanged = output<AdminAgent>();
  /** The agent was deleted. */
  readonly agentRemoved = output<string>();
  /** The agents should be read again, for example after a change of the run configuration. */
  readonly refreshRequested = output<void>();
  /** Opens the page of another agent, such as one that asks a consultation or a shadow variant. */
  readonly openAgent = output<string>();
  readonly openMcpServers = output<void>();
  readonly exportRequested = output<AdminAgent>();
  readonly variantCreated = output<AdminAgent>();
  readonly variantPromoted = output<ShadowVariantPromotion>();

  readonly id = `nh-assistant-agent-page-${nextId++}`;
  readonly codeTextKeys = codeTextKeys;
  readonly warningKeys = warningKeys;
  readonly policyKeys = policyKeys;
  readonly agentPackageOf = agentPackageOf;

  /** The part of Build whose editor is open; one at a time. */
  readonly editing = signal<NhAssistantAgentEditorSection | null>(null);
  /** The section of Build its index last went to. */
  readonly currentSection = signal<AgentBuildSection>('profile');
  readonly activity = signal<'runs' | 'feedback' | 'compositions'>('runs');
  readonly moreOpen = signal(false);
  readonly confirming = signal<'reset' | 'delete' | null>(null);
  readonly busy = signal(false);
  readonly error = signal<NhAssistantError | null>(null);
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));
  readonly runningNow = signal(false);
  /** The agent is not among the chat's agents of the person: the chat cannot try it. */
  readonly testUnavailable = signal(false);

  readonly name = computed(() => agentDisplayName(this.translate, this.agent()));
  readonly work = computed(() => agentWork(this.agent()));
  readonly hasRunConfiguration = computed(() => hasRunConfiguration(this.agent()));
  readonly runs = computed(() => runsOnItsOwn(this.agent()));
  /** The activity shown: an agent that does not run on its own has no runs, so it opens at the feedback. */
  readonly shownActivity = computed(() => this.activity() === 'runs' && !this.runs() ? 'feedback' : this.activity());
  readonly tabs = computed<NhAssistantAgentTab[]>(() => NH_ASSISTANT_AGENT_TABS.filter(tab => tab !== 'rollout' || this.runs()));
  readonly selectedTab = computed<NhAssistantAgentTab>(() => this.tabs().includes(this.tab()) ? this.tab() : 'overview');
  /** What is not live yet, as Rollout lists it, for the count on its tab: the agent's skills in shadow. */
  readonly rolloutCount = computed(() => skillsInShadow(this.agent()).length);
  readonly canTestInChat = computed(() => offeredInChat(this.agent()) && this.agent().runState !== 'stopped');
  /**
   * Whether the agent can be run now: it runs on its own, it is neither stopped nor disabled in code,
   * and the run catalog names what it can run on. An agent that is off still runs for a person.
   */
  readonly canRunNow = computed(() => {
    const agent = this.agent();
    return this.catalog() !== null
      && this.runs()
      && (agent.runState === 'active' || agent.runState === 'paused' || agent.runState === 'off');
  });
  readonly runNowAgent = computed<NhAssistantRunNowAgent>(() => runNowAgentOf(this.agent(), this.name()));
  readonly canReset = computed(() => this.agent().source === 'code' && this.agent().isOverridden);
  readonly resetLocked = computed(() => isPackageLocked(agentDefinitionLockOf(this.agent())));
  readonly canDelete = computed(() => this.agent().source === 'admin');
  readonly deleteLocked = computed(() => agentPackageOf(this.agent()) !== null);
  readonly worksOn = computed<WorksOnSummary & { only: 'all' | 'types' | 'application' }>(() => {
    const summary = worksOnSummary(this.agent(), this.catalog()?.subjectTypes);
    return { ...summary, only: summary.all ? 'all' : summary.types.length > 0 ? 'types' : 'application' };
  });
  readonly groupNames = computed(() => {
    const groups = this.catalog()?.agentGroups ?? [];
    const names = (this.agent().groups ?? []).map(id => groups.find(group => group.id === id)?.name ?? id);
    return names.length > 0 ? names.join(', ') : null;
  });
  readonly variantOf = computed(() => {
    const parent = this.agent().shadowOf;
    return parent ? this.agents().find(item => item.id === parent) ?? null : null;
  });
  /** The tools the agent's selectors choose from the catalog, without those of its MCP servers. */
  readonly matchedTools = computed(() => {
    const selectors = this.agent().toolSelectors;
    return this.tools().filter(tool => tool.source !== 'mcp' && selectors.some(selector => nhAssistantSelectorMatches(tool.id, selector)));
  });
  readonly changingTools = computed(() => this.matchedTools().filter(tool => tool.effect !== 'read-only').length);
  readonly agentServers = computed(() => this.agent().mcpServerIds.map(id => this.servers().find(server => server.id === id)?.displayName ?? id));
  /** The run configuration's parts that Build shows: a chat-only agent has no triggers. */
  readonly buildSections = computed<NhAssistantRunConfigurationSection[]>(() =>
    this.agent().chatOnly ? ['skills', 'consultations', 'settings'] : ['skills', 'triggers', 'consultations', 'settings']);
  readonly flowSections: NhAssistantRunConfigurationSection[] = ['flow'];
  readonly profileSections: NhAssistantAgentEditorSection[] = ['profile'];
  readonly toolSections: NhAssistantAgentEditorSection[] = ['tools'];
  readonly accessSections: NhAssistantAgentEditorSection[] = ['access'];
  /** The sections of Build in their order, for its index. */
  readonly indexSections = computed<AgentBuildSection[]>(() => {
    if (!this.hasRunConfiguration()) {
      return ['profile', 'tools', 'guardrails'];
    }
    const sections: AgentBuildSection[] = ['profile', 'tools', 'skills'];
    if (!this.agent().chatOnly) {
      sections.push('triggers');
    }
    return [...sections, 'connected', 'guardrails'];
  });

  constructor() {
    startNhAssistantAdminTranslations();
    inject(DestroyRef).onDestroy(() => this.actionSubscription?.unsubscribe());
  }

  select(tab: NhAssistantAgentTab): void {
    this.tab.set(tab);
    this.moreOpen.set(false);
  }

  /** Arrow keys, Home and End move between tabs as the ARIA tabs pattern describes. */
  onTabKeydown(event: KeyboardEvent): void {
    const tabs = this.tabs();
    const index = tabs.indexOf(this.selectedTab());
    const last = tabs.length - 1;
    const targets: Record<string, number> = {
      ArrowRight: index === last ? 0 : index + 1,
      ArrowLeft: index === 0 ? last : index - 1,
      Home: 0,
      End: last
    };
    const next = targets[event.key];
    if (next === undefined) {
      return;
    }

    event.preventDefault();
    this.select(tabs[next]);
    this.host.nativeElement.querySelector<HTMLElement>(`#${this.id}-tab-${tabs[next]}`)?.focus();
  }

  /** Scrolls Build to one of its sections and moves the focus to its heading. */
  goTo(section: AgentBuildSection): void {
    this.currentSection.set(section);
    const target = this.host.nativeElement.querySelector<HTMLElement>(`[data-build-section="${section}"]`);
    target?.scrollIntoView({ block: 'start' });
    const heading = target?.querySelector<HTMLElement>('h4');
    if (heading) {
      heading.tabIndex = -1;
      heading.focus({ preventScroll: true });
    }
  }

  /** Opens the editor of one part of the agent in its section of Build. */
  edit(section: NhAssistantAgentEditorSection): void {
    this.editing.set(section);
    this.focusIn(`[data-build-section="${section === 'access' ? 'guardrails' : section}"] input:not([disabled]), [data-build-section="${section === 'access' ? 'guardrails' : section}"] select`);
  }

  onEdited(agent: AdminAgent): void {
    this.editing.set(null);
    this.agentChanged.emit(agent);
    // The run configuration holds the agent's tool selectors for its skill forms and guided setup.
    this.buildConfiguration()?.load();
    void this.store.reloadStatus();
  }

  /**
   * A flow entry of the overview opens its form in Build; the editor's parts, the technical account
   * and the runs open their own places.
   */
  onEntry(entry: AgentFlowEntry): void {
    switch (entry.kind) {
      case 'edit-agent':
        this.openBuild('profile', () => this.edit('profile'));
        return;
      case 'edit-tools':
        this.openBuild('tools', () => this.edit('tools'));
        return;
      case 'edit-access':
        this.openBuild('guardrails', () => this.edit('access'));
        return;
      default:
        this.openBuild(null, () => this.buildConfiguration()?.openEntry(entry));
    }
  }

  /** The technical account of an agent in code is changed in its guardrails. */
  changeAccount(): void {
    this.openBuild('guardrails', () => this.accountControl()?.ask('set'));
  }

  openRuns(): void {
    this.activity.set('runs');
    this.select('activity');
  }

  /** A candidate of Rollout opens its comparison in Build. */
  onReview(review: RolloutReview): void {
    const entry: AgentFlowEntry = review.section === 'skills'
      ? { kind: 'compare-skill', skillId: review.id }
      : { kind: 'compare-trigger', triggerId: review.id };
    this.openBuild(null, () => this.buildConfiguration()?.openEntry(entry));
  }

  /** Opens the panel with the agent in a new conversation, so the administrator tries it as people do. */
  testInChat(): void {
    const agent = this.agent();
    this.testUnavailable.set(false);
    // The chat may not have started yet on an administration page; its agents are those of the person.
    void this.store.initialize().then(() => this.store.reloadStatus()).then(() => {
      if (!this.store.agents().some(item => item.id === agent.id)) {
        this.testUnavailable.set(true);
        return;
      }
      this.store.selectAgent(agent.id);
      this.store.startNewConversation();
      this.panel.open();
    });
  }

  openConversation(conversationId: string): void {
    this.panel.open(conversationId);
  }

  toggleMore(): void {
    this.moreOpen.update(open => !open);
    this.confirming.set(null);
    this.error.set(null);
    if (this.moreOpen()) {
      this.focusIn('[data-more-menu] .menu-item:not([disabled])');
    }
  }

  closeMore(): void {
    if (this.moreOpen() || this.confirming() !== null) {
      this.moreOpen.set(false);
      this.confirming.set(null);
      this.focusIn('[data-more]');
    }
  }

  exportPackage(): void {
    this.moreOpen.set(false);
    this.exportRequested.emit(this.agent());
  }

  confirm(action: 'reset' | 'delete'): void {
    this.moreOpen.set(false);
    this.confirming.set(action);
    this.focusIn(`[data-confirm="${action}"]`);
  }

  reset(): void {
    this.run(this.admin.resetAgent(this.agent().id), agent => this.agentChanged.emit(agent));
  }

  remove(): void {
    const agentId = this.agent().id;
    this.run<void>(this.admin.deleteAgent(agentId), () => this.agentRemoved.emit(agentId));
  }

  onStateChanged(agent: AdminAgent): void {
    this.agentChanged.emit(agent);
    void this.store.reloadStatus();
  }

  private run<T>(request: Observable<T>, done: (result: T) => void): void {
    this.busy.set(true);
    this.error.set(null);
    this.actionSubscription?.unsubscribe();
    this.actionSubscription = request.subscribe({
      next: result => {
        this.confirming.set(null);
        done(result);
      },
      complete: () => {
        this.busy.set(false);
        void this.store.reloadStatus();
      },
      error: (error: unknown) => {
        this.busy.set(false);
        this.error.set(toNhAssistantError(error));
      }
    });
  }

  /** Opens Build, at a section when one is given, and runs what follows once it rendered. */
  private openBuild(section: AgentBuildSection | null, then: () => void): void {
    this.select('build');
    afterNextRender(() => {
      if (section !== null) {
        this.goTo(section);
      }
      then();
    }, { injector: this.injector });
  }

  value(event: Event): string {
    return (event.target as HTMLSelectElement).value;
  }

  private focusIn(selector: string): void {
    afterNextRender(() => this.host.nativeElement.querySelector<HTMLElement>(selector)?.focus(), { injector: this.injector });
  }
}
