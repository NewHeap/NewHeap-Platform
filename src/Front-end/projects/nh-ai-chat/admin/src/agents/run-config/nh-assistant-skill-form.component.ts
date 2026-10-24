import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import {
  AssistantDataClassification,
  DataSkill,
  DataSkillRequest,
  InstructionAssetReference,
  NhAssistantAdminApiService,
  RunCatalog,
  ToolCatalogEntry,
  SkillApplyMode,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantPickerComponent as NhAssistantPickerComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe
} from '@newheap/platform-ai-chat';
import { NhAssistantPackageLockComponent } from '../agent-packages/nh-assistant-package-lock.component';
import { NhAssistantPackageLockedMarkComponent } from '../agent-packages/nh-assistant-package-locked-mark.component';
import { PackageLockView } from '../agent-packages/package-parts';
import { NhAssistantFileLimitsFieldsComponent } from './nh-assistant-file-limits-fields.component';
import { InstructionsSource, NhAssistantInstructionsAssetComponent, sameInstructionAsset } from './nh-assistant-instructions-asset.component';
import { RunConfigForm } from './run-config-form';
import {
  RunConfigErrors,
  blank,
  checkBudget,
  checkWhole,
  dashCase,
  dataClassifications,
  describePolicy,
  FileLimitsDraft,
  numberText,
  policyOptions,
  profileOptions,
  requireText,
  ruleOptions,
  runConfigLimits,
  toolChoices,
  toNumber,
  withBudgetField
} from './run-config-shared';
import { warningKeys } from '../agent-texts';

let nextId = 0;

/** What a saved skill form tells its host. */
export interface SkillFormSaved {
  skill: DataSkill;
  /** The change runs as the skill's candidate in shadow. */
  candidate: boolean;
  added: boolean;
}

/**
 * Adds or changes a skill defined as data: what it is for, how it works (fixed rules or the model,
 * with instructions), its tools within the agent's own, and its approval and limits. A change is
 * evaluated in shadow by default. Fields the skill's agent package locks show read-only.
 */
