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
  blank,
  checkWhole,
  cronExamples,
  dashCase,
  eventOptions,
  requireText,
  runConfigLimits,
  subjectSetOptions,
  timeZoneOptions,
  toNumber
} from './run-config-shared';

let nextId = 0;

const conflicts: TriggerConflict[] = ['coalesce', 'queue', 'supersede', 'ignore'];

/**
 * Adds or changes a trigger defined as data: an event of the application or a schedule over a set of
 * subjects starts a skill of the agent, or lets the agent choose. A new trigger starts in shadow.
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
  readonly message = signal('');
  readonly skill = signal('');
  readonly conflict = signal<TriggerConflict>('coalesce');
  readonly debounce = signal('120');
  readonly reason = signal('');

  readonly editing = computed(() => this.trigger() !== null);
  readonly events = computed(() => eventOptions(this.catalog()));
  readonly subjectSets = computed(() => subjectSetOptions(this.catalog()));
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

  useExample(cron: string): void {
    this.cron.set(cron);
    this.touch('cron');
  }

  save(): void {
    const editing = this.trigger();
    const triggerId = editing?.triggerId ?? this.triggerId().trim();
    const schedule = this.source() === 'schedule';
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
      if (this.subjectSet() === '') {
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
      subjectSet: schedule ? blank(this.subjectSet()) : null,
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
}
