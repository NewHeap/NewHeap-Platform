import { ChangeDetectionStrategy, Component, OnInit, afterNextRender, computed, inject, input, output, signal } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Observable, concat, defer, of, tap } from 'rxjs';
import {
  AgentAccountability,
  NhAssistantAdminApiService,
  RunCatalog,
  ToolCatalogEntry,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantPickerComponent as NhAssistantPickerComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe
} from '@newheap/platform-ai-chat';
import { NhAssistantAccountPickerComponent } from './nh-assistant-account-picker.component';
import { RunConfigForm } from './run-config-form';
import {
  RunConfigErrors,
  blank,
  checkBudget,
  checkWhole,
  cronExamples,
  dashCase,
  describePolicy,
  eventOptions,
  policyOptions,
  requireText,
  ruleOptions,
  runConfigLimits,
  toolChoices,
  subjectSetOptions,
  timeZoneOptions,
  toDashCase,
  toNumber,
  withBudgetField
} from './run-config-shared';

let nextId = 0;

export type WorkSetupStep = 'when' | 'what' | 'who' | 'limits' | 'review';

const steps: WorkSetupStep[] = ['when', 'what', 'who', 'limits', 'review'];

/** The step whose fields a server error belongs to. */
const stepOfField: Record<string, WorkSetupStep> = {
  triggerId: 'when', source: 'when', event: 'when', debounceSeconds: 'when', cron: 'when', timeZone: 'when',
  subjectSet: 'when', message: 'when', skill: 'when', conflict: 'when',
  skillId: 'what', name: 'what', description: 'what', instructions: 'what', tools: 'what', ruleHandler: 'what', profile: 'what',
  accountability: 'who', serviceAccountActorId: 'who',
  approvalPolicy: 'limits', maxTokensPerRun: 'limits', dailyToolCalls: 'limits', dailyTokens: 'limits'
};

/**
 * Lets an agent created in the administration work on its own in one flow: when it starts, what it
 * does, on whose behalf, who approves its proposals and its limits, and a review. Saving adds the run
 * settings, a skill and a trigger that starts it; the skill and the trigger start in shadow, where
 * nobody is notified and nothing executes, and can be put live at the end.
 */
