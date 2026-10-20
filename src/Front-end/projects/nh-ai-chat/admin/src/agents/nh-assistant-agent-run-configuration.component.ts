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
  DataSkill,
  DataSkillVersion,
  DataTrigger,
  NhAssistantAdminApiService,
  NhAssistantError,
  RunCatalog,
  SkillComparison,
  ToolCatalogEntry,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantPickerOption as NhAssistantPickerOption,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';
import { NhAssistantAccountNames, NhAssistantAccountNamesErrorComponent } from '../nh-assistant-account-names';
import { codeTextKeys, readableId, skillDescriptionKeys, skillNameKeys } from './agent-texts';
import { NhAssistantAgentWorkSetupComponent } from './run-config/nh-assistant-agent-work-setup.component';
import { NhAssistantRunSettingsFormComponent } from './run-config/nh-assistant-run-settings-form.component';
import { NhAssistantSkillFormComponent, SkillFormSaved } from './run-config/nh-assistant-skill-form.component';
import { NhAssistantTriggerFormComponent } from './run-config/nh-assistant-trigger-form.component';
import { describePolicy, runConfigLimits } from './run-config/run-config-shared';

let nextId = 0;

type Form = 'setup' | 'settings' | 'skill' | 'trigger';

/** A removal, promotion or discard that waits for its confirmation. */
interface Confirmation {
  action: 'remove-settings' | 'remove-skill' | 'remove-trigger' | 'promote' | 'discard';
  id: string;
}

/**
 * The run configuration of one agent as data: the run settings of an agent created in the
 * administration, its skills and triggers in code (read only) and as data, and the data that no
 * longer fits the code. An agent created here that only chats gets a guided setup that lets it work
 * on its own. Administrators add and change data skills, live at once or as a candidate in shadow that
 * they compare with the live version and promote or discard; they add data triggers on the
 * application's events or on a schedule and put them live; a schedule without a subject set runs for
 * the whole application, or for every division of an agent that works per division. New skills and
 * triggers start in shadow; `changed` tells the agents page to reload the agent's skill states.
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
    NhAssistantAgentWorkSetupComponent,
    NhAssistantRunSettingsFormComponent,
    NhAssistantSkillFormComponent,
    NhAssistantTriggerFormComponent,
    NhAssistantAccountNamesErrorComponent
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

  readonly agent = input.required<AdminAgent>();
  /** Opens the guided setup at once, for an agent that was just created to work on its own. */
  readonly startSetup = input(false);
  /** The tool catalog, to show what the agent's tool selectors offer in the skill forms. */
  readonly tools = input<readonly ToolCatalogEntry[]>([]);
  /** A change of the run configuration changed the agent's skills or state. */
  readonly changed = output<void>();

  readonly id = `nh-assistant-run-configuration-${nextId++}`;
  readonly maxReason = runConfigLimits.reason;
  readonly skillNameKeys = skillNameKeys;
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
  /** The name of the account the agent answers to, once known. */
  readonly accountName = computed(() => {
    const actorId = this.configuration()?.runSettings?.serviceAccountActorId;
    return actorId ? this.accountNames.names().get(actorId) ?? null : null;
  });
  /** The agent's name; an agent in code names itself with a translation key. */
  readonly agentName = computed(() => {
    this.language();
    const agent = this.agent();
    return this.translated(codeTextKeys(agent.source, agent.displayName), agent.displayName);
  });

  /** The skill being changed; `null` with the skill form open adds a new skill. */
  readonly editingSkill = signal<DataSkill | null>(null);
  /** The trigger being changed; `null` with the trigger form open adds a new trigger. */
  readonly editingTrigger = signal<DataTrigger | null>(null);
  readonly comparison = signal<{ skillId: string; result: SkillComparison | null; failed: boolean } | null>(null);
  readonly versions = signal<{ skillId: string; items: DataSkillVersion[] | null; failed: boolean } | null>(null);

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

  load(openSetup = false): void {
    this.loading.set(true);
    this.loadFailed.set(false);
    this.loadSubscription?.unsubscribe();
    this.loadSubscription = forkJoin([this.admin.getRunCatalog(), this.admin.getAgentConfiguration(this.agent().id)]).subscribe({
      next: ([catalog, configuration]) => {
        this.catalog.set(catalog);
        this.configuration.set(configuration);
        this.loading.set(false);
        this.accountNames.load([
          configuration.runSettings?.serviceAccountActorId,
          ...configuration.dataSkills.map(skill => skill.changedBy)
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

  /** A form saved: show what happened and reload, which every change may affect. */
  onSetupCompleted(live: boolean): void {
    this.finish(live ? 'nh-assistant.admin.run-config.setup.saved-live' : 'nh-assistant.admin.run-config.setup.saved-shadow');
  }

  onSettingsSaved(): void {
    this.finish('nh-assistant.admin.run-config.settings.saved');
  }

  onSkillSaved(result: SkillFormSaved): void {
    this.finish(result.candidate
      ? 'nh-assistant.admin.run-config.skills.saved-candidate'
      : result.added ? 'nh-assistant.admin.run-config.skills.added' : 'nh-assistant.admin.run-config.skills.saved');
  }

  onTriggerSaved(added: boolean): void {
    this.finish(added ? 'nh-assistant.admin.run-config.triggers.added' : 'nh-assistant.admin.run-config.triggers.saved');
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
    const form = this.form();
    this.form.set(null);
    this.confirming.set(null);
    this.error.set(null);
    // Back to the action that opened the form.
    afterNextRender(() => this.host.nativeElement.querySelector<HTMLElement>(`[data-open-form="${form}"]`)?.focus(), { injector: this.injector });
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

  private finish(savedKey: string): void {
    this.form.set(null);
    this.saved.set(savedKey);
    this.changed.emit();
    this.load();
    afterNextRender(() => this.host.nativeElement.querySelector<HTMLElement>('[data-saved]')?.focus(), { injector: this.injector });
  }

  /** Opens one form at a time and moves the focus into it. */
  private open(form: Form): void {
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
        this.comparison.set(null);
        this.versions.set(null);
        this.finish(savedKey);
      }
    });
  }
}

function blank(value: string): string | null {
  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : null;
}
