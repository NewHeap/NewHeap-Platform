import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import {
  AgentSubjectType,
  DataHandOff,
  DataHandOffRequest,
  HandOffCeiling,
  NhAssistantAdminApiService,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe
} from '@newheap/platform-ai-chat';
import { RunConfigForm } from './run-config-form';
import { RunConfigErrors, blank, checkWhole, numberText, runConfigLimits, toNumber } from './run-config-shared';

let nextId = 0;

/** What a saved hand-off form tells its host. */
export interface HandOffFormSaved {
  handOff: DataHandOff;
  added: boolean;
}

/** An agent the ceiling lets hand off, as the form offers it. */
export interface HandOffSourceOption {
  agentId: string;
  name: string;
}

/**
 * Adds or changes a hand-off defined as data (S9): the kind of object, the agents that may hand work
 * on it to the agent, chosen from those the ceiling in code allows, a description for their models, a
 * follow-up to the source and a daily limit per source and object within the ceiling's. A saved
 * source that the ceiling no longer allows stays listed so it can be cleared. It is in effect once
 * saved.
 */
@Component({
  selector: 'nh-assistant-hand-off-form',
  standalone: true,
  imports: [TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-hand-off-form.component.html',
  styleUrls: ['../../nh-assistant-admin-shared.scss', './run-config-forms.scss']
})
export class NhAssistantHandOffFormComponent extends RunConfigForm implements OnInit {
  private readonly admin = inject(NhAssistantAdminApiService);

  readonly agentId = input.required<string>();
  readonly ceiling = input.required<HandOffCeiling>();
  /** The kinds of object the agent does not accept hand-offs about yet, in code or as data. */
  readonly freeTypes = input<readonly string[]>([]);
  readonly subjectTypes = input<readonly AgentSubjectType[]>([]);
  /** The names of the agents, by id. */
  readonly agentNames = input<ReadonlyMap<string, string>>(new Map());
  /** The hand-off to change; `null` adds one. */
  readonly handOff = input<DataHandOff | null>(null);
  readonly saved = output<HandOffFormSaved>();
  readonly cancelled = output<void>();

  readonly id = `nh-assistant-hand-off-form-${nextId++}`;
  readonly maxReason = runConfigLimits.reason;
  protected readonly fields = ['subjectType', 'sources', 'description', 'notifySource', 'maxPerSubjectPerDay', 'reason'];

  readonly subjectType = signal('');
  readonly sources = signal<readonly string[]>([]);
  readonly description = signal('');
  readonly notifySource = signal(false);
  readonly limit = signal('');
  readonly reason = signal('');

  readonly editing = computed(() => this.handOff() !== null);
  readonly chosenType = computed(() => this.handOff()?.subjectType ?? this.subjectType());
  readonly maxDescription = computed(() => this.ceiling().maxDescriptionLength);
  readonly maxLimit = computed(() => this.ceiling().maxPerSubjectPerDay);
  readonly typeOptions = computed(() => this.freeTypes().map(type => ({ id: type, name: this.typeName(type) })));
  /** What the ceiling allows about the chosen kind of object. */
  readonly allowance = computed(() => this.ceiling().subjectTypes.find(item => item.subjectType === this.chosenType()) ?? null);
  /**
   * The agents the ceiling lets hand off about the chosen kind of object, and the saved ones it no
   * longer allows, which can only be cleared.
   */
  readonly sourceOptions = computed<(HandOffSourceOption & { allowed: boolean })[]>(() => {
    const allowed = this.allowance()?.sources ?? [];
    const saved = this.handOff()?.sources ?? [];
    return [...allowed, ...saved.filter(source => !allowed.includes(source))]
      .map(agentId => ({ agentId, name: this.agentNames().get(agentId) ?? agentId, allowed: allowed.includes(agentId) }));
  });

  ngOnInit(): void {
    const handOff = this.handOff();
    this.subjectType.set(handOff?.subjectType ?? (this.freeTypes().length === 1 ? this.freeTypes()[0] : ''));
    this.sources.set([...(handOff?.sources ?? [])]);
    this.description.set(handOff?.description ?? '');
    this.notifySource.set(handOff?.notifySource ?? false);
    this.limit.set(numberText(handOff?.maxPerSubjectPerDay));
  }

  typeName(type: string): string {
    return this.subjectTypes().find(item => item.id === type)?.name ?? type;
  }

  setSubjectType(type: string): void {
    this.subjectType.set(type);
    // Sources belong to a kind of object: those the ceiling does not allow for the new one go.
    const allowed = this.allowance()?.sources ?? [];
    this.sources.set(this.sources().filter(source => allowed.includes(source)));
    this.touch('subjectType');
  }

  toggleSource(agentId: string, checked: boolean): void {
    const current = this.sources().filter(source => source !== agentId);
    this.sources.set(checked ? [...current, agentId] : current);
    this.touch('sources');
  }

  isChosen(agentId: string): boolean {
    return this.sources().includes(agentId);
  }

  save(): void {
    const errors: RunConfigErrors = {};
    const type = this.chosenType();
    if (!type) {
      errors['subjectType'] = { code: 'required' };
    }
    if (this.sources().length === 0) {
      errors['sources'] = { code: 'required' };
    } else if (this.sources().length > this.ceiling().maxSources) {
      errors['sources'] = { code: 'too-long', params: { max: this.ceiling().maxSources } };
    }
    if (this.description().trim().length > this.maxDescription()) {
      errors['description'] = { code: 'too-long', params: { max: this.maxDescription() } };
    }
    checkWhole(errors, 'maxPerSubjectPerDay', this.limit(), 1, this.maxLimit());
    if (this.reason().trim().length > this.maxReason) {
      errors['reason'] = { code: 'too-long', params: { max: this.maxReason } };
    }

    const current = this.handOff();
    const request: DataHandOffRequest = {
      sources: [...this.sources()],
      description: blank(this.description()),
      notifySource: this.notifySource(),
      maxPerSubjectPerDay: toNumber(this.limit()),
      expectedVersion: current?.version ?? null,
      reason: blank(this.reason())
    };
    this.submit(
      errors,
      () => this.admin.saveDataHandOff(this.agentId(), type, request),
      handOff => this.saved.emit({ handOff, added: current === null }));
  }
}
