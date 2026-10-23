import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  computed,
  inject,
  input,
  output,
  signal
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { Subscription, map, startWith } from 'rxjs';
import {
  AdminAgent,
  AgentRunConfiguration,
  AgentRunHistory,
  McpServer,
  NhAssistantAdminApiService,
  NhAssistantError,
  NhAssistantPanelService,
  RunCatalog,
  ToolCatalogEntry,
  ɵnhAssistantCodeKeys as nhAssistantCodeKeys,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantSubjectLinkComponent as NhAssistantSubjectLinkComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';
import {
  ɵNhAssistantRunNowDialogComponent as NhAssistantRunNowDialogComponent,
  type ɵNhAssistantRunNowAgent as NhAssistantRunNowAgent
} from '@newheap/platform-ai-chat/agent-runs';
import { codeTextKeys, policyKeys, readableId, runNowAgentOf, skillDescriptionKeys, skillNameKeys, worksOnSummary } from '../agent-texts';
import {
  AgentFlowEntry,
  AgentFlowServerUse,
  AgentFlowStepId,
  AgentFlowWarning,
  agentFlowSteps,
  flowAccountability,
  flowRunNow,
  flowServers,
  flowTools,
  flowTriggers,
  problemStep,
  problemWarning,
  typeName,
  unavailableServerUses
} from './agent-flow';
import { NhAssistantAccountLabelComponent } from '../../nh-assistant-account-names';
import { consultationRows, consultationsClosed } from './consultations';
import { describePolicy } from './run-config-shared';

let nextId = 0;

/** The most recent runs the flow shows; the runs view lists the rest. */
const latestRunCount = 3;

/** One approval policy with its sentence, of the agent or of one of its skills. */
interface FlowPolicy {
  /** The skill whose own policy it is; `null` for the agent's. */
  skillId: string | null;
  /** The policy's name; `null` for the application's default. */
  name: string | null;
  sentence: string | null;
}

/**
 * The flow view of an agent at the top of its run configuration: what it works on, when it runs (its
 * triggers with what each runs for, run now, and the consultations through which other assistants
 * consult it), how it works (its way of working, skills and tools,
 * with where a run's proposals of each tool wait and whether its MCP servers are used in runs without
 * a person), whom it answers to, who approves its proposals, and its results (its groups and latest
 * runs). Problems show at the step they concern with the entry point that fixes them. The flow edits
 * nothing: every entry point opens a form of the run configuration, or asks the page that hosts it to
 * open the agent editor, the technical account, the runs view or the MCP servers (`entry`). Run now
 * opens the run-now dialog here.
 */
@Component({
  selector: 'nh-assistant-agent-flow',
  standalone: true,
  imports: [
    DatePipe,
    TranslatePipe,
    NhAssistantTranslatePipe,
    NhAssistantIconComponent,
    NhAssistantSubjectLinkComponent,
    NhAssistantRunNowDialogComponent,
    NhAssistantAccountLabelComponent
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-agent-flow.component.html',
  styleUrls: ['../../nh-assistant-admin-shared.scss', './run-config-forms.scss', './nh-assistant-agent-flow.component.scss']
})
export class NhAssistantAgentFlowComponent implements OnInit {
  private readonly admin = inject(NhAssistantAdminApiService);
  private readonly panel = inject(NhAssistantPanelService);
  private readonly translate = inject(TranslateService);
  private runsSubscription?: Subscription;

  readonly agent = input.required<AdminAgent>();
  readonly configuration = input.required<AgentRunConfiguration>();
  readonly catalog = input<RunCatalog | null>(null);
  /** The tool catalog; without it the agent's tools are listed by their selectors. */
  readonly tools = input<readonly ToolCatalogEntry[]>([]);
  /** The MCP servers; `null` while unknown, so nothing reads as missing. */
  readonly servers = input<readonly McpServer[] | null>(null);
  /** The name of the account the agent answers to, once known. */
  readonly accountName = input<string | null>(null);
  /** An action of the run configuration is busy; the entry points wait. */
  readonly busy = input(false);
  /** An entry point asks to open the form or view it summarizes. */
  readonly entry = output<AgentFlowEntry>();

  readonly id = `nh-assistant-agent-flow-${nextId++}`;
  readonly steps = agentFlowSteps;
  readonly skillNameKeys = skillNameKeys;
  readonly skillDescriptionKeys = skillDescriptionKeys;
  readonly policyKeys = policyKeys;
  readonly readableId = readableId;
  readonly codeKeys = nhAssistantCodeKeys;

  readonly runs = signal<AgentRunHistory | null>(null);
  readonly runsError = signal<NhAssistantError | null>(null);
  readonly runsErrorKeys = computed(() => nhAssistantErrorKeys(this.runsError()));
  readonly runsLoading = signal(false);
  /** The run-now dialog is open. */
  readonly runningNow = signal(false);

  private readonly language = toSignal(
    this.translate.onLangChange.pipe(map(event => event.lang), startWith(this.translate.currentLang)),
    { initialValue: this.translate.currentLang });

  readonly isAdmin = computed(() => this.configuration().source === 'admin');
  /** An agent created here gets skills and triggers once it has run settings. */
  readonly canAddRunData = computed(() => !this.isAdmin() || this.configuration().runSettings !== null);
  readonly worksOn = computed(() => {
    const summary = worksOnSummary(this.agent(), this.catalog()?.subjectTypes);
    return { ...summary, only: summary.all ? 'all' : summary.types.length > 0 ? 'types' : 'application' };
  });
  readonly triggers = computed(() => flowTriggers(this.configuration(), this.catalog(), this.agent()));
  /** When another assistant consults the agent: its consultations in code and as data. */
  readonly consultations = computed(() => consultationRows(this.configuration()));
  /** Administrators may add a consultation to the agent now. */
  readonly canAddConsultation = computed(() => consultationsClosed(this.configuration(), this.catalog(), this.agent()) === null);
  readonly runNow = computed(() => flowRunNow(this.agent(), this.configuration(), this.catalog()));
  /**
   * What people run the agent for by hand, as the run-now dialog offers it: the whole application (one
   * division at a time for an agent that works per division) and the objects it works on.
   */
  readonly runNowScopeKey = computed(() => {
    const works = this.worksOn();
    const objects = works.all || works.types.length > 0;
    const base = 'nh-assistant.admin.run-config.flow.when.scope.';
    if (!works.application) {
      return `${base}objects`;
    }
    return `${base}${works.perDivision ? 'division' : 'application'}${objects ? '-and-objects' : ''}`;
  });
  readonly runNowAgent = computed<NhAssistantRunNowAgent>(() => {
    this.language();
    return runNowAgentOf(this.agent(), this.agentName());
  });
  readonly agentName = computed(() => {
    this.language();
    const agent = this.agent();
    return this.translated(codeTextKeys(agent.source, agent.displayName), agent.displayName);
  });
  readonly accountability = computed(() => flowAccountability(this.configuration()));
  /** The way of working of the agent: fixed rules (named for an agent created here) or the model. */
  readonly wayOfWorking = computed(() => {
    const configuration = this.configuration();
    if (configuration.source === 'admin') {
      const settings = configuration.runSettings;
      if (!settings) {
        return null;
      }
      const rules = settings.ruleHandler;
      return rules
        ? { kind: 'rules' as const, name: rules, description: this.catalog()?.ruleHandlers.find(block => block.name === rules)?.description ?? null }
        : { kind: 'model' as const, name: null, description: null };
    }
    return configuration.codeRunSettings?.usesRules
      ? { kind: 'code-rules' as const, name: null, description: null }
      : { kind: 'model' as const, name: null, description: null };
  });
  readonly agentTools = computed(() => flowTools(this.configuration().toolSelectors, this.tools(), this.catalog()));
  readonly agentServers = computed(() => flowServers(this.agent().mcpServerIds, this.servers(), this.accountability()));
  /** The agent's approval policy and the skills' own, each with its sentence. */
  readonly policies = computed<FlowPolicy[]>(() => {
    this.language();
    const configuration = this.configuration();
    const policies: FlowPolicy[] = [];
    // The run settings as data, or how an agent in code runs; neither while an agent created here has none.
    const settings = configuration.source === 'admin' ? configuration.runSettings : configuration.codeRunSettings;
    if (settings) {
      policies.push({ skillId: null, name: settings.approvalPolicy, sentence: this.sentence(settings.approvalPolicy) });
    }
    for (const skill of configuration.dataSkills) {
      if (skill.approvalPolicy) {
        policies.push({ skillId: skill.skillId, name: skill.approvalPolicy, sentence: this.sentence(skill.approvalPolicy) });
      }
    }
    return policies;
  });
  /** The names of the groups the agent is in, as the run catalog names them. */
  readonly groups = computed(() => {
    const groups = this.catalog()?.agentGroups ?? [];
    return (this.agent().groups ?? []).map(id => groups.find(group => group.id === id)?.name ?? id);
  });
  /** Whether the agent can be put in groups: every agent except a shadow variant. */
  readonly groupable = computed(() => !this.agent().shadowOf);
  /** The warnings of each step: problems of the run configuration and what keeps the agent from working. */
  readonly warnings = computed<Record<AgentFlowStepId, AgentFlowWarning[]>>(() => {
    const configuration = this.configuration();
    const result: Record<AgentFlowStepId, AgentFlowWarning[]> = {
      'works-on': [],
      when: [],
      skills: [],
      account: [],
      approval: [],
      results: []
    };
    for (const problem of configuration.problems) {
      result[problemStep(problem)].push(problemWarning(problem, configuration, this.catalog()));
    }
    for (const selector of this.agentTools().unmatched) {
      result.skills.push({
        id: `tool:${selector}`,
        key: 'nh-assistant.admin.run-config.flow.skills.tool-not-offered',
        params: { selector },
        entry: { kind: 'edit-agent' },
        entryKey: 'nh-assistant.admin.run-config.flow.entry.tools'
      });
    }
    for (const server of this.agentServers()) {
      if (unavailableServerUses.has(server.use)) {
        result.skills.push(this.serverWarning(server.id, server.name, server.use));
      }
    }
    if (configuration.source === 'admin' && configuration.runSettings === null) {
      result.account.push({
        id: 'no-settings',
        key: 'nh-assistant.admin.run-config.flow.account.no-settings',
        entry: { kind: 'setup' },
        entryKey: 'nh-assistant.admin.run-config.setup.start'
      });
    }
    return result;
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => this.runsSubscription?.unsubscribe());
  }

  ngOnInit(): void {
    this.loadRuns();
  }

  /** Reads the agent's latest runs; a failure shows with its code and a retry in the results step. */
  loadRuns(): void {
    this.runsLoading.set(true);
    this.runsError.set(null);
    this.runsSubscription?.unsubscribe();
    this.runsSubscription = this.admin.getAgentRuns(this.agent().id, { page: 1, itemsPerPage: latestRunCount }).subscribe({
      next: runs => {
        this.runs.set(runs);
        this.runsLoading.set(false);
      },
      error: (error: unknown) => {
        this.runsError.set(toNhAssistantError(error));
        this.runsLoading.set(false);
      }
    });
  }

  open(entry: AgentFlowEntry): void {
    this.entry.emit(entry);
  }

  openRunNow(): void {
    this.runningNow.set(true);
  }

  /** The run-now dialog closed; its run may be among the latest now. */
  onRunNowClosed(): void {
    this.runningNow.set(false);
    this.loadRuns();
  }

  openConversation(conversationId: string): void {
    this.panel.open(conversationId);
  }

  /** A skill's name: the application's translation, or a readable form of its id. */
  skillName(skillId: string | null | undefined): string {
    return skillId ? this.translated(skillNameKeys(this.agent().id, skillId), readableId(skillId)) : '';
  }

  /** The name of an object type, as the run catalog names it in the reader's language. */
  typeName(subjectType: string): string {
    return typeName(this.catalog(), subjectType);
  }

  /** The text of a step's warning count. */
  warningCountKey(count: number): string {
    return count === 1 ? 'nh-assistant.admin.run-config.flow.attention-one' : 'nh-assistant.admin.run-config.flow.attention';
  }

  private serverWarning(id: string, name: string, use: AgentFlowServerUse): AgentFlowWarning {
    const base = { id: `server:${id}`, key: `nh-assistant.admin.run-config.flow.servers.warning.${use}`, params: { server: name } };
    switch (use) {
      case 'missing':
        return { ...base, entry: { kind: 'edit-agent' }, entryKey: 'nh-assistant.admin.run-config.flow.entry.tools' };
      case 'personal-never-account':
      case 'personal-never-agent':
        // Delegating to the person who starts a run lets the server serve that person.
        return this.isAdmin() && this.configuration().runSettings
          ? { ...base, entry: { kind: 'settings' }, entryKey: 'nh-assistant.admin.run-config.flow.entry.settings' }
          : { ...base, entry: { kind: 'mcp-servers' }, entryKey: 'nh-assistant.admin.run-config.flow.entry.mcp-servers' };
      default:
        return { ...base, entry: { kind: 'mcp-servers' }, entryKey: 'nh-assistant.admin.run-config.flow.entry.mcp-servers' };
    }
  }

  private sentence(policy: string | null): string | null {
    const summary = this.catalog()?.approvalPolicySummaries?.find(item => item.name === policy);
    return summary ? describePolicy(summary, (key, params) => this.translate.instant(key, params) as string) : null;
  }

  /** The first of the keys with a translation, or the fallback. */
  private translated(keys: readonly string[], fallback: string): string {
    for (const key of keys) {
      const value = this.translate.instant(key) as unknown;
      if (typeof value === 'string' && value.length > 0 && value !== key) {
        return value;
      }
    }
    return fallback;
  }
}
