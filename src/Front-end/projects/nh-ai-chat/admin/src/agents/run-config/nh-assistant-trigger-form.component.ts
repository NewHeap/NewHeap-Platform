import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import {
  DataTrigger,
  DataTriggerRequest,
  NhAssistantAdminApiService,
  RunCatalog,
  TriggerConflict,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantPickerComponent as NhAssistantPickerComponent,
  ɵNhAssistantPickerOption as NhAssistantPickerOption,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe
} from '@newheap/platform-ai-chat';
import { RunConfigForm } from './run-config-form';
import {
  RunConfigErrors,
  ScheduleTarget,
  blank,
  checkWhole,
  cronExamples,
  dashCase,
  eventOptions,
  WorksOnStatement,
  requireText,
  runConfigLimits,
  subjectSetOptions,
  systemSubjectType,
  timeZoneOptions,
  toNumber,
  withScheduleTargetError,
  worksOnCovers
} from './run-config-shared';

let nextId = 0;

const conflicts: TriggerConflict[] = ['coalesce', 'queue', 'supersede', 'ignore'];

/**
 * Adds or changes a trigger defined as data: an event of the application or a schedule starts a skill
 * of the agent, or lets the agent choose. A schedule runs for every subject in a set or, for an agent
 * that works on the whole application, for the whole application: once each time, or once per
 * division for an agent that works per division. A new trigger starts in shadow.
 */
