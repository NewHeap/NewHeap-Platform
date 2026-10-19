import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import {
  DataSkill,
  DataSkillRequest,
  NhAssistantAdminApiService,
  RunCatalog,
  ToolCatalogEntry,
  SkillApplyMode,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantPickerComponent as NhAssistantPickerComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe
} from '@newheap/platform-ai-chat';
import { RunConfigForm } from './run-config-form';
import {
  RunConfigErrors,
  blank,
  checkBudget,
  checkWhole,
  dashCase,
  describePolicy,
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
 * evaluated in shadow by default.
 */
@Component({
  selector: 'nh-assistant-skill-form',
  standalone: true,
  imports: [TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent, NhAssistantPickerComponent],
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
  readonly saved = output<SkillFormSaved>();
  readonly cancelled = output<void>();

  readonly id = `nh-assistant-skill-form-${nextId++}`;
  readonly maxReason = runConfigLimits.reason;
  readonly maxDescription = runConfigLimits.description;
  readonly maxInstructions = runConfigLimits.instructions;
  protected readonly fields = [
    'skillId', 'description', 'instructions', 'tools', 'ruleHandler', 'profile', 'approvalPolicy',
    'maxTokensPerRun', 'dailyToolCalls', 'dailyTokens', 'apply', 'reason'
  ];

  readonly skillId = signal('');
  readonly description = signal('');
  readonly instructions = signal('');
  readonly tools = signal<string[]>([]);
  readonly profile = signal('');
  readonly ruleHandler = signal('');
  readonly approvalPolicy = signal('');
  readonly maxTokens = signal('');
  readonly dailyToolCalls = signal('');
  readonly dailyTokens = signal('');
  readonly apply = signal<SkillApplyMode>('shadow');
  readonly reason = signal('');

  readonly editing = computed(() => this.skill() !== null);
  readonly rules = computed(() => ruleOptions(this.catalog()));
  readonly profiles = computed(() => profileOptions(this.catalog()));
  readonly policies = computed(() => {
    this.language();
    return policyOptions(this.catalog(), summary => describePolicy(summary, (key, params) => this.t(key, params)));
  });

  ngOnInit(): void {
    const skill = this.skill();
    this.skillId.set(skill?.skillId ?? '');
    this.description.set(skill?.description ?? '');
    this.instructions.set(skill?.instructions ?? '');
    this.tools.set([...(skill?.tools ?? [])]);
    this.profile.set(skill?.profile ?? '');
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
    if (this.instructions().length > this.maxInstructions) {
      errors['instructions'] = { code: 'too-long', params: { max: this.maxInstructions } };
    }
    checkWhole(errors, 'maxTokensPerRun', this.maxTokens(), 1);
    checkBudget(errors, this.dailyToolCalls(), this.dailyTokens());

    const request: DataSkillRequest = {
      description: this.description().trim(),
      instructions: blank(this.instructions()),
      tools: this.tools(),
      profile: blank(this.profile()),
      ruleHandler: blank(this.ruleHandler()),
      approvalPolicy: blank(this.approvalPolicy()),
      maxTokensPerRun: toNumber(this.maxTokens()),
      dailyToolCalls: toNumber(this.dailyToolCalls()),
      dailyTokens: toNumber(this.dailyTokens()),
      expectedVersion: editing?.version ?? null,
      reason: blank(this.reason()),
      ...(editing ? { apply: this.apply() } : {})
    };
    this.submit(errors, () => this.admin.saveDataSkill(this.agentId(), skillId, request), skill => this.saved.emit({
      skill,
      candidate: editing !== null && this.apply() === 'shadow',
      added: editing === null
    }));
  }

  protected override mapServerErrors(errors: RunConfigErrors): RunConfigErrors {
    return withBudgetField(errors, this.dailyToolCalls());
  }
}
