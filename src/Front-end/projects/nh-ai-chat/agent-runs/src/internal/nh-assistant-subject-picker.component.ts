import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked
} from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Observable, Subscription, map } from 'rxjs';
import {
  AgentSubjectType,
  NH_ASSISTANT_SYSTEM_GLOBAL_ID,
  NhAssistantAgentRunApiService,
  NhAssistantError,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantPickerComponent as NhAssistantPickerComponent,
  type ɵNhAssistantPickerOption as NhAssistantPickerOption,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';

/** Lookups that cannot succeed by asking again: the person changes the id instead. */
const finalLookupCodes = new Set(['assistant-subject-not-found', 'assistant-subject-invalid', 'assistant-subject-type-unknown']);

/** A subject chosen in the subject picker, with the name the application gives it. */
export interface NhAssistantChosenSubject {
  type: string;
  id: string;
  displayName: string | null;
}

/**
 * The search of the picker for the subjects of one type: the subjects the caller may read whose
 * name matches, from `GET subjects/{type}/search` (internal).
 */
export function nhAssistantSubjectSearch(
  api: NhAssistantAgentRunApiService,
  subjectType: string,
  limit = 20
): (query: string) => Observable<NhAssistantPickerOption[]> {
  return query => api.searchSubjects(subjectType, query, limit).pipe(
    map(result => result.items.map(item => ({ value: item.id, label: item.displayName ?? item.id })))
  );
}

let nextId = 0;

/**
 * Chooses one subject of a type (internal). A searchable type is a combobox that searches the
 * caller's readable subjects by name; for any other type people give the id, and the picker shows
 * the name the application gives that subject, or why it was not found, below the field, with
 * the translated code and, when asking again can help, a button that looks it up again. The
 * platform's type of the whole application (`isSystem`) is "The whole application" itself for an
 * app-wide agent, chosen as soon as the type is, and a search of the divisions by name for an agent
 * that works per division (`perDivision`).
 */
@Component({
  selector: 'nh-assistant-subject-picker',
  standalone: true,
  imports: [TranslatePipe, NhAssistantTranslatePipe, NhAssistantPickerComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-subject-picker.component.html',
  styleUrl: './nh-assistant-subject-picker.component.scss'
})
export class NhAssistantSubjectPickerComponent {
  private readonly api = inject(NhAssistantAgentRunApiService);
  private lookup?: Subscription;

  /** The id of the field, for the label's `for`. */
  readonly inputId = input.required<string>();
  readonly subjectType = input.required<AgentSubjectType>();
  /**
   * For the whole application: choose one of the application's divisions, for an agent that works
   * per division, instead of the whole application app-wide.
   */
  readonly perDivision = input(false);
  /** The chosen subject's id. */
  readonly value = input<string | null>(null);
  /** The name of the chosen subject, when the host knows it. */
  readonly selectedLabel = input<string | null>(null);
  readonly invalid = input(false);
  readonly describedBy = input<string | null>(null);
  readonly disabled = input(false);

  /** The chosen subject, or `null` when the choice was cleared or the id was not found. */
  readonly subjectChange = output<NhAssistantChosenSubject | null>();

  readonly hintId = `nh-assistant-subject-picker-hint-${nextId++}`;
  /** The whole application app-wide: nothing to choose beyond the type itself. */
  readonly wholeApplication = computed(() => this.subjectType().isSystem && !this.perDivision());
  readonly search = computed(() => {
    const type = this.subjectType();
    if (type.isSystem) {
      // The whole application is searched for its divisions only.
      return this.perDivision() ? nhAssistantSubjectSearch(this.api, type.id) : null;
    }

    return type.searchable ? nhAssistantSubjectSearch(this.api, type.id) : null;
  });
  readonly placeholderKey = computed(() => this.subjectType().isSystem
    ? 'nh-assistant.subject-picker.division-placeholder'
    : 'nh-assistant.subject-picker.search-placeholder');
  readonly typedId = signal('');
  readonly lookingUp = signal(false);
  readonly found = signal<NhAssistantChosenSubject | null>(null);
  /** Why the typed id could not be looked up; `null` while there is no failure. */
  readonly lookupError = signal<NhAssistantError | null>(null);
  /** An unknown subject reads as such; any other failure with its own text and the generic fallback. */
  readonly lookupErrorKeys = computed(() => {
    const error = this.lookupError();
    return error?.code === 'assistant-subject-not-found'
      ? ['nh-assistant.subject-picker.not-found', ...nhAssistantErrorKeys(error)]
      : nhAssistantErrorKeys(error);
  });
  /** Looking the same id up again can help after a failure of the connection or the server. */
  readonly lookupRetryable = computed(() => {
    const error = this.lookupError();
    return error !== null && !finalLookupCodes.has(error.code);
  });
  readonly fieldDescribedBy = computed(() => [this.hintId, this.describedBy()].filter(Boolean).join(' '));

  constructor() {
    inject(DestroyRef).onDestroy(() => this.lookup?.unsubscribe());
    // A new type starts a new choice; the whole application app-wide is chosen with its type.
    effect(() => {
      const type = this.subjectType();
      const whole = this.wholeApplication();
      untracked(() => {
        this.lookup?.unsubscribe();
        this.typedId.set(this.value() ?? '');
        this.found.set(null);
        this.lookupError.set(null);
        this.lookingUp.set(false);
        if (whole) {
          this.subjectChange.emit({ type: type.id, id: NH_ASSISTANT_SYSTEM_GLOBAL_ID, displayName: type.name });
        }
      });
    });
  }

  choose(option: NhAssistantPickerOption | null): void {
    this.subjectChange.emit(option ? { type: this.subjectType().id, id: option.value, displayName: option.label } : null);
  }

  onIdInput(event: Event): void {
    this.typedId.set((event.target as HTMLInputElement).value);
    this.found.set(null);
    this.lookupError.set(null);
  }

  /** Looks the typed id up again after a failure that asking again may resolve. */
  lookUpAgain(): void {
    this.found.set(null);
    this.lookUp();
  }

  /** Looks the typed id up; the server names the subject when the caller may read it. */
  lookUp(): void {
    const id = this.typedId().trim();
    const type = this.subjectType().id;
    this.lookup?.unsubscribe();
    if (id.length === 0) {
      this.lookingUp.set(false);
      this.lookupError.set(null);
      this.subjectChange.emit(null);
      return;
    }
    if (this.found()?.id === id) {
      return;
    }

    this.lookingUp.set(true);
    this.lookupError.set(null);
    this.lookup = this.api.getSubjectWork(type, id).subscribe({
      next: work => {
        const subject = { type, id, displayName: work.subjectDisplayName };
        this.lookingUp.set(false);
        this.found.set(subject);
        this.subjectChange.emit(subject);
      },
      error: (error: unknown) => {
        this.lookingUp.set(false);
        this.lookupError.set(toNhAssistantError(error));
        this.subjectChange.emit(null);
      }
    });
  }
}
