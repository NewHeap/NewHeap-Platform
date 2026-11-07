import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { Observable, Subject, of, throwError } from 'rxjs';
import { NhComboboxComponent } from './nh-combobox.component';
import { NhSelectConfig, NhSelectOption, provideNhSelect } from './nh-select.models';

const statuses: NhSelectOption<string>[] = [
  { value: 'draft', label: 'Draft', description: 'Not yet planned.' },
  { value: 'active', label: 'Active', description: 'Work in progress.' },
  { value: 'archived', label: 'Archived', description: 'Read only.', disabled: true },
  { value: 'done', label: 'Done', description: 'Delivered to the customer.' }
];

function input(fixture: ComponentFixture<unknown>, selector = 'input[role="combobox"]'): HTMLInputElement {
  return (fixture.nativeElement as HTMLElement).querySelector(selector) as HTMLInputElement;
}

function key(fixture: ComponentFixture<unknown>, field: HTMLInputElement, name: string, init: KeyboardEventInit = {}): KeyboardEvent {
  const event = new KeyboardEvent('keydown', { key: name, bubbles: true, cancelable: true, ...init });
  field.dispatchEvent(event);
  fixture.detectChanges();
  return event;
}

function type(fixture: ComponentFixture<unknown>, field: HTMLInputElement, text: string): void {
  field.value = text;
  field.dispatchEvent(new Event('input'));
  fixture.detectChanges();
}

function optionTexts(fixture: ComponentFixture<unknown>): string[] {
  return Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('[role="option"] .nh-select-label'))
    .map(label => label.textContent?.trim() ?? '');
}

function activeText(fixture: ComponentFixture<unknown>, field: HTMLInputElement): string | null {
  const id = field.getAttribute('aria-activedescendant');
  return id ? (fixture.nativeElement as HTMLElement).querySelector(`#${id} .nh-select-label`)?.textContent?.trim() ?? null : null;
}

