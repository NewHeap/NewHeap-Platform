import {
  DestroyRef,
  Directive,
  ElementRef,
  Injector,
  afterNextRender,
  booleanAttribute,
  computed,
  effect,
  inject,
  input,
  isSignal,
  output,
  signal,
  untracked,
  viewChild
} from '@angular/core';
import { Observable, Subscription, from, isObservable, take, throwError } from 'rxjs';
import {
  NH_SELECT_CONFIG,
  NH_SELECT_DEFAULT_TEXTS,
  NhSelectCompare,
  NhSelectConfig,
  NhSelectCreate,
  NhSelectErrorText,
  NhSelectOperation,
  NhSelectOption,
  NhSelectSearch,
  NhSelectSize,
  NhSelectTexts,
  nhSelectSameValue
} from './nh-select.models';

let nextId = 0;

/** The room the list needs below the field before it opens above it instead. */
const popupRoom = 240;

/** A row of the list: an option, the empty choice of a combobox, or the row that creates an option. */
export interface NhSelectRow<T> {
  readonly kind: 'option' | 'none' | 'create';
  readonly option: NhSelectOption<T> | null;
  readonly label: string;
  readonly description: string | null;
  readonly icon: string | null;
  readonly image: string | null;
  readonly classes: string | readonly string[] | null;
  readonly selected: boolean;
  readonly disabled: boolean;
}

/** A failed search or create and the error it failed with. */
interface NhSelectFailure {
  readonly operation: NhSelectOperation;
  readonly error: unknown;
}

/** The answer of a server search and the text it answered. */
interface NhSelectResults<T> {
  readonly query: string;
  readonly options: readonly NhSelectOption<T>[];
}

/** Whether an option's label or description contains the typed text (lower case). */
function matches(option: NhSelectOption<unknown>, text: string): boolean {
  return option.label.toLocaleLowerCase().includes(text)
    || (option.description ?? '').toLocaleLowerCase().includes(text);
}

/**
 * What `nh-combobox` and `nh-multiselect` share (internal): a combobox in the ARIA 1.2 pattern with a
 * listbox popup. Typing filters the options on label and description or asks `search`, the arrow
 * keys, Home and End move, Enter chooses, Escape closes and Tab leaves. A failed search or create says
 * why below the field and offers to try again; `create` adds a row for text that matches no option.
 */