@Component({
  selector: 'nh-assistant-trigger-form',
  standalone: true,
  imports: [TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent, NhAssistantPickerComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-trigger-form.component.html',
  styleUrls: ['../../nh-assistant-admin-shared.scss', './run-config-forms.scss']
})
export class NhAssistantTriggerFormComponent extends RunConfigForm implements OnInit {
  private readonly admin = inject(NhAssistantAdminApiService);

  readonly agentId = input.required<string>();
  readonly catalog = input<RunCatalog | null>(null);
  /**
   * What the agent works on: events about other object types are not offered, and a schedule runs
   * for the whole application only for an agent that works on it.
   */
  readonly worksOn = input<WorksOnStatement | null>(null);
  /** The agent's skills, in code and as data, a trigger can start. */
  readonly skills = input<readonly NhAssistantPickerOption[]>([]);
  /** The trigger to change; `null` adds one. */
  readonly trigger = input<DataTrigger | null>(null);
  readonly takenIds = input<readonly string[]>([]);
  readonly saved = output<{ trigger: DataTrigger; added: boolean }>();
  readonly cancelled = output<void>();

  readonly id = `nh-assistant-trigger-form-${nextId++}`;
  readonly maxReason = runConfigLimits.reason;
  readonly maxMessage = runConfigLimits.message;
  readonly examples = cronExamples;
  readonly timeZones = timeZoneOptions().filter(zone => zone.value !== 'UTC');
  protected readonly fields = [
    'triggerId', 'source', 'event', 'debounceSeconds', 'cron', 'timeZone', 'subjectSet', 'message', 'skill', 'conflict', 'reason'
  ];

  readonly triggerId = signal('');
  readonly source = signal<'event' | 'schedule'>('event');
  readonly event = signal('');
  readonly cron = signal('');
  readonly timeZone = signal('');
  readonly subjectSet = signal('');
  /** The administrator's choice of what a schedule runs for; see `target`. */
  readonly scheduleTarget = signal<ScheduleTarget>('application');
  readonly message = signal('');
  readonly skill = signal('');
  readonly conflict = signal<TriggerConflict>('coalesce');
  readonly debounce = signal('120');
  readonly reason = signal('');

  readonly editing = computed(() => this.trigger() !== null);
  readonly events = computed(() => eventOptions(this.catalog(), this.worksOn()));
  readonly subjectSets = computed(() => subjectSetOptions(this.catalog()));
  /** A schedule may run for the whole application only for an agent that works on it. */
  readonly offersApplication = computed(() => worksOnCovers(this.worksOn(), systemSubjectType));
  /** What the schedule runs for: the whole application while the agent works on it, otherwise a set. */
  readonly target = computed<ScheduleTarget>(() => this.offersApplication() ? this.scheduleTarget() : 'set');
  /** The agent's work for the whole application runs once per division. */
  readonly perDivision = computed(() => this.worksOn()?.perDivision === true);
  /** A stored schedule for the whole application of an agent that no longer works on it. */
  readonly applicationDropped = computed(() => {
    const trigger = this.trigger();
    return trigger?.source === 'schedule' && !trigger.subjectSet && !this.offersApplication();
  });
  readonly conflictOptions = computed<NhAssistantPickerOption[]>(() => {
    this.language();
    return conflicts.map(conflict => ({
      value: conflict,
      label: this.t(`nh-assistant.admin.run-config.triggers.conflicts.${conflict}`),
      description: this.t(`nh-assistant.admin.run-config.triggers.conflict-explain.${conflict}`)
    }));
  });

  ngOnInit(): void {
    const trigger = this.trigger();
    this.triggerId.set(trigger?.triggerId ?? '');
    this.source.set(trigger?.source === 'schedule' ? 'schedule' : 'event');
    this.event.set(trigger?.event ?? '');
    this.cron.set(trigger?.cron ?? '');
    this.timeZone.set(trigger?.timeZone ?? '');
    this.subjectSet.set(trigger?.subjectSet ?? '');
    // A stored schedule without a subject set runs for the whole application; a new one starts there
    // for an agent that works on it.
    this.scheduleTarget.set(trigger?.source === 'schedule' && trigger.subjectSet ? 'set' : 'application');
    this.message.set(trigger?.message ?? '');
    this.skill.set(trigger?.skillId ?? '');
    this.conflict.set(trigger?.conflict ?? 'coalesce');
    this.debounce.set(String(trigger?.debounceSeconds ?? 120));
  }

  /** A schedule skips a tick while a run is busy by default; an event coalesces. */
  setSource(source: 'event' | 'schedule'): void {
    this.source.set(source);
    this.touch('source');
    if (!this.editing()) {
      this.conflict.set(source === 'schedule' ? 'ignore' : 'coalesce');
    }
  }

  setTarget(target: ScheduleTarget): void {
    this.scheduleTarget.set(target);
    this.touch('subjectSet');
  }

  useExample(cron: string): void {
    this.cron.set(cron);
    this.touch('cron');
  }

  save(): void {
    const editing = this.trigger();
    const triggerId = editing?.triggerId ?? this.triggerId().trim();
    const schedule = this.source() === 'schedule';
    const forSet = schedule && this.target() === 'set';
    const errors: RunConfigErrors = {};
    if (!editing) {
      if (triggerId.length === 0) {
        errors['triggerId'] = { code: 'required' };
      } else if (!dashCase.test(triggerId) || triggerId.length > 64) {
        errors['triggerId'] = { code: 'dash-case' };
      } else if (this.takenIds().includes(triggerId)) {
        errors['triggerId'] = { code: 'taken' };
      }
    }
    if (schedule) {
      requireText(errors, 'cron', this.cron());
      if (forSet && this.subjectSet() === '') {
        errors['subjectSet'] = { code: 'required' };
      }
      requireText(errors, 'message', this.message(), this.maxMessage);
    } else {
      if (this.event() === '') {
        errors['event'] = { code: 'required' };
      }
      checkWhole(errors, 'debounceSeconds', this.debounce(), ...runConfigLimits.debounce, true);
    }

    const request: DataTriggerRequest = {
      source: this.source(),
      skill: blank(this.skill()),
      event: schedule ? null : blank(this.event()),
      cron: schedule ? blank(this.cron()) : null,
      timeZone: schedule ? blank(this.timeZone()) : null,
      // Without a subject set the schedule runs for the whole application.
      subjectSet: forSet ? blank(this.subjectSet()) : null,
      message: schedule ? blank(this.message()) : null,
      conflict: this.conflict(),
      debounceSeconds: schedule ? null : toNumber(this.debounce()),
      expectedVersion: editing?.version ?? null,
      reason: blank(this.reason())
    };
    this.submit(errors, () => this.admin.saveDataTrigger(this.agentId(), triggerId, request), trigger => this.saved.emit({
      trigger,
      added: editing === null
    }));
  }

  protected override mapServerErrors(errors: RunConfigErrors): RunConfigErrors {
    return withScheduleTargetError(errors, this.source() === 'schedule' ? this.target() : 'set');
  }
}