@Component({
  selector: 'nh-assistant-skill-form',
  standalone: true,
  imports: [
    TranslatePipe,
    NhAssistantTranslatePipe,
    NhAssistantIconComponent,
    NhAssistantPickerComponent,
    NhAssistantFileLimitsFieldsComponent,
    NhAssistantInstructionsAssetComponent,
    NhAssistantPackageLockComponent,
    NhAssistantPackageLockedMarkComponent
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-skill-form.component.html',
  styleUrls: ['../../nh-assistant-admin-shared.scss', './run-config-forms.scss']
})
export class NhAssistantSkillFormComponent extends RunConfigForm implements OnInit {
  private readonly admin = inject(NhAssistantAdminApiService);

  readonly agentId = input.required<string>();
  readonly catalog = input<RunCatalog | null>(null);
  /** The agent's own tool selectors; a skill's tools stay within them. */
  readonly toolSelectors = input<readonly string[]>([]);
  /** The tool catalog, to show what each of the agent's tool selectors offers. */
  readonly toolCatalog = input<readonly ToolCatalogEntry[]>([]);
  readonly toolChoices = computed(() => toolChoices(this.toolSelectors(), this.toolCatalog()));
  /** The skill to change; `null` adds one. */
  readonly skill = input<DataSkill | null>(null);
  /** Skill ids in use, to refuse a duplicate before sending. */
  readonly takenIds = input<readonly string[]>([]);
  /** What the skill's agent package locks (part `skill:{id}`); `null` without a link. */
  override readonly lock = input<PackageLockView | null>(null);
  readonly saved = output<SkillFormSaved>();
  readonly cancelled = output<void>();

  readonly id = `nh-assistant-skill-form-${nextId++}`;
  readonly maxReason = runConfigLimits.reason;
  readonly maxDescription = runConfigLimits.description;
  readonly maxInstructions = runConfigLimits.instructions;
  protected readonly fields = [
    'skillId', 'description', 'instructions', 'instructionsAsset', 'instructionsAsset.hash', 'tools', 'ruleHandler', 'profile', 'dataClassification', 'approvalPolicy',
    'maxTokensPerRun', 'dailyToolCalls', 'dailyTokens', 'apply', 'startsOff', 'reason',
    'maxFileBytes', 'maxFilePages', 'maxFilesPerTurn', 'maxFileTokensPerTurn'
  ];

  readonly skillId = signal('');
  readonly description = signal('');
  readonly instructions = signal('');
  /** Whether the version's instructions are written here or come from one of the application's assets. */
  readonly instructionsSource = signal<InstructionsSource>('inline');
  /** The application's asset the version names, once chosen and read. */
  readonly instructionsAsset = signal<InstructionAssetReference | null>(null);
  readonly tools = signal<string[]>([]);
  readonly profile = signal('');
  /** The skill's data classification; empty uses the agent's. */
  readonly dataClassification = signal('');
  /** Whether a run in the skill reads files when the agent accepts them too; off for a new skill. */
  readonly acceptsFiles = signal(false);
  /** The skill's lower file limits, next to accepting files; empty keeps the agent's. */
  readonly fileLimits = new FileLimitsDraft();
  readonly ruleHandler = signal('');
  readonly approvalPolicy = signal('');
  readonly maxTokens = signal('');
  readonly dailyToolCalls = signal('');
  readonly dailyTokens = signal('');
  readonly apply = signal<SkillApplyMode>('shadow');
  /** A new skill takes work at once; without it, it starts off until someone switches it on. */
  readonly switchOnNow = signal(true);
  readonly reason = signal('');

  readonly editing = computed(() => this.skill() !== null);
  /** Whether the application offers its text assets as instructions. */
  readonly assetsAvailable = computed(() => this.catalog()?.instructionAssets === true);
  /** The problem of the asset the saved version names, such as `asset-changed`, while it is still the choice. */
  readonly assetProblem = computed(() => {
    const skill = this.skill();
    return isAssetProblem(skill?.problem) && sameInstructionAsset(skill!.instructionsAsset, this.instructionsAsset()) ? skill!.problem : null;
  });
  /** The warnings of the skill as it was saved, until this form saves it again. */
  readonly warnings = computed(() => this.skill()?.warnings ?? []);
  readonly warningKeys = warningKeys;
  readonly rules = computed(() => ruleOptions(this.catalog()));
  readonly profiles = computed(() => profileOptions(this.catalog()));
  readonly classifications = computed(() => {
    this.language();
    return dataClassifications.map(value => ({
      value,
      label: this.t(`nh-assistant.admin.run-config.skills.classification.${value}`),
      description: this.t(`nh-assistant.admin.run-config.skills.classification.${value}-hint`)
    }));
  });
  readonly policies = computed(() => {
    this.language();
    return policyOptions(this.catalog(), summary => describePolicy(summary, (key, params) => this.t(key, params)));
  });

  ngOnInit(): void {
    const skill = this.skill();
    this.skillId.set(skill?.skillId ?? '');
    this.description.set(skill?.description ?? '');
    this.instructions.set(skill?.instructions ?? '');
    this.instructionsSource.set(skill?.instructionsAsset ? 'asset' : 'inline');
    this.instructionsAsset.set(skill?.instructionsAsset ?? null);
    this.tools.set([...(skill?.tools ?? [])]);
    this.profile.set(skill?.profile ?? '');
    this.dataClassification.set(skill?.dataClassification ?? '');
    this.acceptsFiles.set(skill?.acceptsFiles ?? false);
    this.fileLimits.load(skill);
    this.ruleHandler.set(skill?.ruleHandler ?? '');
    this.approvalPolicy.set(skill?.approvalPolicy ?? '');
    this.maxTokens.set(numberText(skill?.maxTokensPerRun));
    this.dailyToolCalls.set(numberText(skill?.dailyToolCalls));
    this.dailyTokens.set(numberText(skill?.dailyTokens));
    // A real change is evaluated in shadow first; a small correction can go live at once.
    this.apply.set('shadow');
  }

  toggleTool(selector: string, selected: boolean): void {
    this.tools.update(tools => selected
      ? [...tools.filter(tool => tool !== selector), selector]
      : tools.filter(tool => tool !== selector));
    this.touch('tools');
  }

  save(): void {
    const editing = this.skill();
    const skillId = editing?.skillId ?? this.skillId().trim();
    const errors: RunConfigErrors = {};
    if (!editing) {
      if (skillId.length === 0) {
        errors['skillId'] = { code: 'required' };
      } else if (!dashCase.test(skillId) || skillId.length > 64) {
        errors['skillId'] = { code: 'dash-case' };
      } else if (this.takenIds().includes(skillId)) {
        errors['skillId'] = { code: 'taken' };
      }
    }
    requireText(errors, 'description', this.description(), this.maxDescription);
    const fromAsset = this.instructionsSource() === 'asset';
    if (!fromAsset && this.instructions().length > this.maxInstructions) {
      errors['instructions'] = { code: 'too-long', params: { max: this.maxInstructions } };
    }
    if (fromAsset && this.instructionsAsset() === null) {
      errors['instructionsAsset'] = { code: 'required' };
    }
    checkWhole(errors, 'maxTokensPerRun', this.maxTokens(), 1);
    checkBudget(errors, this.dailyToolCalls(), this.dailyTokens());
    if (this.acceptsFiles()) {
      this.fileLimits.check(errors, this.catalog()?.fileLimits);
    }

    const request: DataSkillRequest = {
      description: this.description().trim(),
      instructions: fromAsset ? null : blank(this.instructions()),
      instructionsAsset: fromAsset ? this.instructionsAsset() : null,
      tools: this.tools(),
      profile: blank(this.profile()),
      dataClassification: (blank(this.dataClassification()) as AssistantDataClassification | null),
      acceptsFiles: this.acceptsFiles(),
      ...this.fileLimits.toRequest(this.acceptsFiles()),
      ruleHandler: blank(this.ruleHandler()),
      approvalPolicy: blank(this.approvalPolicy()),
      maxTokensPerRun: toNumber(this.maxTokens()),
      dailyToolCalls: toNumber(this.dailyToolCalls()),
      dailyTokens: toNumber(this.dailyTokens()),
      expectedVersion: editing?.version ?? null,
      reason: blank(this.reason()),
      ...(editing ? { apply: this.apply() } : {}),
      ...(!editing && !this.switchOnNow() ? { startsOff: true } : {})
    };
    this.submit(errors, () => this.admin.saveDataSkill(this.agentId(), skillId, request), skill => this.saved.emit({
      skill,
      candidate: editing !== null && this.apply() === 'shadow',
      added: editing === null
    }));
  }

  protected override mapServerErrors(errors: RunConfigErrors): RunConfigErrors {
    return withAssetField(withBudgetField(errors, this.dailyToolCalls()));
  }
}

/** Whether a problem of a skill version concerns the asset it takes its instructions from. */
function isAssetProblem(problem: string | null | undefined): problem is string {
  return problem === 'no-asset-source' || (problem?.startsWith('asset-') ?? false);
}

/**
 * The save's errors of a reference at the one asset field: another hash (`instructionsAsset.hash`)
 * says the asset's text changed since it was read.
 */
export function withAssetField(errors: RunConfigErrors): RunConfigErrors {
  const { ['instructionsAsset.hash']: hash, ...rest } = errors;
  return hash ? { ...rest, instructionsAsset: { code: 'changed' } } : rest;
}