@Directive()
export abstract class NhSelectBase<T> {
  protected readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);
  private readonly configSource = inject(NH_SELECT_CONFIG, { optional: true });
  protected readonly field = viewChild.required<ElementRef<HTMLInputElement>>('field');

  /** The id of the text field, for a `<label for>`; a generated id when not set. */
  readonly inputId = input<string | null>(null);
  /** The options to choose from; ignored while `search` is set. */
  readonly options = input<readonly NhSelectOption<T>[]>([]);
  /**
   * Options for values the list may not contain, such as stored values of a server search, so their
   * labels show before the person searches.
   */
  readonly selectedOptions = input<readonly NhSelectOption<T>[]>([]);
  readonly placeholder = input<string | null>(null);
  /** Searches a server instead of filtering `options`, after `minQueryLength` characters. */
  readonly search = input<NhSelectSearch<T> | null>(null);
  /** Characters a server search waits for; `0` searches as soon as the list opens. Default `2`. */
  readonly minQueryLength = input<number | null>(null);
  /** Milliseconds a server search waits after the last key. Default `250`. */
  readonly searchDebounceMs = input<number | null>(null);
  /** Offers a "Create" row for typed text that matches no option and chooses the option it answers. */
  readonly create = input<NhSelectCreate<T> | null>(null);
  /** Says whether two values are the same choice; by default the same primitive or reference. */
  readonly compareWith = input<NhSelectCompare<T>>(nhSelectSameValue);
  readonly disabled = input(false, { transform: booleanAttribute });
  /** Marks the field invalid for assistive technology (`aria-invalid`) and shows the error border. */
  readonly invalid = input(false, { transform: booleanAttribute });
  /** Ids of elements that describe the field, such as a hint or an error message. */
  readonly describedBy = input<string | null>(null);
  /** The accessible name when no `<label for>` names the field. */
  readonly ariaLabel = input<string | null>(null);
  /** Ids of elements that name the field when no `<label for>` does. */
  readonly ariaLabelledBy = input<string | null>(null);
  readonly size = input<NhSelectSize | null>(null);
  /** Texts that replace those of `provideNhSelect` and the English defaults. */
  readonly texts = input<Partial<NhSelectTexts> | null>(null);
  /** Describes a failed search or create; replaces the `errorText` of `provideNhSelect`. */
  readonly errorText = input<NhSelectErrorText | null>(null);

  /** Emits `true` when the list opens and `false` when it closes, after a choice. */
  readonly openChange = output<boolean>();
  /** Emits the option `create` answered, before it is chosen. */
  readonly optionCreated = output<NhSelectOption<T>>();

  /** Whether the person may choose several options. */
  abstract readonly multiple: boolean;

  protected readonly uid = `nh-select-${nextId++}`;
  readonly listId = `${this.uid}-listbox`;
  readonly errorId = `${this.uid}-error`;
  readonly fieldId = computed(() => this.inputId() || `${this.uid}-field`);

  readonly open = signal(false);
  /** The text typed since the list opened; `null` while the person is not typing. */
  readonly query = signal<string | null>(null);
  /** The index of the row the arrow keys point at, or `-1`. */
  readonly active = signal(-1);
  readonly searching = signal(false);
  readonly creating = signal(false);
  /** The list opens above the field when there is not enough room below it. */
  readonly above = signal(false);
  /** The accessible name of the list, from the field's label. */
  readonly listLabel = signal<string | null>(null);
  /** The latest change a multiselect announces to assistive technology. */
  protected readonly announcement = signal('');
  protected readonly results = signal<NhSelectResults<T> | null>(null);
  protected readonly failure = signal<NhSelectFailure | null>(null);
  /** Options chosen or created that the inputs may not contain, so their labels stay known. */
  protected readonly remembered = signal<readonly NhSelectOption<T>[]>([]);
  /** Disabled by the form through `setDisabledState`. */
  protected readonly formDisabled = signal(false);
  protected onTouched: () => void = () => undefined;

  private searchTimer?: ReturnType<typeof setTimeout>;
  private searchSubscription?: Subscription;
  private createSubscription?: Subscription;
  private searchSequence = 0;
  private announcements = 0;
  /** The text of the latest search and create, to try them again after a failure. */
  private lastSearchText = '';
  private lastCreateText = '';

  private readonly config = computed<NhSelectConfig>(() => {
    const source = this.configSource;
    if (source === null) {
      return {};
    }

    return isSignal(source) ? source() : source;
  });

  readonly resolvedTexts = computed<NhSelectTexts>(() => ({
    ...NH_SELECT_DEFAULT_TEXTS,
    ...this.config().texts,
    ...this.texts()
  }));
  readonly isDisabled = computed(() => this.disabled() || this.formDisabled());
  readonly resolvedSize = computed<NhSelectSize>(() => this.size() ?? this.config().size ?? 'default');
  readonly resolvedMinQueryLength = computed(() => Math.max(0, this.minQueryLength() ?? this.config().minQueryLength ?? 2));
  private readonly resolvedDebounceMs = computed(() => Math.max(0, this.searchDebounceMs() ?? this.config().searchDebounceMs ?? 250));
  /** The typed text without surrounding white space. */
  protected readonly typed = computed(() => (this.query() ?? '').trim());

  readonly hasFailure = computed(() => this.failure() !== null);
  /** Why the latest search or create failed, as the field shows it. */
  readonly failureText = computed<string | null>(() => {
    const failure = this.failure();
    if (failure === null) {
      return null;
    }

    const describe = this.errorText() ?? this.config().errorText;
    const described = describe ? describe(failure.error, failure.operation) : null;
    if (described) {
      return described;
    }

    const texts = this.resolvedTexts();
    return failure.operation === 'search' ? texts.searchFailed : texts.createFailed;
  });

  /** The options that match the typed text, or the server's answer for it. */
  readonly visibleOptions = computed<readonly NhSelectOption<T>[]>(() => {
    if (this.search()) {
      const results = this.results();
      return results !== null && results.query === this.typed() ? results.options : [];
    }

    const text = this.typed().toLocaleLowerCase();
    const options = this.options();
    return text.length === 0 ? options : options.filter(option => matches(option, text));
  });

  /** The typed text a "Create" row offers, or `null` when there is none. */
  readonly createText = computed<string | null>(() => {
    const text = this.typed();
    if (this.create() === null || text.length === 0 || this.searching() || this.failure()?.operation === 'search') {
      return null;
    }

    if (this.search() && this.results()?.query !== text) {
      return null;
    }

    const wanted = text.toLocaleLowerCase();
    const taken = (option: NhSelectOption<T>) => option.label.trim().toLocaleLowerCase() === wanted;
    const exists = this.options().some(taken)
      || this.visibleOptions().some(taken)
      || this.remembered().some(taken)
      || this.selectedOptions().some(taken);
    return exists ? null : text;
  });

  readonly rows = computed<readonly NhSelectRow<T>[]>(() => {
    const rows: NhSelectRow<T>[] = [];
    const none = this.noneRow();
    if (none !== null) {
      rows.push(none);
    }

    for (const option of this.visibleOptions()) {
      rows.push({
        kind: 'option',
        option,
        label: option.label,
        description: option.description ?? null,
        icon: option.icon ?? null,
        image: option.image ?? null,
        classes: option.classes ?? null,
        selected: this.isChosen(option.value),
        disabled: option.disabled === true
      });
    }

    const createText = this.createText();
    if (createText !== null) {
      const texts = this.resolvedTexts();
      rows.push({
        kind: 'create',
        option: null,
        label: this.creating() ? texts.creating : texts.create(createText),
        description: null,
        icon: null,
        image: null,
        classes: null,
        selected: false,
        disabled: this.creating()
      });
    }

    return rows;
  });

  /** Why the list has no (more) rows: searching, a failed search, too few characters, or nothing found. */
  readonly statusText = computed<string | null>(() => {
    const texts = this.resolvedTexts();
    if (this.searching()) {
      return texts.searching;
    }

    if (this.failure()?.operation === 'search') {
      return texts.searchFailed;
    }

    const minQueryLength = this.resolvedMinQueryLength();
    if (this.search() && this.typed().length < minQueryLength) {
      return texts.typeMore(minQueryLength);
    }

    return this.rows().length === 0 ? texts.noResults : null;
  });

  /** What a polite live region reads: the latest change, otherwise the state of the open list. */
  readonly liveText = computed(() => this.announcement() || (this.open() ? this.statusText() ?? '' : ''));

  readonly activeId = computed(() => {
    const index = this.active();
    return this.open() && index >= 0 && index < this.rows().length ? this.optionId(index) : null;
  });

  /** The field describes itself with the host's ids, the reason of a failure and what is chosen. */
  readonly fieldDescribedBy = computed(() =>
    [this.describedBy(), this.failure() ? this.errorId : null, this.selectionDescriptionId()]
      .filter(Boolean)
      .join(' ') || null);

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.cancelSearch();
      this.createSubscription?.unsubscribe();
    });

    // Another search function starts over, and searches again when the list is open.
    effect(() => {
      this.search();
      untracked(() => {
        this.cancelSearch();
        this.results.set(null);
        if (this.failure()?.operation === 'search') {
          this.failure.set(null);
        }
        if (this.open() && this.search()) {
          this.startSearch(this.query() ?? '', true);
        }
      });
    });

    // A control that becomes disabled closes its list.
    effect(() => {
      if (this.isDisabled()) {
        untracked(() => this.close());
      }
    });
  }

  /** Whether the value is chosen. */
  protected abstract isChosen(value: T): boolean;

  /**
   * Chooses an option: `toggle` for a click or Enter, `add` for an option `create` answered, which a
   * multiselect adds even when it was already chosen.
   */
  protected abstract choose(option: NhSelectOption<T>, mode: 'toggle' | 'add'): void;

  /** The id of an element that describes what is chosen, or `null`. */
  protected selectionDescriptionId(): string | null {
    return null;
  }

  /** The empty choice of a combobox; none by default. */
  protected noneRow(): NhSelectRow<T> | null {
    return null;
  }

  /** Chooses the empty choice of a combobox. */
  protected chooseNone(): void {
    this.close();
  }

  /** Backspace in an empty search field; a multiselect removes its last chip. */
  protected onEmptyBackspace(): void {
    // Nothing to remove by default.
  }

  optionId(index: number): string {
    return `${this.listId}-option-${index}`;
  }

  /** Moves the focus to the text field, for example after a refused save. */
  focus(): void {
    this.field().nativeElement.focus();
  }

  /** Opens the list, or closes it when it is open. */
  toggle(): void {
    if (this.isDisabled()) {
      return;
    }

    if (this.open()) {
      this.close();
    } else {
      this.focus();
      this.openList();
    }
  }

  /** Opens the list and points at the chosen option, or the first one. */
  openList(): void {
    if (this.isDisabled() || this.open()) {
      return;
    }

    this.showList();
    this.active.set(this.initialActive());
    const text = this.typed();
    if (this.search() && text.length >= this.resolvedMinQueryLength() && this.results()?.query !== text && !this.searching()) {
      this.startSearch(this.query() ?? '', true);
    }
  }

  /** Closes the list and forgets the typed text; the value stays. */
  close(): void {
    const wasOpen = this.open();
    this.cancelSearch();
    this.open.set(false);
    this.query.set(null);
    this.active.set(-1);
    this.announcement.set('');
    if (wasOpen) {
      this.openChange.emit(false);
    }
  }

  /** Repeats the search or create that failed and shows the list. */
  retry(): void {
    const failure = this.failure();
    if (failure === null || this.isDisabled()) {
      return;
    }

    if (failure.operation === 'search') {
      this.query.set(this.lastSearchText);
      this.showList();
      this.active.set(-1);
      this.startSearch(this.lastSearchText, true);
    } else {
      this.query.set(this.lastCreateText);
      this.showList();
      this.runCreate(this.lastCreateText);
    }

    this.focus();
  }

  registerOnTouched(onTouched: () => void): void {
    this.onTouched = onTouched;
  }

  setDisabledState(disabled: boolean): void {
    this.formDisabled.set(disabled);
  }

  onInput(event: Event): void {
    const text = (event.target as HTMLInputElement).value;
    this.query.set(text);
    this.announcement.set('');
    if (this.failure()?.operation === 'create') {
      this.failure.set(null);
    }

    this.showList();
    if (this.search()) {
      this.active.set(-1);
      this.startSearch(text);
    } else {
      this.active.set(this.nextEnabled(-1, 1));
    }
  }

  onKeydown(event: KeyboardEvent): void {
    if (event.isComposing) {
      return;
    }

    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        if (!this.open()) {
          this.openList();
        } else if (!event.altKey) {
          this.move(1);
        }
        break;
      case 'ArrowUp':
        event.preventDefault();
        if (event.altKey) {
          this.close();
        } else if (!this.open()) {
          this.openList();
        } else {
          this.move(-1);
        }
        break;
      case 'Home':
      case 'End':
        // With the list open they move through the options; otherwise they move the caret.
        if (this.open()) {
          event.preventDefault();
          this.pointAt(event.key === 'Home' ? this.nextEnabled(-1, 1) : this.nextEnabled(this.rows().length, -1));
        }
        break;
      case 'Enter':
        if (this.open()) {
          event.preventDefault();
          const row = this.rows()[this.active()];
          if (row) {
            this.selectRow(row);
          } else if (!this.multiple) {
            this.close();
          }
        }
        break;
      case 'Escape':
        if (this.open()) {
          // Handled: a surrounding modal checks defaultPrevented and stays open, and a containing
          // disclosure with its own Escape handler does not see the key.
          event.preventDefault();
          event.stopPropagation();
          this.close();
        }
        break;
      case 'Tab':
        this.close();
        break;
      case 'Backspace':
        if (!event.repeat && (this.query() ?? '') === '') {
          this.onEmptyBackspace();
        }
        break;
    }
  }

  onBlur(): void {
    this.close();
    this.onTouched();
  }

  /** A press on the field's frame or its buttons keeps the focus in the text field. */
  onControlMousedown(event: MouseEvent): void {
    if (event.button !== 0 || event.target === this.field().nativeElement) {
      return;
    }

    event.preventDefault();
    if (!this.isDisabled()) {
      this.focus();
    }
  }

  /** A click on the frame around the text opens the list, as a click in the text does. */
  onControlClick(event: MouseEvent): void {
    const target = event.target as HTMLElement | null;
    if (target?.closest('button')) {
      return;
    }

    this.openList();
  }

  /** Points at a row under the pointer. */
  hover(index: number): void {
    const row = this.rows()[index];
    if (row && !row.disabled) {
      this.active.set(index);
    }
  }

  selectRow(row: NhSelectRow<T>): void {
    if (row.disabled || this.isDisabled()) {
      return;
    }

    if (row.kind === 'none') {
      this.chooseNone();
    } else if (row.kind === 'create') {
      const text = this.createText();
      if (text !== null) {
        this.runCreate(text);
      }
    } else if (row.option !== null) {
      this.choose(row.option, 'toggle');
    }
  }

  /** The same choice, also for `null` and `undefined`, which only equal each other. */
  protected same(left: T | null | undefined, right: T | null | undefined): boolean {
    if (left === null || left === undefined || right === null || right === undefined) {
      return (left ?? null) === (right ?? null);
    }

    return this.compareWith()(left, right);
  }

  /** The option of a value from every source the control knows, or `null`. */
  protected findOption(value: T): NhSelectOption<T> | null {
    const same = (option: NhSelectOption<T>) => this.same(option.value, value);
    return this.options().find(same)
      ?? this.results()?.options.find(same)
      ?? this.remembered().find(same)
      ?? this.selectedOptions().find(same)
      ?? null;
  }

  /** The label of a value no option describes: the primitive as text, nothing for an object. */
  protected fallbackLabel(value: T): string {
    return typeof value === 'object' || typeof value === 'function' ? '' : String(value);
  }

  /** Keeps the option of a chosen or created value, so its label shows after the list changes. */
  protected remember(option: NhSelectOption<T>): void {
    if (this.options().some(known => this.same(known.value, option.value))) {
      return;
    }

    this.remembered.update(known => [...known.filter(item => !this.same(item.value, option.value)), option]);
  }

  protected clearFailure(): void {
    this.failure.set(null);
  }

  /** Reads a change to assistive technology, also when the same text follows itself. */
  protected announce(text: string): void {
    this.announcements++;
    this.announcement.set(this.announcements % 2 === 0 ? text : `${text} `);
  }

  /** The next row from `from` in the direction of `step` that can be chosen, or `-1`. */
  protected nextEnabled(from: number, step: 1 | -1): number {
    const rows = this.rows();
    for (let index = from + step; index >= 0 && index < rows.length; index += step) {
      if (!rows[index].disabled) {
        return index;
      }
    }

    return -1;
  }

  private initialActive(): number {
    if (!this.multiple) {
      const chosen = this.rows().findIndex(row => row.selected && !row.disabled);
      if (chosen >= 0) {
        return chosen;
      }
    }

    return this.nextEnabled(-1, 1);
  }

  private move(step: 1 | -1): void {
    const current = this.active();
    const from = current < 0 ? (step > 0 ? -1 : this.rows().length) : current;
    const next = this.nextEnabled(from, step);
    if (next >= 0) {
      this.pointAt(next);
    }
  }

  private pointAt(index: number): void {
    if (index < 0) {
      return;
    }

    this.active.set(index);
    afterNextRender({
      read: () => {
        const id = this.activeId();
        if (id) {
          this.host.nativeElement.ownerDocument.getElementById(id)?.scrollIntoView?.({ block: 'nearest' });
        }
      }
    }, { injector: this.injector });
  }

  private showList(): void {
    if (this.open()) {
      return;
    }

    this.above.set(this.opensAbove());
    this.listLabel.set(this.labelText());
    this.open.set(true);
    this.openChange.emit(true);
  }

  private opensAbove(): boolean {
    const view = this.host.nativeElement.ownerDocument.defaultView;
    if (!view) {
      return false;
    }

    const rect = this.host.nativeElement.getBoundingClientRect();
    const below = view.innerHeight - rect.bottom;
    return below < popupRoom && rect.top > below;
  }

  /** The name of the field, for the list: `ariaLabel`, the labelling elements or the `<label>`. */
  private labelText(): string | null {
    const own = this.ariaLabel();
    if (own) {
      return own;
    }

    const document = this.host.nativeElement.ownerDocument;
    const labelledBy = (this.ariaLabelledBy() ?? '')
      .split(/\s+/)
      .filter(Boolean)
      .map(id => document.getElementById(id)?.textContent?.trim() ?? '')
      .filter(Boolean)
      .join(' ');
    if (labelledBy) {
      return labelledBy;
    }

    const labels = Array.from(this.field().nativeElement.labels ?? [])
      .map(label => label.textContent?.trim() ?? '')
      .filter(Boolean)
      .join(' ');
    return labels || null;
  }

  private startSearch(text: string, immediate = false): void {
    this.cancelSearch();
    const sequence = ++this.searchSequence;
    this.lastSearchText = text;
    if (this.failure()?.operation === 'search') {
      this.failure.set(null);
    }

    const search = this.search();
    const query = text.trim();
    if (!search || query.length < this.resolvedMinQueryLength()) {
      this.results.set(null);
      return;
    }

    this.searching.set(true);
    const run = () => {
      this.searchSubscription = this.answer(() => search(query)).pipe(take(1)).subscribe({
        next: options => {
          if (sequence !== this.searchSequence) {
            return;
          }

          this.results.set({ query, options });
          this.searching.set(false);
          this.active.set(this.nextEnabled(-1, 1));
        },
        error: (error: unknown) => {
          if (sequence !== this.searchSequence) {
            return;
          }

          this.results.set(null);
          this.searching.set(false);
          this.failure.set({ operation: 'search', error });
        },
        complete: () => {
          // A search that completes without an answer found nothing.
          if (sequence === this.searchSequence && this.searching()) {
            this.results.set({ query, options: [] });
            this.searching.set(false);
          }
        }
      });
    };

    const delay = immediate ? 0 : this.resolvedDebounceMs();
    if (delay === 0) {
      run();
    } else {
      this.searchTimer = setTimeout(run, delay);
    }
  }

  private cancelSearch(): void {
    clearTimeout(this.searchTimer);
    this.searchTimer = undefined;
    this.searchSubscription?.unsubscribe();
    this.searchSubscription = undefined;
    this.searchSequence++;
    this.searching.set(false);
  }

  private runCreate(text: string): void {
    const create = this.create();
    if (create === null || this.creating()) {
      return;
    }

    this.lastCreateText = text;
    this.failure.set(null);
    this.creating.set(true);
    this.createSubscription?.unsubscribe();
    this.createSubscription = this.answer(() => create(text)).pipe(take(1)).subscribe({
      next: option => {
        this.creating.set(false);
        this.remember(option);
        this.optionCreated.emit(option);
        this.choose(option, 'add');
      },
      error: (error: unknown) => {
        this.creating.set(false);
        this.failure.set({ operation: 'create', error });
      },
      complete: () => {
        this.creating.set(false);
      }
    });
  }

  /** The answer of a search or create as an observable, also when the call itself throws. */
  private answer<R>(call: () => Observable<R> | Promise<R>): Observable<R> {
    try {
      const result = call();
      return isObservable(result) ? result : from(result);
    } catch (error: unknown) {
      return throwError(() => error);
    }
  }
}
