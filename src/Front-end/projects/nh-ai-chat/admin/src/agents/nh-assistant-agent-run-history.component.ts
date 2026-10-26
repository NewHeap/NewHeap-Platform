import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked
} from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import {
  AgentRunFilter,
  AgentRunHistoryItem,
  AgentRunStatus,
  AgentSubjectType,
  NH_ASSISTANT_SYSTEM_SUBJECT_TYPE,
  NhAssistantAdminApiService,
  NhAssistantError,
  ɵnhAssistantCodeKeys as nhAssistantCodeKeys,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantSubjectLinkComponent as NhAssistantSubjectLinkComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';
import { NhAssistantRunResultComponent } from '@newheap/platform-ai-chat/agent-runs';
import { startNhAssistantAdminTranslations } from '../i18n/nh-assistant-admin-translations';

let nextId = 0;

/** The runs one page of the runs view loads. */
const pageSize = 20;

/** The statuses a run can have, in the order the filter offers them. */
const statuses: readonly AgentRunStatus[] = ['completed', 'failed', 'cancelled', 'waiting-for-approval', 'running', 'queued'];

/** A filter value as the form holds it: the empty string means "any". */
interface RunHistoryForm {
  subjectType: string;
  subjectId: string;
  status: AgentRunStatus | '';
  from: string;
  to: string;
}

const emptyForm: RunHistoryForm = { subjectType: '', subjectId: '', status: '', from: '', to: '' };

/** The filter fields the form shows; the server's errors of others stay with the form's error. */
const formFields = ['subjectType', 'subjectId', 'status', 'to'] as const;

/**
 * The runs view of one agent for administrators (`GET admin/agents/{id}/runs`): every run of the
 * agent, shadow runs marked, newest first, filtered by the kind of object, one object, the whole
 * application, the status and the period. Per run its time, trigger, object, the beginning of its
 * result, its proposals, usage and duration; "Show the result" opens the run's whole result, its
 * proposals and its files in `nh-assistant-run-result`. The administration's agent cards open it with
 * "Runs". A filter the server refuses (`assistant-run-filter-invalid`) shows each refused part at its
 * field, and a run's failure reads as its translated code.
 */
@Component({
  selector: 'nh-assistant-agent-run-history',
  standalone: true,
  imports: [
    DatePipe,
    TranslatePipe,
    NhAssistantTranslatePipe,
    NhAssistantIconComponent,
    NhAssistantSubjectLinkComponent,
    NhAssistantRunResultComponent
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-agent-run-history.component.html',
  styleUrls: ['../nh-assistant-admin-shared.scss', './nh-assistant-agent-run-history.component.scss']
})
export class NhAssistantAgentRunHistoryComponent {
  private readonly admin = inject(NhAssistantAdminApiService);
  private readonly injector = inject(Injector);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private subscription?: Subscription;

  /** The agent whose runs are shown. */
  readonly agentId = input.required<string>();
  /** The kinds of objects of the application, from the run catalog, to filter by. */
  readonly subjectTypes = input<readonly AgentSubjectType[]>([]);
  /** Emits the conversation of a run, so the host can open it in the assistant panel. */
  readonly openConversation = output<string>();

  readonly id = `nh-assistant-agent-run-history-${nextId++}`;
  readonly statuses = statuses;
  readonly systemType = NH_ASSISTANT_SYSTEM_SUBJECT_TYPE;
  /** The kinds of objects, the whole application left out: it has an option of its own. */
  readonly objectTypes = computed(() => this.subjectTypes().filter(type => !type.isSystem));
  readonly form = signal<RunHistoryForm>(emptyForm);
  /** The period ends before it starts. */
  readonly periodInvalid = signal(false);
  /** One object can be chosen once a kind of object is. */
  readonly objectChoosable = computed(() => {
    const type = this.form().subjectType;
    return type !== '' && type !== NH_ASSISTANT_SYSTEM_SUBJECT_TYPE;
  });
  readonly loading = signal(true);
  readonly loadingMore = signal(false);
  readonly error = signal<NhAssistantError | null>(null);
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));
  /** The refused filter fields the form shows, with the server's first value per field. */
  readonly fieldErrors = computed<Partial<Record<string, string>>>(() => {
    const errors: Partial<Record<string, string>> = {};
    for (const field of formFields) {
      const value = this.error()?.fieldErrors?.[field]?.[0];
      if (value) {
        errors[field] = value;
      }
    }
    return errors;
  });
  readonly codeKeys = nhAssistantCodeKeys;
  readonly items = signal<AgentRunHistoryItem[]>([]);
  readonly total = signal(0);
  readonly page = signal(1);
  readonly hasMore = computed(() => this.items().length < this.total());
  /** The run whose whole result is shown instead of the list. */
  readonly opened = signal<AgentRunHistoryItem | null>(null);
  private applied: AgentRunFilter = {};

  constructor() {
    startNhAssistantAdminTranslations();

    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());
    effect(() => {
      this.agentId();
      untracked(() => {
        this.opened.set(null);
        this.form.set(emptyForm);
        this.applied = {};
        this.fetch(1);
      });
    });
  }

  /** The text of the server's error of a filter field; a refused end of the period reads as such. */
  fieldErrorKeys(field: string): string[] {
    const value = this.fieldErrors()[field];
    if (!value) {
      return [];
    }
    const own = field === 'to' ? ['nh-assistant.admin.runs.period-invalid'] : [];
    return [...own, `nh-assistant.admin.run-config.field-errors.${value}`, 'nh-assistant.errors.generic'];
  }

  /** The ids a filter field describes itself with: its hint and its error while there is one. */
  describedBy(hintId: string | null, field: string): string | null {
    const ids = [hintId, this.fieldErrors()[field] ? `${this.id}-${field}-error` : null].filter((id): id is string => id !== null);
    return ids.length > 0 ? ids.join(' ') : null;
  }

  update(field: keyof RunHistoryForm, event: Event): void {
    const value = (event.target as HTMLInputElement | HTMLSelectElement).value;
    this.form.update(form => field === 'subjectType'
      // Another kind of object starts a new choice of object.
      ? { ...form, subjectType: value, subjectId: '' }
      : { ...form, [field]: value });
    if (field === 'from' || field === 'to') {
      this.periodInvalid.set(false);
    }
  }

  /** Reads the runs that match the form, from the first page. */
  apply(): void {
    const form = this.form();
    const from = form.from ? startOfDay(form.from) : undefined;
    // The last day counts in full: the period ends where the next day starts.
    const to = form.to ? startOfDay(form.to, 1) : undefined;
    if (from && to && Date.parse(to) <= Date.parse(from)) {
      this.periodInvalid.set(true);
      afterNextRender(() => this.host.nativeElement.querySelector<HTMLInputElement>(`#${this.id}-to`)?.focus(), { injector: this.injector });
      return;
    }

    this.periodInvalid.set(false);
    this.applied = {
      subjectType: form.subjectType || undefined,
      subjectId: this.objectChoosable() ? form.subjectId.trim() || undefined : undefined,
      status: form.status || undefined,
      from,
      to
    };
    this.fetch(1);
  }

  /** Clears the filters and reads every run again. */
  clear(): void {
    this.form.set(emptyForm);
    this.periodInvalid.set(false);
    this.applied = {};
    this.fetch(1);
  }

  /** Narrows the view to the runs on the object of one run. */
  onlyObject(run: AgentRunHistoryItem): void {
    this.form.set({
      ...emptyForm,
      subjectType: run.subjectType,
      subjectId: run.subjectType === NH_ASSISTANT_SYSTEM_SUBJECT_TYPE ? '' : run.subjectId
    });
    this.apply();
  }

  loadMore(): void {
    this.fetch(this.page() + 1);
  }

  open(run: AgentRunHistoryItem): void {
    this.opened.set(run);
    afterNextRender(() => this.host.nativeElement.querySelector<HTMLButtonElement>('[data-runs-back]')?.focus(), { injector: this.injector });
  }

  /** Returns to the list and to the result action of the run that was open. */
  close(): void {
    const runId = this.opened()?.id;
    this.opened.set(null);
    afterNextRender(() => {
      const action = Array.from(this.host.nativeElement.querySelectorAll<HTMLButtonElement>('[data-open-run]'))
        .find(item => item.dataset['openRun'] === runId);
      action?.focus();
    }, { injector: this.injector });
  }

  /** How long the run took, as a text key and its number; `null` while it runs. */
  durationOf(run: AgentRunHistoryItem): { key: string; value: number } | null {
    if (!run.startedAt || !run.endedAt) {
      return null;
    }

    const seconds = Math.max(0, Math.round((Date.parse(run.endedAt) - Date.parse(run.startedAt)) / 1000));
    return seconds < 120
      ? { key: 'nh-assistant.agent-runs.result.duration-seconds', value: seconds }
      : { key: 'nh-assistant.agent-runs.result.duration-minutes', value: Math.round(seconds / 60) };
  }

  /** Moves the focus to the first filter field the server refused. */
  private focusRefusedField(): void {
    if (Object.keys(this.fieldErrors()).length === 0) {
      return;
    }
    afterNextRender(() => this.host.nativeElement.querySelector<HTMLElement>('.filters [aria-invalid="true"]')?.focus(), { injector: this.injector });
  }

  private fetch(page: number): void {
    const first = page === 1;
    this.loading.set(first);
    this.loadingMore.set(!first);
    this.error.set(null);
    this.subscription?.unsubscribe();
    this.subscription = this.admin.getAgentRuns(this.agentId(), { ...this.applied, page, itemsPerPage: pageSize }).subscribe({
      next: history => {
        this.items.update(items => first ? history.items : [...items, ...history.items]);
        this.total.set(history.total);
        this.page.set(page);
        this.loading.set(false);
        this.loadingMore.set(false);
      },
      error: (failure: unknown) => {
        if (first) {
          this.items.set([]);
          this.total.set(0);
        }
        this.loading.set(false);
        this.loadingMore.set(false);
        this.error.set(toNhAssistantError(failure));
        this.focusRefusedField();
      }
    });
  }
}

/** The start of a day of the person's calendar, plus some days, as an ISO 8601 time. */
function startOfDay(date: string, plusDays = 0): string {
  const [year, month, day] = date.split('-').map(Number);
  return new Date(year, month - 1, day + plusDays).toISOString();
}
