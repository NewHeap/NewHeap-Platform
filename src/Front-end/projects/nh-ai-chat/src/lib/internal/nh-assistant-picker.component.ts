import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  computed,
  inject,
  input,
  output,
  signal,
  viewChild
} from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Observable, Subscription } from 'rxjs';
import { NhAssistantIconComponent } from './nh-assistant-icon.component';

/** One choice of a picker: the stored value, its name and what it means. */
export interface NhAssistantPickerOption {
  value: string;
  label: string;
  description?: string | null;
}

let nextId = 0;
const searchDelayMs = 250;

/**
 * A searchable choice for the administration (internal): a combobox with a listbox popup in the ARIA
 * 1.2 pattern. Typing filters the options on name and description, the arrow keys move, Enter
 * chooses and Escape closes. With `search` it asks the server instead, after `minQueryLength`
 * characters. `noneLabel` adds an explicit empty choice, emitted as `''`.
 */
@Component({
  selector: 'nh-assistant-picker',
  standalone: true,
  imports: [TranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-picker.component.html',
  styleUrl: './nh-assistant-picker.component.scss'
})
export class NhAssistantPickerComponent {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private searchTimer?: ReturnType<typeof setTimeout>;
  private searchSubscription?: Subscription;
  private readonly field = viewChild.required<ElementRef<HTMLInputElement>>('field');

  /** The id of the text field, for the label's `for`. */
  readonly inputId = input.required<string>();
  readonly options = input<readonly NhAssistantPickerOption[]>([]);
  readonly value = input<string | null>(null);
  /** The name to show for a value that is not among the options, such as a found account. */
  readonly selectedLabel = input<string | null>(null);
  /** The label of the empty choice; without it there is none. */
  readonly noneLabel = input<string | null>(null);
  readonly placeholder = input<string | null>(null);
  /** Searches the server; the options are ignored while it is set. */
  readonly search = input<((query: string) => Observable<NhAssistantPickerOption[]>) | null>(null);
  readonly minQueryLength = input(2);
  readonly invalid = input(false);
  readonly describedBy = input<string | null>(null);
  readonly disabled = input(false);

  readonly valueChange = output<string>();
  /** The chosen option, also for a search result the options do not contain. */
  readonly optionChange = output<NhAssistantPickerOption | null>();

  readonly listId = `nh-assistant-picker-${nextId++}`;
  readonly open = signal(false);
  /** The text typed since the list opened; `null` shows the chosen option's name. */
  readonly query = signal<string | null>(null);
  readonly active = signal(-1);
  readonly searching = signal(false);
  readonly results = signal<NhAssistantPickerOption[]>([]);
  private readonly chosen = signal<NhAssistantPickerOption | null>(null);

  readonly selected = computed<NhAssistantPickerOption | null>(() => {
    const value = this.value();
    if (value === null || value === '') {
      return null;
    }
    const known = this.options().find(option => option.value === value)
      ?? this.results().find(option => option.value === value)
      ?? (this.chosen()?.value === value ? this.chosen() : null);
    return known ?? { value, label: this.selectedLabel() ?? value };
  });

  readonly inputText = computed(() => this.query() ?? this.selected()?.label ?? (this.value() === '' ? this.noneLabel() ?? '' : ''));

  readonly visible = computed<NhAssistantPickerOption[]>(() => {
    if (this.search()) {
      return this.results();
    }
    const none = this.noneLabel();
    const all = none === null ? [...this.options()] : [{ value: '', label: none }, ...this.options()];
    const text = this.query()?.trim().toLocaleLowerCase() ?? '';
    return text.length === 0
      ? all
      : all.filter(option =>
        option.label.toLocaleLowerCase().includes(text)
        || option.value.toLocaleLowerCase().includes(text)
        || (option.description ?? '').toLocaleLowerCase().includes(text));
  });

  /** Why the list is empty: still typing, searching, or nothing found. */
  readonly emptyKey = computed(() => {
    if (this.searching()) {
      return 'nh-assistant.picker.searching';
    }
    if (this.search() && (this.query()?.trim().length ?? 0) < this.minQueryLength()) {
      return 'nh-assistant.picker.type-more';
    }
    return 'nh-assistant.picker.no-results';
  });

  readonly activeId = computed(() => this.open() && this.active() >= 0 && this.active() < this.visible().length
    ? this.optionId(this.active())
    : null);

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      clearTimeout(this.searchTimer);
      this.searchSubscription?.unsubscribe();
    });
  }

  optionId(index: number): string {
    return `${this.listId}-option-${index}`;
  }

  /** Moves the focus to the text field, for example after a refused save. */
  focus(): void {
    this.field().nativeElement.focus();
  }

  toggle(): void {
    if (this.open()) {
      this.close();
    } else {
      this.openList();
      this.focus();
    }
  }

  openList(): void {
    if (this.disabled() || this.open()) {
      return;
    }
    this.open.set(true);
    const index = this.visible().findIndex(option => option.value === (this.value() ?? ''));
    this.active.set(index);
  }

  onInput(event: Event): void {
    const text = (event.target as HTMLInputElement).value;
    this.query.set(text);
    this.open.set(true);
    this.active.set(this.search() ? -1 : 0);
    if (this.search()) {
      this.startSearch(text);
    }
  }

  onKeydown(event: KeyboardEvent): void {
    const count = this.visible().length;
    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        if (!this.open()) {
          this.openList();
        }
        this.active.set(Math.min(this.active() + 1, count - 1));
        this.scrollActiveIntoView();
        break;
      case 'ArrowUp':
        event.preventDefault();
        this.active.set(Math.max(this.active() - 1, 0));
        this.scrollActiveIntoView();
        break;
      case 'Enter':
        if (this.open()) {
          event.preventDefault();
          const option = this.visible()[this.active()];
          if (option) {
            this.choose(option);
          } else {
            this.close();
          }
        }
        break;
      case 'Escape':
        if (this.open()) {
          event.preventDefault();
          event.stopPropagation();
          this.close();
        }
        break;
      case 'Tab':
        this.close();
        break;
    }
  }

  onBlur(): void {
    this.close();
  }

  choose(option: NhAssistantPickerOption): void {
    this.chosen.set(option.value === '' ? null : option);
    this.valueChange.emit(option.value);
    this.optionChange.emit(option.value === '' ? null : option);
    this.close();
  }

  private close(): void {
    this.open.set(false);
    this.query.set(null);
    this.active.set(-1);
  }

  private startSearch(text: string): void {
    clearTimeout(this.searchTimer);
    this.searchSubscription?.unsubscribe();
    const search = this.search();
    const query = text.trim();
    if (!search || query.length < this.minQueryLength()) {
      this.results.set([]);
      this.searching.set(false);
      return;
    }
    this.searching.set(true);
    this.searchTimer = setTimeout(() => {
      this.searchSubscription = search(query).subscribe({
        next: results => {
          this.results.set(results);
          this.searching.set(false);
          this.active.set(results.length > 0 ? 0 : -1);
        },
        error: () => {
          this.results.set([]);
          this.searching.set(false);
        }
      });
    }, searchDelayMs);
  }

  private scrollActiveIntoView(): void {
    const id = this.activeId();
    if (id) {
      queueMicrotask(() => this.host.nativeElement.querySelector(`#${id}`)?.scrollIntoView?.({ block: 'nearest' }));
    }
  }
}