@Component({
  standalone: true,
  imports: [NhComboboxComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <label for="status">Status</label>
    <p id="status-hint">The status of the project.</p>
    <nh-combobox
      inputId="status"
      describedBy="status-hint"
      [options]="options()"
      [value]="value()"
      [noneLabel]="noneLabel()"
      [disabled]="disabled()"
      (valueChange)="changes.push($event); value.set($event)"
      (optionChange)="chosen.push($event)" />
  `
})
class ValueHost {
  readonly options = signal<NhSelectOption<string>[]>(statuses);
  readonly value = signal<string | null>('active');
  readonly noneLabel = signal<string | null>(null);
  readonly disabled = signal(false);
  readonly changes: (string | null)[] = [];
  readonly chosen: (NhSelectOption<string> | null)[] = [];
}

describe('NhComboboxComponent', () => {
  describe('as a combobox with a listbox popup', () => {
    let fixture: ComponentFixture<ValueHost>;
    let field: HTMLInputElement;

    beforeEach(() => {
      fixture = TestBed.createComponent(ValueHost);
      fixture.detectChanges();
      field = input(fixture);
    });

    it('is a labelled, described combobox that shows the chosen option and its description', () => {
      expect(field.getAttribute('role')).toBe('combobox');
      expect(field.getAttribute('aria-autocomplete')).toBe('list');
      expect(field.getAttribute('aria-expanded')).toBe('false');
      expect(field.getAttribute('aria-describedby')).toBe('status-hint');
      expect(field.labels?.[0]?.textContent).toBe('Status');
      expect(field.value).toBe('Active');
      expect((fixture.nativeElement as HTMLElement).querySelector('.nh-select-chosen')?.textContent).toContain('Work in progress.');
    });

    it('opens on the chosen option, moves with the arrow keys, Home and End past disabled options, and chooses with Enter', () => {
      key(fixture, field, 'ArrowDown');
      expect(field.getAttribute('aria-expanded')).toBe('true');
      const list = (fixture.nativeElement as HTMLElement).querySelector(`#${field.getAttribute('aria-controls')}`) as HTMLElement;
      expect(list.getAttribute('role')).toBe('listbox');
      expect(list.getAttribute('aria-label')).toBe('Status');
      expect(optionTexts(fixture)).toEqual(['Draft', 'Active', 'Archived', 'Done']);
      expect(activeText(fixture, field)).toBe('Active');
      expect(list.querySelector('[aria-selected="true"] .nh-select-label')?.textContent).toBe('Active');
      expect(list.querySelector('[aria-disabled="true"] .nh-select-label')?.textContent).toBe('Archived');

      key(fixture, field, 'ArrowDown');
      expect(activeText(fixture, field)).toBe('Done');
      key(fixture, field, 'ArrowDown');
      expect(activeText(fixture, field)).toBe('Done');
      key(fixture, field, 'Home');
      expect(activeText(fixture, field)).toBe('Draft');
      key(fixture, field, 'End');
      expect(activeText(fixture, field)).toBe('Done');
      key(fixture, field, 'ArrowUp');
      expect(activeText(fixture, field)).toBe('Active');

      key(fixture, field, 'ArrowUp');
      const enter = key(fixture, field, 'Enter');
      expect(enter.defaultPrevented).toBeTrue();
      expect(fixture.componentInstance.changes).toEqual(['draft']);
      expect(fixture.componentInstance.chosen).toEqual([statuses[0]]);
      expect(field.getAttribute('aria-expanded')).toBe('false');
      expect(field.getAttribute('aria-activedescendant')).toBeNull();
      expect(field.value).toBe('Draft');
    });

    it('filters on label and description and keeps the value when Escape closes the list', () => {
      field.click();
      fixture.detectChanges();
      type(fixture, field, 'customer');
      expect(optionTexts(fixture)).toEqual(['Done']);
      expect(activeText(fixture, field)).toBe('Done');

      type(fixture, field, 'zzz');
      expect(optionTexts(fixture)).toEqual([]);
      expect((fixture.nativeElement as HTMLElement).querySelector('.nh-select-state')?.textContent).toBe('Nothing found.');
      expect((fixture.nativeElement as HTMLElement).querySelector('[role="status"]')?.textContent).toBe('Nothing found.');

      const escape = key(fixture, field, 'Escape');
      expect(escape.defaultPrevented).toBeTrue();
      expect(field.getAttribute('aria-expanded')).toBe('false');
      expect(field.value).toBe('Active');
      expect(fixture.componentInstance.changes).toEqual([]);
    });

    it('marks Escape as handled only while the list is open, so a surrounding modal stays open', () => {
      // A modal listens on the document and ignores a key that is already handled, as NhModal does.
      const modalCloses: KeyboardEvent[] = [];
      const modal = (event: KeyboardEvent) => {
        if (event.key === 'Escape' && !event.defaultPrevented) {
          modalCloses.push(event);
        }
      };
      document.addEventListener('keydown', modal);
      try {
        key(fixture, field, 'ArrowDown');
        const open = key(fixture, field, 'Escape');
        expect(open.defaultPrevented).toBeTrue();
        expect(modalCloses).toEqual([]);

        // Closed, Escape belongs to the modal around the field.
        const closed = key(fixture, field, 'Escape');
        expect(closed.defaultPrevented).toBeFalse();
        expect(modalCloses).toEqual([closed]);
      } finally {
        document.removeEventListener('keydown', modal);
      }
    });

    it('closes on Alt+ArrowUp and Tab, opens with the toggle button and chooses with a click', () => {
      key(fixture, field, 'ArrowDown', { altKey: true });
      expect(field.getAttribute('aria-expanded')).toBe('true');
      key(fixture, field, 'ArrowUp', { altKey: true });
      expect(field.getAttribute('aria-expanded')).toBe('false');

      key(fixture, field, 'ArrowDown');
      key(fixture, field, 'Tab');
      expect(field.getAttribute('aria-expanded')).toBe('false');

      const toggle = (fixture.nativeElement as HTMLElement).querySelector('.nh-select-toggle') as HTMLButtonElement;
      expect(toggle.getAttribute('aria-label')).toBe('Show the options');
      expect(toggle.tabIndex).toBe(-1);
      toggle.click();
      fixture.detectChanges();
      expect(field.getAttribute('aria-expanded')).toBe('true');
      expect(document.activeElement).toBe(field);

      const done = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll<HTMLElement>('[role="option"]'))[3];
      done.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true }));
      done.click();
      fixture.detectChanges();
      expect(fixture.componentInstance.changes).toEqual(['done']);
      expect(field.value).toBe('Done');

      // A disabled option is not chosen.
      toggle.click();
      fixture.detectChanges();
      (Array.from((fixture.nativeElement as HTMLElement).querySelectorAll<HTMLElement>('[role="option"]'))[2]).click();
      fixture.detectChanges();
      expect(fixture.componentInstance.changes).toEqual(['done']);
    });

    it('offers an explicit empty choice that emits its value and shows its label', () => {
      fixture.componentInstance.noneLabel.set('No status');
      fixture.detectChanges();
      key(fixture, field, 'ArrowDown');
      expect(optionTexts(fixture)).toEqual(['No status', 'Draft', 'Active', 'Archived', 'Done']);
      key(fixture, field, 'Home');
      key(fixture, field, 'Enter');
      expect(fixture.componentInstance.changes).toEqual([null]);
      expect(fixture.componentInstance.chosen).toEqual([null]);
      expect(field.value).toBe('No status');

      // Typed text hides the empty choice unless it matches.
      key(fixture, field, 'ArrowDown');
      type(fixture, field, 'dra');
      expect(optionTexts(fixture)).toEqual(['Draft']);
      type(fixture, field, 'no st');
      expect(optionTexts(fixture)).toEqual(['No status']);
    });

    it('updates inside an OnPush host when options change and is disabled by its input', () => {
      fixture.componentInstance.options.set([...statuses, { value: 'paused', label: 'Paused' }]);
      fixture.detectChanges();
      key(fixture, field, 'ArrowDown');
      expect(optionTexts(fixture)).toContain('Paused');

      fixture.componentInstance.disabled.set(true);
      fixture.detectChanges();
      expect(field.disabled).toBeTrue();
      expect(field.getAttribute('aria-expanded')).toBe('false');
      expect((fixture.nativeElement as HTMLElement).querySelector('.nh-select-toggle')?.hasAttribute('disabled')).toBeTrue();
      expect((fixture.nativeElement as HTMLElement).querySelector('nh-combobox')?.classList).toContain('nh-select-disabled');
      field.click();
      key(fixture, field, 'ArrowDown');
      expect(field.getAttribute('aria-expanded')).toBe('false');
    });
  });

  describe('as a form control', () => {
    @Component({
      standalone: true,
      imports: [NhComboboxComponent, ReactiveFormsModule],
      changeDetection: ChangeDetectionStrategy.OnPush,
      template: `<nh-combobox ariaLabel="Priority" [formControl]="control" [options]="options" size="compact" />`
    })
    class FormHost {
      readonly options: NhSelectOption<number | boolean | string>[] = [
        { value: 0, label: 'Zero' },
        { value: 1, label: 'One' },
        { value: false, label: 'No' },
        { value: '', label: 'Empty text' }
      ];
      readonly control = new FormControl<number | boolean | string | null>(1);
    }

    let fixture: ComponentFixture<FormHost>;
    let field: HTMLInputElement;

    beforeEach(() => {
      fixture = TestBed.createComponent(FormHost);
      fixture.detectChanges();
      field = input(fixture);
    });

    it('never reports a value the form writes back as a change and stays pristine', () => {
      const values: unknown[] = [];
      fixture.componentInstance.control.valueChanges.subscribe(value => values.push(value));

      fixture.componentInstance.control.setValue(0);
      fixture.detectChanges();
      expect(values).toEqual([0]);
      expect(field.value).toBe('Zero');
      expect(fixture.componentInstance.control.dirty).toBeFalse();

      fixture.componentInstance.control.reset(false);
      fixture.detectChanges();
      expect(values).toEqual([0, false]);
      expect(field.value).toBe('No');
      expect(fixture.componentInstance.control.pristine).toBeTrue();
    });

    it('keeps 0, false and empty text as chosen values and marks the control dirty on a choice', () => {
      key(fixture, field, 'ArrowDown');
      key(fixture, field, 'Home');
      key(fixture, field, 'Enter');
      expect(fixture.componentInstance.control.value).toBe(0);
      expect(fixture.componentInstance.control.dirty).toBeTrue();
      expect(field.value).toBe('Zero');

      key(fixture, field, 'ArrowDown');
      expect(activeText(fixture, field)).toBe('Zero');
      key(fixture, field, 'ArrowDown');
      key(fixture, field, 'ArrowDown');
      key(fixture, field, 'Enter');
      expect(fixture.componentInstance.control.value).toBeFalse();
      expect(field.value).toBe('No');

      key(fixture, field, 'ArrowDown');
      key(fixture, field, 'End');
      key(fixture, field, 'Enter');
      expect(fixture.componentInstance.control.value).toBe('');
      expect(field.value).toBe('Empty text');
    });

    it('reports touched when the field loses focus and follows the disabled state of the form', () => {
      expect(fixture.componentInstance.control.touched).toBeFalse();
      field.dispatchEvent(new FocusEvent('blur'));
      expect(fixture.componentInstance.control.touched).toBeTrue();

      fixture.componentInstance.control.disable();
      fixture.detectChanges();
      expect(field.disabled).toBeTrue();
      fixture.componentInstance.control.enable();
      fixture.detectChanges();
      expect(field.disabled).toBeFalse();
    });

    it('takes its accessible name from ariaLabel and the compact size from its input', () => {
      expect(field.getAttribute('aria-label')).toBe('Priority');
      key(fixture, field, 'ArrowDown');
      expect((fixture.nativeElement as HTMLElement).querySelector('[role="listbox"]')?.getAttribute('aria-label')).toBe('Priority');
      expect((fixture.nativeElement as HTMLElement).querySelector('nh-combobox')?.classList).toContain('nh-select-compact');
    });
  });

  describe('with object values', () => {
    interface Person { id: number; name: string }

    @Component({
      standalone: true,
      imports: [NhComboboxComponent, ReactiveFormsModule],
      template: `<nh-combobox ariaLabel="Owner" [formControl]="control" [options]="options" [compareWith]="sameId" />`
    })
    class ObjectHost {
      readonly options: NhSelectOption<Person>[] = [
        { value: { id: 1, name: 'Ada' }, label: 'Ada Lovelace' },
        { value: { id: 2, name: 'Grace' }, label: 'Grace Hopper' }
      ];
      readonly control = new FormControl<Person | null>({ id: 2, name: 'Grace' });
      readonly sameId = (left: Person, right: Person) => left.id === right.id;
    }

    it('compares them with compareWith', () => {
      const fixture = TestBed.createComponent(ObjectHost);
      fixture.detectChanges();
      const field = input(fixture);
      expect(field.value).toBe('Grace Hopper');
      key(fixture, field, 'ArrowDown');
      expect(activeText(fixture, field)).toBe('Grace Hopper');
    });
  });

  describe('with a server search', () => {
    @Component({
      standalone: true,
      imports: [NhComboboxComponent],
      template: `
        <label for="owner">Owner</label>
        <nh-combobox
          inputId="owner"
          describedBy="owner-hint"
          [search]="search"
          [minQueryLength]="minQueryLength()"
          [value]="value()"
          [selectedLabel]="selectedLabel"
          [errorText]="errorText"
          (valueChange)="value.set($event)"
          (optionChange)="chosen.push($event)" />
      `
    })
    class SearchHost {
      readonly minQueryLength = signal(2);
      readonly value = signal<string | null>('u-9');
      readonly selectedLabel = 'Edsger Dijkstra';
      readonly queries: string[] = [];
      readonly answers: Observable<readonly NhSelectOption<string>[]>[] = [];
      readonly chosen: (NhSelectOption<string> | null)[] = [];
      readonly search = (query: string) => {
        this.queries.push(query);
        return this.answers.shift() ?? of([]);
      };
      readonly errorText = (error: unknown) => error instanceof Error ? `Search unavailable: ${error.message}` : null;
    }

    let fixture: ComponentFixture<SearchHost>;
    let field: HTMLInputElement;

    beforeEach(() => {
      fixture = TestBed.createComponent(SearchHost);
      fixture.detectChanges();
      field = input(fixture);
    });

    it('shows the stored value by its label and searches after the minimum length and a pause', fakeAsync(() => {
      expect(field.value).toBe('Edsger Dijkstra');
      fixture.componentInstance.answers.push(of([
        { value: 'u-1', label: 'Ada Lovelace', description: 'ada@example.test' },
        { value: 'u-2', label: 'Grace Hopper' }
      ]));

      field.click();
      fixture.detectChanges();
      expect((fixture.nativeElement as HTMLElement).querySelector('.nh-select-state')?.textContent).toBe('Type at least 2 characters.');

      type(fixture, field, 'a');
      expect(fixture.componentInstance.queries).toEqual([]);
      type(fixture, field, 'ad');
      expect((fixture.nativeElement as HTMLElement).querySelector('.nh-select-state')?.textContent).toBe('Searching…');
      expect((fixture.nativeElement as HTMLElement).querySelector('[role="listbox"]')?.getAttribute('aria-busy')).toBe('true');
      tick(249);
      expect(fixture.componentInstance.queries).toEqual([]);
      tick(1);
      fixture.detectChanges();
      expect(fixture.componentInstance.queries).toEqual(['ad']);
      expect(optionTexts(fixture)).toEqual(['Ada Lovelace', 'Grace Hopper']);
      expect(activeText(fixture, field)).toBe('Ada Lovelace');

      key(fixture, field, 'Enter');
      expect(fixture.componentInstance.value()).toBe('u-1');
      expect(fixture.componentInstance.chosen[0]?.label).toBe('Ada Lovelace');
      expect(field.value).toBe('Ada Lovelace');
      expect((fixture.nativeElement as HTMLElement).querySelector('.nh-select-chosen')?.textContent).toContain('ada@example.test');
    }));

    it('says when a search found nothing and ignores an answer to an older text', fakeAsync(() => {
      const slow = new Subject<readonly NhSelectOption<string>[]>();
      fixture.componentInstance.answers.push(slow, of([]));
      field.click();
      type(fixture, field, 'gr');
      tick(250);
      type(fixture, field, 'grx');
      tick(250);
      fixture.detectChanges();
      expect(fixture.componentInstance.queries).toEqual(['gr', 'grx']);
      expect(slow.observed).toBeFalse();
      slow.next([{ value: 'u-2', label: 'Grace Hopper' }]);
      fixture.detectChanges();
      expect(optionTexts(fixture)).toEqual([]);
      expect((fixture.nativeElement as HTMLElement).querySelector('.nh-select-state')?.textContent).toBe('Nothing found.');
    }));

    it('says why a search failed instead of "nothing found", and searches again', fakeAsync(() => {
      fixture.componentInstance.answers.push(
        throwError(() => new Error('timeout')),
        of([{ value: 'u-2', label: 'Grace Hopper' }]));
      field.click();
      type(fixture, field, 'gra');
      tick(250);
      fixture.detectChanges();

      const element = fixture.nativeElement as HTMLElement;
      const error = element.querySelector('[data-nh-select-error]') as HTMLElement;
      expect(error.getAttribute('role')).toBe('alert');
      expect(error.textContent).toContain('Search unavailable: timeout');
      expect(element.querySelector('.nh-select-state')?.textContent).toBe('The search failed.');
      expect(field.getAttribute('aria-describedby')).toBe(`owner-hint ${error.id}`);

      field.dispatchEvent(new FocusEvent('blur'));
      fixture.detectChanges();
      (element.querySelector('[data-nh-select-retry]') as HTMLButtonElement).click();
      fixture.detectChanges();
      expect(fixture.componentInstance.queries).toEqual(['gra', 'gra']);
      expect(element.querySelector('[data-nh-select-error]')).toBeNull();
      expect(field.getAttribute('aria-expanded')).toBe('true');
      expect(field.value).toBe('gra');
      expect(optionTexts(fixture)).toEqual(['Grace Hopper']);
      expect(field.getAttribute('aria-describedby')).toBe('owner-hint');
    }));

    it('searches as soon as the list opens with a minimum length of 0', fakeAsync(() => {
      fixture.componentInstance.minQueryLength.set(0);
      fixture.componentInstance.answers.push(of([{ value: 'u-1', label: 'Ada Lovelace' }]));
      fixture.detectChanges();
      field.click();
      fixture.detectChanges();
      expect(fixture.componentInstance.queries).toEqual(['']);
      expect(optionTexts(fixture)).toEqual(['Ada Lovelace']);
    }));
  });

  describe('with create', () => {
    @Component({
      standalone: true,
      imports: [NhComboboxComponent, ReactiveFormsModule],
      template: `
        <nh-combobox
          ariaLabel="Label"
          [formControl]="control"
          [options]="options()"
          [create]="create"
          (optionCreated)="created.push($event)" />
      `
    })
    class CreateHost {
      readonly options = signal<NhSelectOption<string>[]>([{ value: 'bug', label: 'Bug' }, { value: 'feature', label: 'Feature' }]);
      readonly control = new FormControl<string | null>(null);
      readonly created: NhSelectOption<string>[] = [];
      readonly requests: string[] = [];
      failNext = false;
      readonly create = (text: string): Promise<NhSelectOption<string>> => {
        this.requests.push(text);
        if (this.failNext) {
          this.failNext = false;
          return Promise.reject(new Error('conflict'));
        }

        return Promise.resolve({ value: `label-${text.toLowerCase()}`, label: text });
      };
    }

    let fixture: ComponentFixture<CreateHost>;
    let field: HTMLInputElement;

    beforeEach(() => {
      fixture = TestBed.createComponent(CreateHost);
      fixture.detectChanges();
      field = input(fixture);
    });

    it('offers to create typed text that matches no option and chooses the created option', fakeAsync(() => {
      field.click();
      type(fixture, field, 'bug');
      expect(optionTexts(fixture)).toEqual(['Bug']);

      type(fixture, field, 'Urgent');
      expect(optionTexts(fixture)).toEqual(['Create “Urgent”']);
      expect(activeText(fixture, field)).toBe('Create “Urgent”');
      key(fixture, field, 'Enter');
      expect(optionTexts(fixture)).toEqual(['Creating…']);
      tick();
      fixture.detectChanges();

      expect(fixture.componentInstance.requests).toEqual(['Urgent']);
      expect(fixture.componentInstance.created).toEqual([{ value: 'label-urgent', label: 'Urgent' }]);
      expect(fixture.componentInstance.control.value).toBe('label-urgent');
      expect(fixture.componentInstance.control.dirty).toBeTrue();
      expect(field.value).toBe('Urgent');
      expect(field.getAttribute('aria-expanded')).toBe('false');

      // The created option is known now, so its text no longer offers to create it.
      field.click();
      type(fixture, field, 'urgent');
      expect(optionTexts(fixture)).toEqual([]);
    }));

    it('says why creating failed and tries again', fakeAsync(() => {
      fixture.componentInstance.failNext = true;
      field.click();
      type(fixture, field, 'Docs');
      key(fixture, field, 'Enter');
      tick();
      fixture.detectChanges();

      const element = fixture.nativeElement as HTMLElement;
      expect(element.querySelector('[data-nh-select-error]')?.textContent).toContain('The option could not be created.');
      expect(fixture.componentInstance.control.value).toBeNull();

      (element.querySelector('[data-nh-select-retry]') as HTMLButtonElement).click();
      tick();
      fixture.detectChanges();
      expect(fixture.componentInstance.requests).toEqual(['Docs', 'Docs']);
      expect(fixture.componentInstance.control.value).toBe('label-docs');
      expect(element.querySelector('[data-nh-select-error]')).toBeNull();
    }));
  });

  describe('with application-wide texts', () => {
    @Component({
      standalone: true,
      imports: [NhComboboxComponent],
      template: `<nh-combobox ariaLabel="Status" [options]="[]" [texts]="texts" />`
    })
    class TextHost {
      readonly texts = { noResults: 'Niets gevonden (input).' };
    }

    it('take texts from provideNhSelect, follow its signal and let the input win', () => {
      const config = signal<NhSelectConfig>({ texts: { toggle: 'Toon de keuzes', noResults: 'Niets gevonden.' } });
      TestBed.configureTestingModule({ providers: [provideNhSelect(config)] });
      const fixture = TestBed.createComponent(TextHost);
      fixture.detectChanges();
      const field = input(fixture);
      const toggle = (fixture.nativeElement as HTMLElement).querySelector('.nh-select-toggle') as HTMLButtonElement;
      expect(toggle.getAttribute('aria-label')).toBe('Toon de keuzes');

      key(fixture, field, 'ArrowDown');
      expect((fixture.nativeElement as HTMLElement).querySelector('.nh-select-state')?.textContent).toBe('Niets gevonden (input).');

      config.set({ texts: { toggle: 'Show choices' } });
      fixture.detectChanges();
      expect(toggle.getAttribute('aria-label')).toBe('Show choices');
    });
  });
});
