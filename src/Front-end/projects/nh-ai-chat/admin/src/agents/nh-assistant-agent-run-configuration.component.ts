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
import { TranslatePipe } from '@ngx-translate/core';
import { Observable, Subscription, forkJoin } from 'rxjs';
import {
  AdminAgent,
  AgentAccountability,
  AgentElevatedReads,
  AgentRunConfiguration,
  AgentRunSettingsRequest,
  DataSkill,
  DataSkillRequest,
  DataSkillVersion,
  DataTrigger,
  DataTriggerRequest,
  NhAssistantAdminApiService,
  NhAssistantError,
  RunCatalog,
  SkillApplyMode,
  SkillComparison,
  TriggerConflict,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';

let nextId = 0;

/** The longest reason the server stores with a change. */
const maxReason = 500;
const maxDescription = 300;
const maxInstructions = 20_000;
const maxMessage = 2_000;
const dashCase = /^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$/;

type Form = 'settings' | 'skill' | 'trigger';

/** A removal, promotion or discard that waits for its confirmation. */
interface Confirmation {
  action: 'remove-settings' | 'remove-skill' | 'remove-trigger' | 'promote' | 'discard';
  id: string;
}

/**
 * The run configuration of one agent as data: the run settings of an agent created in the
 * administration, its skills and triggers in code (read only) and as data, and the data that no
 * longer fits the code. Administrators add and change data skills, live at once or as a candidate in
 * shadow that they compare with the live version and promote or discard; they add data triggers on
 * the application's events or on a schedule and put them live. New skills and triggers start in
 * shadow; `changed` tells the agents page to reload the agent's skill states.
 */
@Component({
  selector: 'nh-assistant-agent-run-configuration',
  standalone: true,
  imports: [DatePipe, DecimalPipe, TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-agent-run-configuration.component.html',
  styleUrls: ['../nh-assistant-admin-shared.scss', './nh-assistant-agent-feedback-report.component.scss']
})
export class NhAssistantAgentRunConfigurationComponent implements OnInit {
  private readonly admin = inject(NhAssistantAdminApiService);
  private readonly injector = inject(Injector);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private loadSubscription?: Subscription;
  private actionSubscription?: Subscription;
  private detailSubscription?: Subscription;

  readonly agent = input.required<AdminAgent>();
  /** A change of the run configuration changed the agent's skills or state. */
  readonly changed = output<void>();

  readonly id = `nh-assistant-run-configuration-${nextId++}`;
  readonly maxReason = maxReason;
  readonly maxDescription = maxDescription;
  readonly maxInstructions = maxInstructions;
  readonly maxMessage = maxMessage;
  readonly accountabilities: AgentAccountability[] = ['agent', 'subject-responsible', 'service-account'];
  readonly elevatedReadModes: AgentElevatedReads[] = ['restricted', 'deny'];
  readonly conflicts: TriggerConflict[] = ['coalesce', 'queue', 'supersede', 'ignore'];

  readonly loading = signal(true);
  readonly loadFailed = signal(false);
  readonly busy = signal(false);
  readonly catalog = signal<RunCatalog | null>(null);
  readonly configuration = signal<AgentRunConfiguration | null>(null);
  readonly error = signal<NhAssistantError | null>(null);
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));
  readonly saved = signal<string | null>(null);
  readonly form = signal<Form | null>(null);
  readonly confirming = signal<Confirmation | null>(null);
  readonly reason = signal('');

  /** The skill being changed; `null` with the skill form open adds a new skill. */
  readonly editingSkill = signal<DataSkill | null>(null);
  /** The trigger being changed; `null` with the trigger form open adds a new trigger. */
  readonly editingTrigger = signal<DataTrigger | null>(null);
  readonly comparison = signal<{ skillId: string; result: SkillComparison | null; failed: boolean } | null>(null);
  readonly versions = signal<{ skillId: string; items: DataSkillVersion[] | null; failed: boolean } | null>(null);

  // Run settings form.
  readonly accountability = signal<AgentAccountability>('service-account');
  readonly serviceAccount = signal('');
  readonly approvalPolicy = signal('');
  readonly ruleHandler = signal('');
  readonly triageProfile = signal('');
  readonly retryCount = signal('3');
  readonly maxConcurrentRuns = signal('4');
  readonly maxRunsPerSubjectPerDay = signal('10');
  readonly maxTokensPerRun = signal('');
  readonly dailyToolCalls = signal('');
  readonly dailyTokens = signal('');
  readonly allowGatewayReads = signal(false);
  readonly delegateFromInitiator = signal(false);
  readonly elevatedReads = signal<AgentElevatedReads>('restricted');

  // Skill form.
  readonly skillId = signal('');
  readonly skillDescription = signal('');
  readonly skillInstructions = signal('');
  readonly skillTools = signal<string[]>([]);
  readonly skillProfile = signal('');
  readonly skillRuleHandler = signal('');
  readonly skillApprovalPolicy = signal('');
  readonly skillMaxTokens = signal('');
  readonly skillDailyToolCalls = signal('');
  readonly skillDailyTokens = signal('');
  readonly skillApply = signal<SkillApplyMode>('live');

  // Trigger form.
  readonly triggerId = signal('');
  readonly triggerSource = signal<'event' | 'schedule'>('event');
  readonly triggerEvent = signal('');
  readonly triggerCron = signal('');
  readonly triggerTimeZone = signal('');
  readonly triggerSubjectSet = signal('');
  readonly triggerMessage = signal('');
  readonly triggerSkill = signal('');
  readonly triggerConflict = signal<TriggerConflict>('coalesce');
  readonly triggerDebounce = signal('120');

  readonly isAdminAgent = computed(() => this.configuration()?.source === 'admin');
  /** An agent created in the administration gets skills and triggers once it has run settings. */
  readonly canAddRunData = computed(() => {
    const configuration = this.configuration();
    return configuration !== null && (configuration.source === 'code' || configuration.runSettings !== null);
  });
  readonly skillOptions = computed(() => {
    const configuration = this.configuration();
    return configuration
      ? [...configuration.codeSkills.map(skill => skill.skillId), ...configuration.dataSkills.map(skill => skill.skillId)]
      : [];
  });
  readonly problems = computed(() => this.configuration()?.problems ?? []);
  readonly skillIdInvalid = computed(() => this.editingSkill() === null && !dashCase.test(this.skillId().trim()));
  readonly triggerIdInvalid = computed(() => this.editingTrigger() === null && !dashCase.test(this.triggerId().trim()));
  readonly descriptionInvalid = computed(() => {
    const description = this.skillDescription().trim();
    return description.length === 0 || description.length > maxDescription;
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.loadSubscription?.unsubscribe();
      this.actionSubscription?.unsubscribe();
      this.detailSubscription?.unsubscribe();
    });
  }

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.loadFailed.set(false);
    this.loadSubscription?.unsubscribe();
    this.loadSubscription = forkJoin([this.admin.getRunCatalog(), this.admin.getAgentConfiguration(this.agent().id)]).subscribe({
      next: ([catalog, configuration]) => {
        this.catalog.set(catalog);
        this.configuration.set(configuration);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.loadFailed.set(true);
      }
    });
  }

  /** The first error of a field from the last failed save, as a translation key. */
  fieldError(field: string): string | null {
    const code = this.error()?.fieldErrors?.[field]?.[0];
    return code ? `nh-assistant.admin.run-config.field-errors.${code}` : null;
  }

  editSettings(): void {
    const settings = this.configuration()?.runSettings;
    this.accountability.set(settings?.accountability ?? 'service-account');
    this.serviceAccount.set(settings?.serviceAccountActorId ?? '');
    this.approvalPolicy.set(settings?.approvalPolicy ?? '');
    this.ruleHandler.set(settings?.ruleHandler ?? '');
    this.triageProfile.set(settings?.triageProfile ?? '');
    this.retryCount.set(String(settings?.retryCount ?? 3));
    this.maxConcurrentRuns.set(String(settings?.maxConcurrentRuns ?? 4));
    this.maxRunsPerSubjectPerDay.set(String(settings?.maxRunsPerSubjectPerDay ?? 10));
    this.maxTokensPerRun.set(numberText(settings?.maxTokensPerRun));
    this.dailyToolCalls.set(numberText(settings?.dailyToolCalls));
    this.dailyTokens.set(numberText(settings?.dailyTokens));
    this.allowGatewayReads.set(settings?.allowGatewayReads ?? false);
    this.delegateFromInitiator.set(settings?.delegateFromInitiator ?? false);
    this.elevatedReads.set(settings?.elevatedReads ?? 'restricted');
    this.open('settings');
  }

  saveSettings(): void {
    const request: AgentRunSettingsRequest = {
      accountability: this.accountability(),
      serviceAccountActorId: this.accountability() === 'service-account' ? blank(this.serviceAccount()) : null,
      delegateFromInitiator: this.delegateFromInitiator(),
      allowGatewayReads: this.allowGatewayReads(),
      elevatedReads: this.elevatedReads(),
      approvalPolicy: blank(this.approvalPolicy()),
      retryCount: toNumber(this.retryCount()),
      maxConcurrentRuns: toNumber(this.maxConcurrentRuns()),
      maxRunsPerSubjectPerDay: toNumber(this.maxRunsPerSubjectPerDay()),
      maxTokensPerRun: toNumber(this.maxTokensPerRun()),
      dailyToolCalls: toNumber(this.dailyToolCalls()),
      dailyTokens: toNumber(this.dailyTokens()),
      triageProfile: blank(this.triageProfile()),
      ruleHandler: blank(this.ruleHandler()),
      expectedVersion: this.configuration()?.runSettings?.version ?? null,
      reason: blank(this.reason())
    };
    this.run(this.admin.saveRunSettings(this.agent().id, request), 'nh-assistant.admin.run-config.settings.saved');
  }

  removeSettings(): void {
    const version = this.configuration()?.runSettings?.version ?? null;
    this.run(this.admin.removeRunSettings(this.agent().id, { expectedVersion: version }), 'nh-assistant.admin.run-config.settings.removed');
  }

  addSkill(): void {
    this.editingSkill.set(null);
    this.fillSkill(null);
    this.open('skill');
  }

  editSkill(skill: DataSkill): void {
    this.editingSkill.set(skill);
    this.fillSkill(skill);
    this.open('skill');
  }

  toggleTool(selector: string, selected: boolean): void {
    this.skillTools.update(tools => selected
      ? [...tools.filter(tool => tool !== selector), selector]
      : tools.filter(tool => tool !== selector));
  }

  saveSkill(): void {
    const editing = this.editingSkill();
    const skillId = editing?.skillId ?? this.skillId().trim();
    if (this.skillIdInvalid() || this.descriptionInvalid() || this.skillInstructions().length > maxInstructions) {
      return;
    }

    const request: DataSkillRequest = {
      description: this.skillDescription().trim(),
      instructions: blank(this.skillInstructions()),
      tools: this.skillTools(),
      profile: blank(this.skillProfile()),
      ruleHandler: blank(this.skillRuleHandler()),
      approvalPolicy: blank(this.skillApprovalPolicy()),
      maxTokensPerRun: toNumber(this.skillMaxTokens()),
      dailyToolCalls: toNumber(this.skillDailyToolCalls()),
      dailyTokens: toNumber(this.skillDailyTokens()),
      expectedVersion: editing?.version ?? null,
      reason: blank(this.reason()),
      ...(editing ? { apply: this.skillApply() } : {})
    };
    const saved = editing && this.skillApply() === 'shadow'
      ? 'nh-assistant.admin.run-config.skills.saved-candidate'
      : editing ? 'nh-assistant.admin.run-config.skills.saved' : 'nh-assistant.admin.run-config.skills.added';
    this.run(this.admin.saveDataSkill(this.agent().id, skillId, request), saved);
  }

  removeSkill(skill: DataSkill): void {
    this.run(
      this.admin.removeDataSkill(this.agent().id, skill.skillId, { expectedVersion: skill.version }),
      'nh-assistant.admin.run-config.skills.removed');
  }

  promote(skill: DataSkill): void {
    const candidate = skill.candidate;
    if (candidate) {
      this.run(
        this.admin.promoteSkillCandidate(this.agent().id, skill.skillId, { version: candidate.version, reason: blank(this.reason()) }),
        'nh-assistant.admin.run-config.candidate.promoted');
    }
  }

  discard(skill: DataSkill): void {
    const candidate = skill.candidate;
    if (candidate) {
      this.run(
        this.admin.discardSkillCandidate(this.agent().id, skill.skillId, { version: candidate.version, reason: blank(this.reason()) }),
        'nh-assistant.admin.run-config.candidate.discarded');
    }
  }

  /** Shows or hides the comparison of a skill's candidate with its live version. */
  toggleComparison(skill: DataSkill): void {
    if (this.comparison()?.skillId === skill.skillId) {
      this.comparison.set(null);
      return;
    }

    this.versions.set(null);
    this.comparison.set({ skillId: skill.skillId, result: null, failed: false });
    this.detailSubscription?.unsubscribe();
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

    this.comparison.set(null);
    this.versions.set({ skillId: skill.skillId, items: null, failed: false });
    this.detailSubscription?.unsubscribe();
    this.detailSubscription = this.admin.getDataSkillVersions(this.agent().id, skill.skillId).subscribe({
      next: items => this.versions.set({ skillId: skill.skillId, items, failed: false }),
      error: () => this.versions.set({ skillId: skill.skillId, items: null, failed: true })
    });
  }

  addTrigger(): void {
    this.editingTrigger.set(null);
    this.fillTrigger(null);
    this.open('trigger');
  }

  editTrigger(trigger: DataTrigger): void {
    this.editingTrigger.set(trigger);
    this.fillTrigger(trigger);
    this.open('trigger');
  }

  /** A schedule skips a tick while a run is busy by default; an event coalesces. */
  setTriggerSource(source: 'event' | 'schedule'): void {
    this.triggerSource.set(source);
    if (this.editingTrigger() === null) {
      this.triggerConflict.set(source === 'schedule' ? 'ignore' : 'coalesce');
    }
  }

  saveTrigger(): void {
    const editing = this.editingTrigger();
    if (this.triggerIdInvalid()) {
      return;
    }

    const schedule = this.triggerSource() === 'schedule';
    const request: DataTriggerRequest = {
      source: this.triggerSource(),
      skill: blank(this.triggerSkill()),
      event: schedule ? null : blank(this.triggerEvent()),
      cron: schedule ? blank(this.triggerCron()) : null,
      timeZone: schedule ? blank(this.triggerTimeZone()) : null,
      subjectSet: schedule ? blank(this.triggerSubjectSet()) : null,
      message: schedule ? blank(this.triggerMessage()) : null,
      conflict: this.triggerConflict(),
      debounceSeconds: schedule ? null : toNumber(this.triggerDebounce()),
      expectedVersion: editing?.version ?? null,
      reason: blank(this.reason())
    };
    this.run(
      this.admin.saveDataTrigger(this.agent().id, editing?.triggerId ?? this.triggerId().trim(), request),
      editing ? 'nh-assistant.admin.run-config.triggers.saved' : 'nh-assistant.admin.run-config.triggers.added');
  }

  setTriggerShadow(trigger: DataTrigger, shadow: boolean): void {
    this.run(
      this.admin.setDataTriggerShadow(this.agent().id, trigger.triggerId, { mode: shadow ? 'shadow' : 'live', reason: null }),
      shadow ? 'nh-assistant.admin.run-config.triggers.in-shadow' : 'nh-assistant.admin.run-config.triggers.live');
  }

  removeTrigger(trigger: DataTrigger): void {
    this.run(
      this.admin.removeDataTrigger(this.agent().id, trigger.triggerId, { expectedVersion: trigger.version }),
      'nh-assistant.admin.run-config.triggers.removed');
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

  cancel(): void {
    this.form.set(null);
    this.confirming.set(null);
    this.error.set(null);
  }

  value(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  checked(event: Event): boolean {
    return (event.target as HTMLInputElement).checked;
  }

  private fillSkill(skill: DataSkill | null): void {
    this.skillId.set(skill?.skillId ?? '');
    this.skillDescription.set(skill?.description ?? '');
    this.skillInstructions.set(skill?.instructions ?? '');
    this.skillTools.set([...(skill?.tools ?? [])]);
    this.skillProfile.set(skill?.profile ?? '');
    this.skillRuleHandler.set(skill?.ruleHandler ?? '');
    this.skillApprovalPolicy.set(skill?.approvalPolicy ?? '');
    this.skillMaxTokens.set(numberText(skill?.maxTokensPerRun));
    this.skillDailyToolCalls.set(numberText(skill?.dailyToolCalls));
    this.skillDailyTokens.set(numberText(skill?.dailyTokens));
    // A real change is evaluated in shadow first; a small correction goes live.
    this.skillApply.set('shadow');
  }

  private fillTrigger(trigger: DataTrigger | null): void {
    this.triggerId.set(trigger?.triggerId ?? '');
    this.triggerSource.set(trigger?.source ?? 'event');
    this.triggerEvent.set(trigger?.event ?? '');
    this.triggerCron.set(trigger?.cron ?? '');
    this.triggerTimeZone.set(trigger?.timeZone ?? '');
    this.triggerSubjectSet.set(trigger?.subjectSet ?? '');
    this.triggerMessage.set(trigger?.message ?? '');
    this.triggerSkill.set(trigger?.skillId ?? '');
    this.triggerConflict.set(trigger?.conflict ?? 'coalesce');
    this.triggerDebounce.set(String(trigger?.debounceSeconds ?? 120));
  }

  /** Opens one form at a time and moves the focus into it. */
  private open(form: Form): void {
    this.confirming.set(null);
    this.error.set(null);
    this.saved.set(null);
    this.reason.set('');
    this.form.set(form);
    afterNextRender(() => {
      this.host.nativeElement.querySelector<HTMLElement>(`[data-form="${form}"] input:not([disabled]), [data-form="${form}"] select, [data-form="${form}"] textarea`)?.focus();
    }, { injector: this.injector });
  }

  /** Runs a change and reloads the configuration, which every change may affect. */
  private run(request: Observable<unknown>, savedKey: string): void {
    this.busy.set(true);
    this.error.set(null);
    this.saved.set(null);
    this.actionSubscription?.unsubscribe();
    this.actionSubscription = request.subscribe({
      error: (error: unknown) => {
        this.busy.set(false);
        this.error.set(toNhAssistantError(error));
      },
      complete: () => {
        this.busy.set(false);
        this.form.set(null);
        this.confirming.set(null);
        this.comparison.set(null);
        this.versions.set(null);
        this.saved.set(savedKey);
        this.changed.emit();
        this.load();
      }
    });
  }
}

function blank(value: string): string | null {
  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : null;
}

function toNumber(value: string): number | null {
  const trimmed = value.trim();
  if (trimmed.length === 0) {
    return null;
  }
  const parsed = Number(trimmed);
  return Number.isFinite(parsed) ? parsed : null;
}

function numberText(value: number | null | undefined): string {
  return value === null || value === undefined ? '' : String(value);
}
