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
import { AgentSubjectType } from '../models/assistant-admin.models';
import { NhAssistantAgentRunApiService } from '../services/nh-assistant-agent-run-api.service';
import { toNhAssistantError } from './nh-assistant-errors';
import { NhAssistantPickerComponent, NhAssistantPickerOption } from './nh-assistant-picker.component';

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
 * the name the application gives that subject, or why it was not found, below the field.
 */
@Component({
  selector: 'nh-assistant-subject-picker',
  standalone: true,
  imports: [TranslatePipe, NhAssistantPickerComponent],
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
  readonly search = computed(() => {
    const type = this.subjectType();
    return type.searchable ? nhAssistantSubjectSearch(this.api, type.id) : null;
  });
  readonly typedId = signal('');
  readonly lookingUp = signal(false);
  readonly found = signal<NhAssistantChosenSubject | null>(null);
  readonly lookupError = signal<string | null>(null);
  readonly fieldDescribedBy = computed(() => [this.hintId, this.describedBy()].filter(Boolean).join(' '));

  constructor() {
    inject(DestroyRef).onDestroy(() => this.lookup?.unsubscribe());
    // A new type starts a new choice.
    effect(() => {
      this.subjectType();
      untracked(() => {
        this.lookup?.unsubscribe();
        this.typedId.set(this.value() ?? '');
        this.found.set(null);
        this.lookupError.set(null);
        this.lookingUp.set(false);
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

  /** Looks the typed id up; the server names the subject when the caller may read it. */
  lookUp(): void {
    const id = this.typedId().trim();
    const type = this.subjectType().id;
    this.lookup?.unsubscribe();
    if (id.length === 0) {
      this.lookingUp.set(false);
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
        this.lookupError.set(toNhAssistantError(error).code);
        this.subjectChange.emit(null);
      }
    });
  }
}