@Component({
  selector: 'nh-assistant-agent-work-setup',
  standalone: true,
  imports: [TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent, NhAssistantPickerComponent, NhAssistantAccountPickerComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-agent-work-setup.component.html',
  styleUrls: ['../../nh-assistant-admin-shared.scss', './run-config-forms.scss']
})
export class NhAssistantAgentWorkSetupComponent extends RunConfigForm implements OnInit {
  private readonly admin = inject(NhAssistantAdminApiService);

  readonly agentId = input.required<string>();
  readonly agentName = input('');
  readonly catalog = input<RunCatalog | null>(null);
  readonly toolSelectors = input<readonly string[]>([]);
  /** The tool catalog, to show what each of the agent's tool selectors offers. */
  readonly toolCatalog = input<readonly ToolCatalogEntry[]>([]);
  readonly toolChoices = computed(() => toolChoices(this.toolSelectors(), this.toolCatalog()));
  readonly takenSkillIds = input<readonly string[]>([]);
  readonly takenTriggerIds = input<readonly string[]>([]);
  /** The setup saved; `live` tells whether the skill and the trigger were put live. */
  readonly completed = output<{ live: boolean }>();
  readonly cancelled = output<void>();

  readonly id = `nh-assistant-work-setup-${nextId++}`;
  readonly steps = steps;
  readonly examples = cronExamples;
  readonly timeZones = timeZoneOptions().filter(zone => zone.value !== 'UTC');
  readonly accountabilities: AgentAccountability[] = ['service-account', 'subject-responsible', 'agent'];
  readonly maxDescription = runConfigLimits.description;
  readonly maxInstructions = runConfigLimits.instructions;
  readonly maxMessage = runConfigLimits.message;
  protected readonly fields = Object.keys(stepOfField);

  readonly step = signal<WorkSetupStep>('when');
  /** Saved parts, so a retry after a refusal does not add them again. */
  private readonly savedSettingsVersion = signal<number | null>(null);
  private readonly savedSkill = signal(false);
  readonly done = signal(false);

  // When.
  readonly source = signal<'event' | 'schedule'>('event');
  readonly event = signal('');
  readonly cron = signal('');
  readonly timeZone = signal('');
  readonly subjectSet = signal('');
  readonly message = signal('');
  // What.
  readonly name = signal('');
  readonly description = signal('');
  readonly ruleHandler = signal('');
  readonly instructions = signal('');
  readonly tools = signal<string[]>([]);
  // Who.
  readonly accountability = signal<AgentAccountability>('service-account');
  readonly serviceAccount = signal('');
  // Approval and limits.
  readonly approvalPolicy = signal('');
  readonly maxTokensPerRun = signal('');
  readonly dailyToolCalls = signal('');
  readonly dailyTokens = signal('');

  readonly stepIndex = computed(() => steps.indexOf(this.step()));
  readonly skillId = computed(() => toDashCase(this.name()));
  readonly triggerId = computed(() => {
    const base = this.source() === 'schedule' ? `${this.skillId()}-schedule` : `${this.skillId()}-${this.event()}`;
    return toDashCase(base).slice(0, 64).replace(/-+$/, '');
  });
  readonly accountSearch = computed(() => this.catalog()?.accountSearch === true);
  readonly events = computed(() => eventOptions(this.catalog()));
  readonly subjectSets = computed(() => subjectSetOptions(this.catalog()));
  readonly rules = computed(() => ruleOptions(this.catalog()));
  readonly policies = computed(() => {
    this.language();
    return policyOptions(this.catalog(), summary => describePolicy(summary, (key, params) => this.t(key, params)));
  });
  readonly eventDescription = computed(() => this.events().find(option => option.value === this.event())?.description ?? null);
  readonly subjectSetDescription = computed(() => this.subjectSets().find(option => option.value === this.subjectSet())?.description ?? null);
  readonly ruleDescription = computed(() => this.rules().find(option => option.value === this.ruleHandler())?.description ?? null);
  readonly policyDescription = computed(() => this.policies().find(option => option.value === this.approvalPolicy())?.description ?? null);

  ngOnInit(): void {
    // A new skill starts with every tool of the agent; the administrator narrows it.
    this.tools.set([...this.toolSelectors()]);
  }

  stepState(step: WorkSetupStep): 'current' | 'done' | 'next' {
    const index = steps.indexOf(step);
    return index === this.stepIndex() ? 'current' : index < this.stepIndex() ? 'done' : 'next';
  }

  setSource(source: 'event' | 'schedule'): void {
    this.source.set(source);
    this.touch('source');
  }

  setAccountability(accountability: AgentAccountability): void {
    this.accountability.set(accountability);
    this.touch('accountability');
    this.touch('serviceAccountActorId');
  }

  toggleTool(selector: string, selected: boolean): void {
    this.tools.update(tools => selected ? [...tools.filter(tool => tool !== selector), selector] : tools.filter(tool => tool !== selector));
    this.touch('tools');
  }

  useExample(cron: string): void {
    this.cron.set(cron);
    this.touch('cron');
  }

  back(): void {
    const index = this.stepIndex();
    if (index > 0) {
      this.go(steps[index - 1]);
    }
  }

  next(): void {
    const errors = this.validate(this.step());
    this.serverError.set(null);
    this.clientErrors.set(errors);
    if (Object.keys(errors).length > 0) {
      this.focusFirstInvalid();
      return;
    }
    const index = this.stepIndex();
    if (index < steps.length - 1) {
      this.go(steps[index + 1]);
    }
  }

  /** Saves the run settings, the skill and the trigger, in that order, and stops at a refusal. */
  save(live = false): void {
    const errors: RunConfigErrors = {};
    for (const step of steps) {
      Object.assign(errors, this.validate(step));
    }
    const first = Object.keys(errors)[0];
    if (first) {
      this.go(stepOfField[first] ?? 'when', false);
      this.submit(errors, () => of(null), () => undefined);
      return;
    }

    const agentId = this.agentId();
    const skillId = this.skillId();
    const triggerId = this.triggerId();
    const schedule = this.source() === 'schedule';
    const settings = defer(() => this.admin.saveRunSettings(agentId, {
      accountability: this.accountability(),
      serviceAccountActorId: this.accountability() === 'service-account' ? blank(this.serviceAccount()) : null,
      approvalPolicy: blank(this.approvalPolicy()),
      maxTokensPerRun: toNumber(this.maxTokensPerRun()),
      dailyToolCalls: toNumber(this.dailyToolCalls()),
      dailyTokens: toNumber(this.dailyTokens()),
      expectedVersion: this.savedSettingsVersion(),
      reason: null
    }).pipe(tap(saved => this.savedSettingsVersion.set(saved.version))));
    const skill = defer(() => this.savedSkill()
      ? of(null)
      : this.admin.saveDataSkill(agentId, skillId, {
        description: this.description().trim(),
        instructions: blank(this.instructions()),
        tools: this.tools(),
        ruleHandler: blank(this.ruleHandler()),
        expectedVersion: null,
        reason: null
      }).pipe(tap(() => this.savedSkill.set(true))));
    const trigger = defer(() => this.admin.saveDataTrigger(agentId, triggerId, {
      source: this.source(),
      skill: skillId,
      event: schedule ? null : blank(this.event()),
      cron: schedule ? blank(this.cron()) : null,
      timeZone: schedule ? blank(this.timeZone()) : null,
      subjectSet: schedule ? blank(this.subjectSet()) : null,
      message: schedule ? blank(this.message()) : null,
      conflict: schedule ? 'ignore' : 'coalesce',
      debounceSeconds: schedule ? null : 120,
      expectedVersion: null,
      reason: null
    }));
    const goLive: Observable<unknown>[] = live
      ? [
        defer(() => this.admin.setSkillShadow(agentId, skillId, { mode: 'live', reason: null })),
        defer(() => this.admin.setDataTriggerShadow(agentId, triggerId, { mode: 'live', reason: null }))
      ]
      : [];
    this.submit({}, () => concat(settings, skill, trigger, ...goLive), () => {
      this.done.set(true);
      this.completed.emit({ live });
    }, () => {
      const field = Object.keys(this.fieldErrors())[0];
      this.go(field ? stepOfField[field] ?? 'review' : 'review', false);
    });
  }

  protected override mapServerErrors(errors: RunConfigErrors): RunConfigErrors {
    const mapped = withBudgetField(errors, this.dailyToolCalls());
    // The skill id is derived from the name.
    if (mapped['skillId']) {
      const { skillId, ...rest } = mapped;
      return { ...rest, name: skillId };
    }
    return mapped;
  }

  private go(step: WorkSetupStep, focus = true): void {
    this.step.set(step);
    if (focus) {
      afterNextRender(() => this.host.nativeElement.querySelector<HTMLElement>(`[data-step="${step}"] legend`)?.focus(), { injector: this.injector });
    }
  }

  private validate(step: WorkSetupStep): RunConfigErrors {
    const errors: RunConfigErrors = {};
    switch (step) {
      case 'when':
        if (this.source() === 'event') {
          if (this.event() === '') {
            errors['event'] = { code: 'required' };
          }
        } else {
          requireText(errors, 'cron', this.cron());
          if (this.subjectSet() === '') {
            errors['subjectSet'] = { code: 'required' };
          }
          requireText(errors, 'message', this.message(), this.maxMessage);
        }
        break;
      case 'what':
        requireText(errors, 'name', this.name());
        if (!errors['name'] && (!dashCase.test(this.skillId()) || this.skillId().length === 0)) {
          errors['name'] = { code: 'name-letters' };
        } else if (!errors['name'] && this.takenSkillIds().includes(this.skillId())) {
          errors['name'] = { code: 'taken' };
        }
        requireText(errors, 'description', this.description(), this.maxDescription);
        if (this.instructions().length > this.maxInstructions) {
          errors['instructions'] = { code: 'too-long', params: { max: this.maxInstructions } };
        }
        if (this.ruleHandler() === '' && this.instructions().trim() === '') {
          errors['instructions'] = { code: 'instructions-for-model' };
        }
        break;
      case 'who':
        if (this.accountability() === 'service-account' && this.serviceAccount().trim() === '') {
          errors['serviceAccountActorId'] = { code: 'required' };
        }
        break;
      case 'limits':
        checkWhole(errors, 'maxTokensPerRun', this.maxTokensPerRun(), 1);
        checkBudget(errors, this.dailyToolCalls(), this.dailyTokens());
        break;
    }
    return errors;
  }
}
