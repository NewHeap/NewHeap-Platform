import { DatePipe, DecimalPipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  OnInit,
  afterNextRender,
  computed,
  inject,
  input,
  output,
  signal
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { Observable, Subscription, forkJoin, map, startWith } from 'rxjs';
import {
  AdminAgent,
  AgentRunConfiguration,
  DataConsultation,
  DataSkill,
  DataSkillVersion,
  DataTrigger,
  DataTriggerVersion,
  McpServer,
  NhAssistantAdminApiService,
  NhAssistantError,
  RunCatalog,
  SkillComparison,
  ToolCatalogEntry,
  TriggerComparison,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantPickerOption as NhAssistantPickerOption,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';
import { NhAssistantAccountLabelComponent, NhAssistantAccountNames, NhAssistantAccountNamesErrorComponent } from '../nh-assistant-account-names';
import { agentDisplayName, policyKeys, readableId, skillDescriptionKeys, skillNameKeys, warningKeys } from './agent-texts';
import { NhAssistantPackageLockComponent } from './agent-packages/nh-assistant-package-lock.component';
import { isPackageLocked, packageLockOf } from './agent-packages/package-parts';
import { NhAssistantAgentConsultationRoutesComponent } from './nh-assistant-agent-consultation-routes.component';
import { AgentFlowEntry, flowAccountability, typeName } from './run-config/agent-flow';
import { ConsultationRow, agentApprovalPolicy, consultationRows, consultationsClosed, showsConsultations } from './run-config/consultations';
import { ConsultationFormSaved, NhAssistantConsultationFormComponent } from './run-config/nh-assistant-consultation-form.component';
import { NhAssistantAgentFlowComponent } from './run-config/nh-assistant-agent-flow.component';
import { NhAssistantAgentWorkSetupComponent } from './run-config/nh-assistant-agent-work-setup.component';
import { NhAssistantRunSettingsFormComponent } from './run-config/nh-assistant-run-settings-form.component';
import { NhAssistantSkillFormComponent, SkillFormSaved } from './run-config/nh-assistant-skill-form.component';
import { NhAssistantTriggerFormComponent } from './run-config/nh-assistant-trigger-form.component';
import { describePolicy, runConfigLimits } from './run-config/run-config-shared';
import { TriggerChange, isConflict, triggerChanges, triggerFieldKeys, versionSummary } from './run-config/trigger-versions';

let nextId = 0;

type Form = 'setup' | 'settings' | 'skill' | 'trigger' | 'consultation';

/** A removal, promotion or discard that waits for its confirmation. */
interface Confirmation {
  action: 'remove-settings' | 'remove-skill' | 'remove-trigger' | 'remove-consultation' | 'promote' | 'discard' | 'promote-trigger' | 'discard-trigger';
  id: string;
}

/**
 * The run configuration of one agent as data: the run settings of an agent created in the
 * administration, its skills and triggers in code (read only) and as data, and the data that no
 * longer fits the code. It opens with the agent's flow: what it works on, when it runs, how it works,
 * whom it answers to, who approves and its results, each step with its problems and an entry point
 * to the form below that changes it; the agent editor (`editAgent`), the technical account of an
 * agent in code (`changeAccount`), the runs view (`openRuns`) and the MCP servers (`openMcpServers`)
 * belong to the page that hosts it. An agent created here that only chats gets a guided setup that
 * lets it work on its own. Administrators add and change data skills, live at once or as a candidate in shadow that
 * they compare with the live version and promote or discard; they add data triggers on the
 * application's events or on a schedule and put them live; a schedule without a subject set runs for
 * the whole application, or for every division of an agent that works per division. New skills and
 * triggers start in shadow; a change of a trigger applies at once or fires as a candidate in shadow next
 * to the live version, which they compare, promote or discard, and every trigger shows its history.
 * `changed` tells the agents page to reload the agent's skill states. Its
 * consultations (AC20) show what other assistants may consult it about: those in code read only, and
 * those defined as data, which administrators add, change and remove within the agent's ceiling in
 * code, each with the agents that ask it (`openAgent` opens the editor of one) and its problems, and
 * switch on or off (a new one may start off), followed by the switches that turn a route off for all of
 * its consultations.
 */
@Component({
  selector: 'nh-assistant-agent-run-configuration',
  standalone: true,
  imports: [
    DatePipe,
    DecimalPipe,
    TranslatePipe,
    NhAssistantTranslatePipe,
    NhAssistantIconComponent,
    NhAssistantAgentFlowComponent,
    NhAssistantAgentWorkSetupComponent,
    NhAssistantRunSettingsFormComponent,
    NhAssistantSkillFormComponent,
    NhAssistantTriggerFormComponent,
    NhAssistantConsultationFormComponent,
    NhAssistantAccountNamesErrorComponent,
    NhAssistantAccountLabelComponent,
    NhAssistantAgentConsultationRoutesComponent,
    NhAssistantPackageLockComponent
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-agent-run-configuration.component.html',
  styleUrls: ['../nh-assistant-admin-shared.scss', './nh-assistant-agent-feedback-report.component.scss', './run-config/run-config-forms.scss']
})
export class NhAssistantAgentRunConfigurationComponent implements OnInit {
  private readonly admin = inject(NhAssistantAdminApiService);
  private readonly translate = inject(TranslateService);
  private readonly injector = inject(Injector);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private loadSubscription?: Subscription;
  private actionSubscription?: Subscription;
  private detailSubscription?: Subscription;
  /** The flow's entry point that opened the form, which takes the focus back when the form closes. */
  private returnFocus: HTMLElement | null = null;

  readonly agent = input.required<AdminAgent>();
  /** Opens the guided setup at once, for an agent that was just created to work on its own. */
  readonly startSetup = input(false);
  /** The tool catalog, to show what the agent's tool selectors offer in the skill forms and the flow. */
  readonly tools = input<readonly ToolCatalogEntry[]>([]);
  /**
   * The MCP servers, so the flow shows whether the agent's servers are used in runs without a person;
   * `null` while unknown.
   */
  readonly servers = input<readonly McpServer[] | null>(null);
  /** Every agent of the administration, to name the agents that ask the agent's consultations. */
  readonly agents = input<readonly AdminAgent[]>([]);
  /** A change of the run configuration changed the agent's skills or state. */
  readonly changed = output<void>();
  /** The flow asks to open the agent editor: what it works on, its tools, MCP servers and groups. */
  readonly editAgent = output<void>();
  /** The flow asks to change the technical account of an agent in code. */
  readonly changeAccount = output<void>();
  /** The flow asks to open the agent's runs. */
  readonly openRuns = output<void>();
  /** The flow asks to open the MCP servers, whose background use decides what runs without a person use. */
  readonly openMcpServers = output<void>();
  /** Opens the editor of an agent that asks one of the agent's consultations, by its id. */
  readonly openAgent = output<string>();

  readonly id = `nh-assistant-run-configuration-${nextId++}`;
  readonly maxReason = runConfigLimits.reason;
  readonly skillNameKeys = skillNameKeys;
  readonly warningKeys = warningKeys;
  readonly skillDescriptionKeys = skillDescriptionKeys;
  readonly readableId = readableId;

  readonly loading = signal(true);
  readonly loadFailed = signal(false);
  readonly busy = signal(false);
  readonly catalog = signal<RunCatalog | null>(null);
  readonly configuration = signal<AgentRunConfiguration | null>(null);
  readonly error = signal<NhAssistantError | null>(null);
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));
  readonly saved = signal<string | null>(null);
  /** The warnings of the skill that was just saved, shown with the saved message. */
  readonly savedWarnings = signal<readonly string[]>([]);
  readonly form = signal<Form | null>(null);
  readonly confirming = signal<Confirmation | null>(null);
  readonly reason = signal('');
  /** The item whose action failed, so its error shows next to it: `settings`, `skill:{id}` or `trigger:{id}`. */
  readonly actionTarget = signal<string | null>(null);
  /**
   * The names of the account the agent answers to and of the people who changed skills, once known,
   * and why they are not.
   */
  readonly accountNames = new NhAssistantAccountNames();
  /**
   * The name of the account the agent answers to, once known: the technical account of its run
   * settings, or of an agent in code.
   */
  readonly accountName = computed(() => {
    const configuration = this.configuration();
    const actorId = configuration?.source === 'admin'
      ? configuration.runSettings?.serviceAccountActorId
      : this.agent().serviceAccount?.actorId;
    return actorId ? this.accountNames.names().get(actorId) ?? null : null;
  });
  /** The agent's name; an agent in code names itself with a translation key. */
  readonly agentName = computed(() => {
    this.language();
    const agent = this.agent();
    return agentDisplayName(this.translate, agent);
  });

  /** The skill being changed; `null` with the skill form open adds a new skill. */
  readonly editingSkill = signal<DataSkill | null>(null);
  /** The trigger being changed; `null` with the trigger form open adds a new trigger. */
  readonly editingTrigger = signal<DataTrigger | null>(null);
  /** The consultation being changed; `null` with the consultation form open adds a new one. */
  readonly editingConsultation = signal<DataConsultation | null>(null);
  /** Counts the loads, so the route switches read the agent's routes again after a change. */
  readonly loads = signal(0);
  readonly comparison = signal<{ skillId: string; result: SkillComparison | null; failed: boolean } | null>(null);
  readonly versions = signal<{ skillId: string; items: DataSkillVersion[] | null; failed: boolean } | null>(null);
  readonly triggerComparison = signal<{ triggerId: string; result: TriggerComparison | null; failed: boolean } | null>(null);
  readonly triggerVersions = signal<{ triggerId: string; items: DataTriggerVersion[] | null; failed: boolean } | null>(null);
  readonly triggerFieldKeys = triggerFieldKeys;
  readonly versionSummary = versionSummary;

  private readonly language = toSignal(
    this.translate.onLangChange.pipe(map(event => event.lang), startWith(this.translate.currentLang)),
    { initialValue: this.translate.currentLang });

  readonly isAdminAgent = computed(() => this.configuration()?.source === 'admin');
  /** An agent created in the administration gets skills and triggers once it has run settings. */
  readonly canAddRunData = computed(() => {
    const configuration = this.configuration();
    return configuration !== null && (configuration.source === 'code' || configuration.runSettings !== null);
  });
  readonly skillOptions = computed<NhAssistantPickerOption[]>(() => {
    this.language();
    const configuration = this.configuration();
    return configuration
      ? [
        ...configuration.codeSkills.map(skill => ({
          value: skill.skillId,
          label: this.skillName(skill.skillId),
          description: this.translated(skillDescriptionKeys(configuration.agentId, skill.skillId), skill.description)
        })),
        ...configuration.dataSkills.map(skill => ({ value: skill.skillId, label: this.skillName(skill.skillId), description: skill.description }))
      ]
      : [];
  });
  readonly skillIds = computed(() => this.skillOptions().map(option => option.value));
  readonly triggerIds = computed(() => {
    const configuration = this.configuration();
    return configuration ? [...configuration.codeTriggers, ...configuration.dataTriggers].map(trigger => trigger.triggerId) : [];
  });
  readonly problems = computed(() => this.configuration()?.problems ?? []);
  /** The consultations in code and as data, by subject type. */
  readonly consultationRows = computed(() => {
    const configuration = this.configuration();
    return configuration ? consultationRows(configuration) : [];
  });
  readonly showConsultations = computed(() => {
    const configuration = this.configuration();
    return configuration !== null && showsConsultations(configuration, this.catalog());
  });
  /** The ceiling within which administrators may define the agent's consultations; `null` for none. */
  readonly consultationCeiling = computed(() => this.configuration()?.consultationCeiling ?? null);
  /** Why administrators cannot add a consultation now, or `null` when they can. */
  readonly consultationsClosed = computed(() => {
    const configuration = this.configuration();
    return configuration ? consultationsClosed(configuration, this.catalog(), this.agent()) : null;
  });
  /** The subject types the agent already has a consultation about. */
  readonly consultationTypes = computed(() => this.consultationRows().map(row => row.subjectType));
  /** Whom the agent answers to, which decides whether a consultation may lend its rights. */
  readonly accountability = computed(() => {
    const configuration = this.configuration();
    return configuration ? flowAccountability(configuration)?.accountability ?? null : null;
  });
  readonly approvalPolicy = computed(() => {
    const configuration = this.configuration();
    return configuration ? agentApprovalPolicy(configuration) : null;
  });
  /** What the run settings' approval policy means, in a sentence. */
  readonly settingsPolicy = computed(() => {
    this.language();
    const name = this.configuration()?.runSettings?.approvalPolicy;
    const summary = this.catalog()?.approvalPolicySummaries?.find(item => item.name === name);
    return summary ? describePolicy(summary, (key, params) => this.translate.instant(key, params) as string) : null;
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.loadSubscription?.unsubscribe();
      this.actionSubscription?.unsubscribe();
      this.detailSubscription?.unsubscribe();
    });
  }

  ngOnInit(): void {
    this.load(this.startSetup());
  }

  /** The lock a part's package link shows, for the lists and the forms. */
  readonly packageLockOf = packageLockOf;
  /** Whether a part's package locks it, so it cannot be removed until it is unlinked. */
  readonly isPackageLocked = isPackageLocked;

  load(openSetup = false): void {
    this.loading.set(true);
    this.loadFailed.set(false);
    this.loadSubscription?.unsubscribe();
    this.loadSubscription = forkJoin([this.admin.getRunCatalog(), this.admin.getAgentConfiguration(this.agent().id)]).subscribe({
      next: ([catalog, configuration]) => {
        this.catalog.set(catalog);
        this.configuration.set(configuration);
        this.loading.set(false);
        this.loads.update(count => count + 1);
        this.accountNames.load([
          configuration.runSettings?.serviceAccountActorId,
          this.agent().serviceAccount?.actorId,
          ...configuration.dataSkills.map(skill => skill.changedBy),
          ...(configuration.dataConsultations ?? []).map(consultation => consultation.changedBy)
        ]);
        if (openSetup && configuration.source === 'admin' && configuration.runSettings === null) {
          this.open('setup');
        }
      },
      error: () => {
        this.loading.set(false);
        this.loadFailed.set(true);
      }
    });
  }

  openSetup(): void {
    this.open('setup');
  }

  editSettings(): void {
    this.open('settings');
  }

  removeSettings(): void {
    const version = this.configuration()?.runSettings?.version ?? null;
    this.run(this.admin.removeRunSettings(this.agent().id, { expectedVersion: version }), 'nh-assistant.admin.run-config.settings.removed', 'settings');
  }

  addSkill(): void {
    this.editingSkill.set(null);
    this.open('skill');
  }

  editSkill(skill: DataSkill): void {
    this.editingSkill.set(skill);
    this.open('skill');
  }

  removeSkill(skill: DataSkill): void {
    this.run(
      this.admin.removeDataSkill(this.agent().id, skill.skillId, { expectedVersion: skill.version }),
      'nh-assistant.admin.run-config.skills.removed',
      `skill:${skill.skillId}`);
  }

  promote(skill: DataSkill): void {
    const candidate = skill.candidate;
    if (candidate) {
      this.run(
        this.admin.promoteSkillCandidate(this.agent().id, skill.skillId, { version: candidate.version, reason: blank(this.reason()) }),
        'nh-assistant.admin.run-config.candidate.promoted',
        `skill:${skill.skillId}`);
    }
  }

  discard(skill: DataSkill): void {
    const candidate = skill.candidate;
    if (candidate) {
      this.run(
        this.admin.discardSkillCandidate(this.agent().id, skill.skillId, { version: candidate.version, reason: blank(this.reason()) }),
        'nh-assistant.admin.run-config.candidate.discarded',
        `skill:${skill.skillId}`);
    }
  }

  /** Shows or hides the comparison of a skill's candidate with its live version. */
  toggleComparison(skill: DataSkill): void {
    if (this.comparison()?.skillId === skill.skillId) {
      this.comparison.set(null);
      return;
    }

    this.closeDetails();
    this.comparison.set({ skillId: skill.skillId, result: null, failed: false });
    this.detailSubscription = this.admin.compareSkillCandidate(this.agent().id, skill.skillId).subscribe({
      next: result => this.comparison.set({ skillId: skill.skillId, result, failed: false }),
      error: () => this.comparison.set({ skillId: skill.skillId, result: null, failed: true })
    });
  }

  /** Shows or hides the version history of a skill. */
  toggleVersions(skill: DataSkill): void {
    if (this.versions()?.skillId === skill.skillId) {
      this.versions.set(null);
      return;
    }

    this.closeDetails();
    this.versions.set({ skillId: skill.skillId, items: null, failed: false });
    this.detailSubscription = this.admin.getDataSkillVersions(this.agent().id, skill.skillId).subscribe({
      next: items => {
        this.versions.set({ skillId: skill.skillId, items, failed: false });
        this.accountNames.load(items.map(item => item.createdBy));
      },
      error: () => this.versions.set({ skillId: skill.skillId, items: null, failed: true })
    });
  }

  addTrigger(): void {
    this.editingTrigger.set(null);
    this.open('trigger');
  }

  /** What a trigger's candidate changes against its live version. */
  triggerChanges(trigger: DataTrigger): TriggerChange[] {
    return trigger.candidate ? triggerChanges(trigger, trigger.candidate) : [];
  }

  /** A value of a changed field as people read it: a skill's name, a conflict, a source, or none. */
  changeValue(change: TriggerChange, value: string | null): string {
    if (value === null) {
      return this.translate.instant('nh-assistant.admin.run-config.triggers.candidate.none') as string;
    }
    switch (change.field) {
      case 'skill':
        return this.skillName(value);
      case 'conflict':
        return isConflict(value) ? this.translate.instant(`nh-assistant.admin.run-config.triggers.conflicts.${value}`) as string : value;
      case 'source':
        return this.translate.instant(`nh-assistant.admin.run-config.triggers.sources.${value}`) as string;
      default:
        return value;
    }
  }

  promoteTrigger(trigger: DataTrigger): void {
    const candidate = trigger.candidate;
    if (candidate) {
      this.run(
        this.admin.promoteTriggerCandidate(this.agent().id, trigger.triggerId, { version: candidate.version, reason: blank(this.reason()) }),
        'nh-assistant.admin.run-config.triggers.candidate.promoted',
        `trigger:${trigger.triggerId}`);
    }
  }

  discardTrigger(trigger: DataTrigger): void {
    const candidate = trigger.candidate;
    if (candidate) {
      this.run(
        this.admin.discardTriggerCandidate(this.agent().id, trigger.triggerId, { version: candidate.version, reason: blank(this.reason()) }),
        'nh-assistant.admin.run-config.candidate.discarded',
        `trigger:${trigger.triggerId}`);
    }
  }

  /** Shows or hides the comparison of a trigger's candidate with its live version. */
  toggleTriggerComparison(trigger: DataTrigger): void {
    if (this.triggerComparison()?.triggerId === trigger.triggerId) {
      this.triggerComparison.set(null);
      return;
    }

    this.closeDetails();
    this.triggerComparison.set({ triggerId: trigger.triggerId, result: null, failed: false });
    this.detailSubscription = this.admin.compareTriggerCandidate(this.agent().id, trigger.triggerId).subscribe({
      next: result => this.triggerComparison.set({ triggerId: trigger.triggerId, result, failed: false }),
      error: () => this.triggerComparison.set({ triggerId: trigger.triggerId, result: null, failed: true })
    });
  }

  /** Shows or hides the version history of a trigger. */
  toggleTriggerVersions(trigger: DataTrigger): void {
    if (this.triggerVersions()?.triggerId === trigger.triggerId) {
      this.triggerVersions.set(null);
      return;
    }

    this.closeDetails();
    this.triggerVersions.set({ triggerId: trigger.triggerId, items: null, failed: false });
    this.detailSubscription = this.admin.getDataTriggerVersions(this.agent().id, trigger.triggerId).subscribe({
      next: items => {
        this.triggerVersions.set({ triggerId: trigger.triggerId, items, failed: false });
        this.accountNames.load(items.map(item => item.createdBy));
      },
      error: () => this.triggerVersions.set({ triggerId: trigger.triggerId, items: null, failed: true })
    });
  }

  editTrigger(trigger: DataTrigger): void {
    this.editingTrigger.set(trigger);
    this.open('trigger');
  }

  setTriggerShadow(trigger: DataTrigger, shadow: boolean): void {
    this.run(
      this.admin.setDataTriggerShadow(this.agent().id, trigger.triggerId, { mode: shadow ? 'shadow' : 'live', reason: null }),
      shadow ? 'nh-assistant.admin.run-config.triggers.in-shadow' : 'nh-assistant.admin.run-config.triggers.live',
      `trigger:${trigger.triggerId}`);
  }

  removeTrigger(trigger: DataTrigger): void {
    this.run(
      this.admin.removeDataTrigger(this.agent().id, trigger.triggerId, { expectedVersion: trigger.version }),
      'nh-assistant.admin.run-config.triggers.removed',
      `trigger:${trigger.triggerId}`);
  }

  addConsultation(): void {
    this.editingConsultation.set(null);
    this.open('consultation');
  }

  editConsultation(consultation: DataConsultation): void {
    this.editingConsultation.set(consultation);
    this.open('consultation');
  }

  /** Switches a consultation defined as data on or off; off, asking agents get none of its tools. */
  setConsultationState(consultation: DataConsultation, on: boolean): void {
    this.run(
      this.admin.setDataConsultationState(this.agent().id, consultation.subjectType, { state: on ? 'on' : 'off', reason: null }),
      on ? 'nh-assistant.admin.run-config.consultations.switched-on' : 'nh-assistant.admin.run-config.consultations.switched-off',
      `consultation:${consultation.subjectType}`);
  }

  removeConsultation(consultation: DataConsultation): void {
    this.run(
      this.admin.removeDataConsultation(this.agent().id, consultation.subjectType, { expectedVersion: consultation.version }),
      'nh-assistant.admin.run-config.consultations.removed',
      `consultation:${consultation.subjectType}`);
  }

  onConsultationSaved(result: ConsultationFormSaved): void {
    this.finish(result.added ? 'nh-assistant.admin.run-config.consultations.added' : 'nh-assistant.admin.run-config.consultations.saved');
  }

  /** The name of a subject type, as the run catalog names it in the reader's language. */
  typeName(subjectType: string): string {
    return typeName(this.catalog(), subjectType);
  }

  /** An agent's name: the translation of an agent in code, the administrator's text, or its id. */
  agentNameOf(agentId: string): string {
    const agent = this.agents().find(item => item.id === agentId);
    return agent ? agentDisplayName(this.translate, agent) : agentId;
  }

  /** Who may ask a consultation defined as data, in a sentence; `null` for one in code, whose code says it. */
  askSentence(row: ConsultationRow): string | null {
    const data = row.data;
    if (!data) {
      return null;
    }
    const base = 'nh-assistant.admin.run-config.consultations.summary';
    const role = this.translate.instant(`nh-assistant.admin.run-config.consultations.roles-lower.${data.minimumRole}`) as string;
    const ask = this.translate.instant(`${base}.ask`, { role }) as string;
    const policy = data.askerPolicy
      ? this.translate.instant(`${base}.asker-policy`, { policy: this.translated(policyKeys(data.askerPolicy), data.askerPolicy) }) as string
      : this.translate.instant(`${base}.asker-policy-agent`) as string;
    return `${ask} ${policy}`;
  }

  /** What the consultation may do and who decides its proposals, by the routes in effect. */
  decideSentence(row: ConsultationRow): string {
    const base = 'nh-assistant.admin.run-config.consultations.summary';
    if (row.routes.length === 0) {
      return this.translate.instant(`${base}.read-only`) as string;
    }
    const routes = row.routes
      .map(route => this.translate.instant(`nh-assistant.admin.agents.consultation-routes.route.${route}`) as string)
      .join('; ');
    if (!row.data) {
      return this.translate.instant(`${base}.proposes-code`, { routes }) as string;
    }
    const role = this.translate.instant(`nh-assistant.admin.run-config.consultations.roles-lower.${row.data.proposalRole}`) as string;
    return this.translate.instant(`${base}.proposes`, { role, routes }) as string;
  }

  /** A form saved: show what happened and reload, which every change may affect. */
  onSetupCompleted(live: boolean): void {
    this.finish(live ? 'nh-assistant.admin.run-config.setup.saved-live' : 'nh-assistant.admin.run-config.setup.saved-shadow');
  }

  onSettingsSaved(): void {
    this.finish('nh-assistant.admin.run-config.settings.saved');
  }

  onSkillSaved(result: SkillFormSaved): void {
    this.finish(
      result.candidate
        ? 'nh-assistant.admin.run-config.skills.saved-candidate'
        : result.added ? 'nh-assistant.admin.run-config.skills.added' : 'nh-assistant.admin.run-config.skills.saved',
      result.skill.warnings ?? []);
  }

  onTriggerSaved(result: { added: boolean; candidate: boolean }): void {
    this.finish(
      result.candidate
        ? 'nh-assistant.admin.run-config.triggers.saved-candidate'
        : result.added ? 'nh-assistant.admin.run-config.triggers.added' : 'nh-assistant.admin.run-config.triggers.saved');
  }

  confirm(action: Confirmation['action'], id: string): void {
    this.form.set(null);
    this.error.set(null);
    this.saved.set(null);
    this.reason.set('');
    this.confirming.set({ action, id });
  }

  isConfirming(action: Confirmation['action'], id: string): boolean {
    const confirming = this.confirming();
    return confirming?.action === action && confirming.id === id;
  }

  /**
   * An entry point of the flow: the forms of the run configuration open below with the focus in them;
   * the editor, the account, the runs and the MCP servers belong to the host.
   */
  onFlowEntry(entry: AgentFlowEntry): void {
    const opener = this.host.nativeElement.ownerDocument.activeElement;
    switch (entry.kind) {
      case 'setup':
        this.openFromFlow(opener, () => this.openSetup());
        return;
      case 'settings':
        this.openFromFlow(opener, () => this.editSettings());
        return;
      case 'add-skill':
        this.openFromFlow(opener, () => this.addSkill());
        return;
      case 'edit-skill': {
        const skill = this.configuration()?.dataSkills.find(item => item.skillId === entry.skillId);
        if (skill) {
          this.openFromFlow(opener, () => this.editSkill(skill));
        }
        return;
      }
      case 'add-trigger':
        this.openFromFlow(opener, () => this.addTrigger());
        return;
      case 'edit-trigger': {
        const trigger = this.configuration()?.dataTriggers.find(item => item.triggerId === entry.triggerId);
        if (trigger) {
          this.openFromFlow(opener, () => this.editTrigger(trigger));
        }
        return;
      }
      case 'add-consultation':
        this.openFromFlow(opener, () => this.addConsultation());
        return;
      case 'edit-consultation': {
        const consultation = this.configuration()?.dataConsultations?.find(item => item.subjectType === entry.subjectType);
        if (consultation) {
          this.openFromFlow(opener, () => this.editConsultation(consultation));
        }
        return;
      }
      case 'edit-agent':
        this.editAgent.emit();
        return;
      case 'account':
        this.changeAccount.emit();
        return;
      case 'runs':
        this.openRuns.emit();
        return;
      case 'mcp-servers':
        this.openMcpServers.emit();
        return;
    }
  }

  cancel(): void {
    const form = this.form();
    const opener = this.returnFocus;
    this.returnFocus = null;
    this.form.set(null);
    this.confirming.set(null);
    this.error.set(null);
    // Back to the action that opened the form: the flow's entry point, or the action of its section.
    afterNextRender(() => {
      const target = opener?.isConnected
        ? opener
        : this.host.nativeElement.querySelector<HTMLElement>(`[data-open-form="${form}"]`);
      target?.focus();
    }, { injector: this.injector });
  }

  value(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  /** A skill's name: the application's translation, or a readable form of its id. */
  skillName(skillId: string | null | undefined): string {
    return skillId ? this.translated(skillNameKeys(this.agent().id, skillId), readableId(skillId)) : '';
  }

  /** A person's name, or the id when the application does not know it. */
  nameOf(actorId: string | null | undefined): string {
    return this.accountNames.nameOf(actorId);
  }

  /** The account ID as the tooltip of a name shown in its place. */
  accountTitle(actorId: string | null | undefined): string | null {
    return this.accountNames.idTitleOf(actorId);
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

  /** Opens a form for the flow and remembers the entry point that opened it. */
  private openFromFlow(opener: Element | null, openForm: () => void): void {
    openForm();
    this.returnFocus = opener instanceof HTMLElement ? opener : null;
  }

  private finish(savedKey: string, warnings: readonly string[] = []): void {
    this.returnFocus = null;
    this.form.set(null);
    this.saved.set(savedKey);
    this.savedWarnings.set(warnings);
    this.changed.emit();
    this.load();
    afterNextRender(() => this.host.nativeElement.querySelector<HTMLElement>('[data-saved]')?.focus(), { injector: this.injector });
  }

  /** Opens one form at a time and moves the focus into it. */
  private open(form: Form): void {
    this.returnFocus = null;
    this.confirming.set(null);
    this.error.set(null);
    this.saved.set(null);
    this.reason.set('');
    this.form.set(form);
    afterNextRender(() => {
      this.host.nativeElement.querySelector<HTMLElement>(
        `[data-form="${form}"] input:not([disabled]), [data-form="${form}"] textarea, [data-work-setup] input:not([disabled])`)?.focus();
    }, { injector: this.injector });
  }

  /** One comparison or history is shown at a time. */
  private closeDetails(): void {
    this.detailSubscription?.unsubscribe();
    this.comparison.set(null);
    this.versions.set(null);
    this.triggerComparison.set(null);
    this.triggerVersions.set(null);
  }

  /** Whether the last failed action concerned this item, so its error shows there. */
  failed(target: string): boolean {
    return this.error() !== null && this.actionTarget() === target;
  }

  /** Runs a change and reloads the configuration, which every change may affect. */
  private run(request: Observable<unknown>, savedKey: string, target: string): void {
    this.busy.set(true);
    this.error.set(null);
    this.saved.set(null);
    this.actionTarget.set(target);
    this.actionSubscription?.unsubscribe();
    this.actionSubscription = request.subscribe({
      error: (error: unknown) => {
        this.busy.set(false);
        this.error.set(toNhAssistantError(error));
      },
      complete: () => {
        this.busy.set(false);
        this.confirming.set(null);
        this.closeDetails();
        this.finish(savedKey);
      }
    });
  }
}

function blank(value: string): string | null {
  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : null;
}
