import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  input,
  output,
  signal,
  viewChild
} from '@angular/core';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import {
  AgentSubjectType,
  AgentTriggerReceipt,
  NH_ASSISTANT_SYSTEM_SUBJECT_TYPE,
  NhAssistantAgentRunApiService,
  NhAssistantError,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantSubjectLinkComponent as NhAssistantSubjectLinkComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';
import { NhAssistantRunReceiptComponent } from './nh-assistant-run-receipt.component';
import { NhAssistantChosenSubject, NhAssistantSubjectPickerComponent } from './nh-assistant-subject-picker.component';

/** The agent the dialog runs: its id, its name as people read it and what it works on. */
export interface NhAssistantRunNowAgent {
  id: string;
  /** The agent's name in the reader's language, or the text an administrator gave it. */
  name: string;
  /** The object types it works on; with `worksOnAllSubjects` every type the application registers. */
  worksOnSubjectTypes?: readonly string[] | null;
  worksOnAllSubjects?: boolean | null;
  /** It works on the whole application. */
  worksOnApplication?: boolean | null;
  /** Its work for the whole application exists per division. */
  perDivision?: boolean | null;
}

/** A subject the dialog runs the agent on without asking, such as the object of the page. */
export interface NhAssistantRunNowSubject {
  type: string;
  id: string;
  displayName?: string | null;
  typeName?: string | null;
  link?: string | null;
}

/** What a run by hand was started on, with the receipt of `POST threads/start`. */
export interface NhAssistantRunNowStarted {
  receipt: AgentTriggerReceipt;
  subject: NhAssistantChosenSubject;
}

/** One answer to "What should the agent work on?". */
interface RunNowTarget {
  key: string;
  type: AgentSubjectType;
  /** For the whole application: the agent works per division, so a division is chosen. */
  perDivision: boolean;
}

/** The longest instruction the dialog sends with a run. */
export const NH_ASSISTANT_RUN_NOW_MAX_MESSAGE = 4_000;

let nextId = 0;

/**
 * Runs an agent now, by hand (internal to the library; the administration's "Run now", the run now
 * of `nh-assistant-object-agents` and the board's tile use it, so it lives in the `agent-runs` entry
 * point and stays out of the chat's initial bundle). A modal dialog asks what the agent
 * works on this time, unless the host gives the subject: the whole application, in a division for an
 * agent that works per division, or an object of one of its types through the subject picker; and an
 * optional instruction. It sends `POST threads/start` with the caller as initiator, shows the
 * server's refusal at the form with its translated code, and ends with the receipt: what happens
 * now (a warning while the agent or its skill is paused, information while the start waits for the
 * application's change to be saved), the subject and the conversation to open. The server decides
 * who may run the agent: on an object the participant role, on the whole application the agent's
 * run policy.
 *
 * The host renders it when people ask to run the agent; it opens itself and emits `closed` when it
 * closes, after which the host removes it. Escape closes it, and focus returns to what opened it.
 */
@Component({
  selector: 'nh-assistant-run-now-dialog',
  standalone: true,
  imports: [
    TranslatePipe,
    NhAssistantTranslatePipe,
    NhAssistantIconComponent,
    NhAssistantSubjectLinkComponent,
    NhAssistantSubjectPickerComponent,
    NhAssistantRunReceiptComponent
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-run-now-dialog.component.html',
  styleUrl: './nh-assistant-run-now-dialog.component.scss'
})
export class NhAssistantRunNowDialogComponent {
  private readonly api = inject(NhAssistantAgentRunApiService);
  private readonly translate = inject(TranslateService);
  private readonly injector = inject(Injector);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');
  private subscription?: Subscription;

  readonly agent = input.required<NhAssistantRunNowAgent>();
  /** The application's subject types from the run catalog, for choosing an object type. */
  readonly subjectTypes = input<readonly AgentSubjectType[]>([]);
  /** Whether the application lists its divisions, so a per-division agent can be run in one. */
  readonly divisions = input(false);
  /** Runs the agent on this subject without asking, such as the object of the page. */
  readonly subject = input<NhAssistantRunNowSubject | null>(null);

  /** The run started, with its receipt. */
  readonly started = output<NhAssistantRunNowStarted>();
  /** People want to open the agent's conversation; the dialog has closed. */
  readonly openConversation = output<string>();
  /** The dialog closed; the host removes it. */
  readonly closed = output<void>();

  readonly id = `nh-assistant-run-now-${nextId++}`;
  readonly maxMessage = NH_ASSISTANT_RUN_NOW_MAX_MESSAGE;
  readonly targetKey = signal<string | null>(null);
  readonly chosen = signal<NhAssistantChosenSubject | null>(null);
  readonly message = signal('');
  readonly starting = signal(false);
  readonly subjectMissing = signal(false);
  readonly receipt = signal<NhAssistantRunNowStarted | null>(null);
  readonly error = signal<NhAssistantError | null>(null);
  /** A refusal of the start explained for running an agent, then as the server names it. */
  readonly errorKeys = computed(() => {
    const error = this.error();
    return error ? [`nh-assistant.run-now.errors.${error.code}`, ...nhAssistantErrorKeys(error)] : [];
  });

  /** The whole application as the run catalog names it, or in the reader's language without one. */
  private readonly systemType = computed<AgentSubjectType>(() =>
    this.subjectTypes().find(type => type.isSystem && type.id === NH_ASSISTANT_SYSTEM_SUBJECT_TYPE) ?? {
      id: NH_ASSISTANT_SYSTEM_SUBJECT_TYPE,
      name: this.translate.instant('nh-assistant.run-now.whole-application'),
      pluralName: null,
      entityType: null,
      entityTypeFull: null,
      link: null,
      searchable: this.divisions(),
      isSystem: true
    });

  /** What the agent can be run on: the whole application when it works on it, then its object types. */
  readonly targets = computed<RunNowTarget[]>(() => {
    const agent = this.agent();
    const targets: RunNowTarget[] = [];
    if (agent.worksOnApplication) {
      targets.push({ key: NH_ASSISTANT_SYSTEM_SUBJECT_TYPE, type: this.systemType(), perDivision: agent.perDivision === true });
    }
    const named = new Set(agent.worksOnSubjectTypes ?? []);
    for (const type of this.subjectTypes()) {
      if (!type.isSystem && (agent.worksOnAllSubjects === true || named.has(type.id))) {
        targets.push({ key: type.id, type, perDivision: false });
      }
    }
    return targets;
  });

  /** The chosen answer, by default the first: the whole application when the agent works on it. */
  readonly target = computed(() => {
    const targets = this.targets();
    return targets.find(item => item.key === this.targetKey()) ?? targets[0] ?? null;
  });
  /** A per-division agent cannot be run while the application lists no divisions. */
  readonly divisionUnavailable = computed(() => this.target()?.perDivision === true && !this.divisions());
  readonly subjectFieldId = computed(() => `${this.id}-subject`);
  readonly subjectErrorId = computed(() => `${this.id}-subject-error`);

  constructor() {
    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());
    afterNextRender(() => {
      const dialog = this.dialog().nativeElement;
      if (!dialog.open) {
        dialog.showModal();
      }
      this.focusFirst();
    }, { injector: this.injector });
  }

  chooseTarget(key: string): void {
    if (this.target()?.key === key) {
      return;
    }
    this.targetKey.set(key);
    this.chosen.set(null);
    this.subjectMissing.set(false);
    this.error.set(null);
  }

  onSubject(subject: NhAssistantChosenSubject | null): void {
    this.chosen.set(subject);
    if (subject) {
      this.subjectMissing.set(false);
    }
  }

  onMessage(event: Event): void {
    this.message.set((event.target as HTMLTextAreaElement).value);
  }

  /** The subject the run is for: the host's, or the one chosen in the form. */
  subjectOfRun(): NhAssistantChosenSubject | null {
    const fixed = this.subject();
    return fixed ? { type: fixed.type, id: fixed.id, displayName: fixed.displayName ?? null } : this.chosen();
  }

  submit(): void {
    if (this.starting()) {
      return;
    }
    const subject = this.subjectOfRun();
    if (!subject) {
      this.subjectMissing.set(true);
      this.focusSubject();
      return;
    }

    this.starting.set(true);
    this.error.set(null);
    this.subscription?.unsubscribe();
    this.subscription = this.api.startAgent({
      agentId: this.agent().id,
      subjectType: subject.type,
      subjectId: subject.id,
      message: this.message().trim() || null
    }).subscribe({
      next: receipt => {
        const started = { receipt, subject };
        this.starting.set(false);
        this.receipt.set(started);
        this.started.emit(started);
        afterNextRender(() => this.host.nativeElement.querySelector<HTMLElement>('[data-receipt-focus]')?.focus(), { injector: this.injector });
      },
      error: (failure: unknown) => {
        this.starting.set(false);
        this.error.set(toNhAssistantError(failure));
      }
    });
  }

  open(conversationId: string): void {
    this.close();
    this.openConversation.emit(conversationId);
  }

  close(): void {
    const dialog = this.dialog().nativeElement;
    if (dialog.open) {
      dialog.close();
    } else {
      this.onClosed();
    }
  }

  /** The native dialog closed, by a button or by Escape. */
  onClosed(): void {
    this.subscription?.unsubscribe();
    this.closed.emit();
  }

  /** The name of the subject in the receipt, when the server's type name adds something. */
  receiptTypeName(started: NhAssistantRunNowStarted): string | null {
    const fixed = this.subject();
    if (fixed && fixed.type === started.subject.type && fixed.id === started.subject.id) {
      return fixed.typeName ?? null;
    }
    return this.subjectTypes().find(type => type.id === started.subject.type)?.name ?? null;
  }

  receiptLink(started: NhAssistantRunNowStarted): string | null {
    const fixed = this.subject();
    return fixed && fixed.type === started.subject.type && fixed.id === started.subject.id ? fixed.link ?? null : null;
  }

  private focusFirst(): void {
    const element = this.host.nativeElement;
    const first = element.querySelector<HTMLElement>('[data-target-choice]:checked')
      ?? element.querySelector<HTMLElement>('textarea');
    first?.focus();
  }

  private focusSubject(): void {
    afterNextRender(() => this.host.nativeElement.querySelector<HTMLElement>(`#${this.subjectFieldId()}`)?.focus(), { injector: this.injector });
  }
}
