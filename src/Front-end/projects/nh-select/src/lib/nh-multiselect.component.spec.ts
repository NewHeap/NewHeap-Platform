import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { Observable, of, throwError } from 'rxjs';
import { NhMultiselectComponent } from './nh-multiselect.component';
import { NhSelectOption } from './nh-select.models';

const members: NhSelectOption<string>[] = [
  { value: 'ada', label: 'Ada Lovelace', description: 'Analyst', icon: 'ph ph-user' },
  { value: 'grace', label: 'Grace Hopper', description: 'Compiler author', image: 'data:image/gif;base64,R0lGODlhAQABAAAAACw=' },
  { value: 'edsger', label: 'Edsger Dijkstra', classes: 'is-external' },
  { value: 'owner', label: 'Project owner', disabled: true }
];

function element(fixture: ComponentFixture<unknown>): HTMLElement {
  return fixture.nativeElement as HTMLElement;
}

function field(fixture: ComponentFixture<unknown>): HTMLInputElement {
  return element(fixture).querySelector('input[role="combobox"]') as HTMLInputElement;
}

function key(fixture: ComponentFixture<unknown>, name: string, init: KeyboardEventInit = {}): KeyboardEvent {
  const event = new KeyboardEvent('keydown', { key: name, bubbles: true, cancelable: true, ...init });
  field(fixture).dispatchEvent(event);
  fixture.detectChanges();
  return event;
}

function type(fixture: ComponentFixture<unknown>, text: string): void {
  const input = field(fixture);
  input.value = text;
  input.dispatchEvent(new Event('input'));
  fixture.detectChanges();
}

function chips(fixture: ComponentFixture<unknown>): string[] {
  return Array.from(element(fixture).querySelectorAll('[data-nh-select-chip] .nh-select-chip-label'))
    .map(chip => chip.textContent?.trim() ?? '');
}

function optionTexts(fixture: ComponentFixture<unknown>): string[] {
  return Array.from(element(fixture).querySelectorAll('[role="option"] .nh-select-label'))
    .map(label => label.textContent?.trim() ?? '');
}

function selectedTexts(fixture: ComponentFixture<unknown>): string[] {
  return Array.from(element(fixture).querySelectorAll('[role="option"][aria-selected="true"] .nh-select-label'))
    .map(label => label.textContent?.trim() ?? '');
}

function live(fixture: ComponentFixture<unknown>): string {
  return element(fixture).querySelector('[role="status"]')?.textContent?.replace(/ /g, '').trim() ?? '';
}

@Component({
  standalone: true,
  imports: [NhMultiselectComponent, ReactiveFormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <label for="members">Members</label>
    <nh-multiselect inputId="members" placeholder="Add members" [formControl]="control" [options]="options()" />
  `
})
class FormHost {
  readonly options = signal<NhSelectOption<string>[]>(members);
  readonly control = new FormControl<string[]>(['owner', 'ada'], { nonNullable: true });
}

describe('NhMultiselectComponent', () => {
  describe('as a form control with chips', () => {
    let fixture: ComponentFixture<FormHost>;

    beforeEach(() => {
      fixture = TestBed.createComponent(FormHost);
      fixture.detectChanges();
    });

    it('shows the values as chips that describe the field, with the option icon, image and classes', () => {
      expect(chips(fixture)).toEqual(['Project owner', 'Ada Lovelace']);
      const input = field(fixture);
      const summaryId = input.getAttribute('aria-describedby');
      expect(element(fixture).querySelector(`#${summaryId}`)?.textContent).toBe('Project owner, Ada Lovelace');
      expect(input.placeholder).toBe('');
      expect(element(fixture).querySelector('[data-nh-select-chip] .ph-user')).not.toBeNull();

      // A chosen disabled option is locked: it has no remove button.
      const [owner, ada] = Array.from(element(fixture).querySelectorAll('[data-nh-select-chip]'));
      expect(owner.querySelector('[data-nh-select-chip-remove]')).toBeNull();
      expect(ada.querySelector('[data-nh-select-chip-remove]')?.getAttribute('aria-label')).toBe('Remove Ada Lovelace');

      fixture.componentInstance.control.setValue(['grace', 'edsger']);
      fixture.detectChanges();
      expect(element(fixture).querySelector('[data-nh-select-chip] img')).not.toBeNull();
      expect(element(fixture).querySelectorAll('[data-nh-select-chip]')[1].classList).toContain('is-external');
    });

    it('adds and removes options with Enter, keeps the list open and announces each change', () => {
      key(fixture, 'ArrowDown');
      const list = element(fixture).querySelector('[role="listbox"]') as HTMLElement;
      expect(list.getAttribute('aria-multiselectable')).toBe('true');
      expect(list.getAttribute('aria-label')).toBe('Members');
      expect(selectedTexts(fixture)).toEqual(['Ada Lovelace', 'Project owner']);

      key(fixture, 'ArrowDown');
      key(fixture, 'Enter');
      expect(fixture.componentInstance.control.value).toEqual(['owner', 'ada', 'grace']);
      expect(field(fixture).getAttribute('aria-expanded')).toBe('true');
      expect(chips(fixture)).toEqual(['Project owner', 'Ada Lovelace', 'Grace Hopper']);
      expect(live(fixture)).toBe('Grace Hopper added.');

      key(fixture, 'Home');
      key(fixture, 'Enter');
      expect(fixture.componentInstance.control.value).toEqual(['owner', 'grace']);
      expect(live(fixture)).toBe('Ada Lovelace removed.');

      // A disabled option is skipped by the keys and cannot be toggled.
      key(fixture, 'End');
      const active = field(fixture).getAttribute('aria-activedescendant');
      expect(element(fixture).querySelector(`#${active} .nh-select-label`)?.textContent).toBe('Edsger Dijkstra');
    });

    it('starts a new search after a choice and filters on label and description', () => {
      field(fixture).click();
      type(fixture, 'compiler');
      expect(optionTexts(fixture)).toEqual(['Grace Hopper']);
      key(fixture, 'Enter');
      expect(field(fixture).value).toBe('');
      expect(optionTexts(fixture)).toEqual(['Ada Lovelace', 'Grace Hopper', 'Edsger Dijkstra', 'Project owner']);
    });

    it('removes the last removable chip with Backspace in an empty search field, never while text is typed', () => {
      type(fixture, 'x');
      key(fixture, 'Backspace');
      expect(fixture.componentInstance.control.value).toEqual(['owner', 'ada']);

      type(fixture, '');
      key(fixture, 'Backspace');
      expect(fixture.componentInstance.control.value).toEqual(['owner']);
      expect(live(fixture)).toBe('Ada Lovelace removed.');

      // The locked chip stays, also when Backspace is held down.
      key(fixture, 'Backspace');
      key(fixture, 'Backspace', { repeat: true });
      expect(fixture.componentInstance.control.value).toEqual(['owner']);
    });

    it('removes a chip with its button and keeps the focus in the field', () => {
      const remove = element(fixture).querySelector('[data-nh-select-chip-remove]') as HTMLButtonElement;
      expect(remove.tabIndex).toBe(-1);
      remove.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true, button: 0 }));
      remove.click();
      fixture.detectChanges();
      expect(fixture.componentInstance.control.value).toEqual(['owner']);
      expect(document.activeElement).toBe(field(fixture));
      expect(fixture.componentInstance.control.dirty).toBeTrue();
    });

    it('never reports a value the form writes back, reports touched on blur and follows the disabled state', () => {
      const values: string[][] = [];
      fixture.componentInstance.control.valueChanges.subscribe(value => values.push(value));
      fixture.componentInstance.control.setValue(['edsger']);
      fixture.componentInstance.control.reset();
      fixture.detectChanges();
      expect(values).toEqual([['edsger'], ['owner', 'ada']]);
      expect(fixture.componentInstance.control.pristine).toBeTrue();
      expect(chips(fixture)).toEqual(['Project owner', 'Ada Lovelace']);

      field(fixture).dispatchEvent(new FocusEvent('blur'));
      expect(fixture.componentInstance.control.touched).toBeTrue();

      fixture.componentInstance.control.disable();
      fixture.detectChanges();
      expect(field(fixture).disabled).toBeTrue();
      expect(element(fixture).querySelector('[data-nh-select-chip-remove]')).toBeNull();
      key(fixture, 'Backspace');
      expect(fixture.componentInstance.control.value).toEqual(['owner', 'ada']);
    });

    it('updates inside an OnPush host when the options change', () => {
      fixture.componentInstance.options.set([...members, { value: 'barbara', label: 'Barbara Liskov' }]);
      fixture.detectChanges();
      key(fixture, 'ArrowDown');
      expect(optionTexts(fixture)).toContain('Barbara Liskov');
    });
  });

  describe('with primitive values', () => {
    @Component({
      standalone: true,
      imports: [NhMultiselectComponent],
      template: `<nh-multiselect ariaLabel="Flags" [options]="options" [value]="value()" (valueChange)="value.set($event)" />`
    })
    class PrimitiveHost {
      readonly options: NhSelectOption<number | boolean>[] = [
        { value: 0, label: 'Zero' },
        { value: false, label: 'False' },
        { value: 1, label: 'One' }
      ];
      readonly value = signal<(number | boolean)[]>([0]);
    }

    it('keeps 0 and false as values', () => {
      const fixture = TestBed.createComponent(PrimitiveHost);
      fixture.detectChanges();
      expect(chips(fixture)).toEqual(['Zero']);
      key(fixture, 'ArrowDown');
      key(fixture, 'ArrowDown');
      key(fixture, 'Enter');
      expect(fixture.componentInstance.value()).toEqual([0, false]);
      expect(chips(fixture)).toEqual(['Zero', 'False']);
      key(fixture, 'Home');
      key(fixture, 'Enter');
      expect(fixture.componentInstance.value()).toEqual([false]);
    });
  });

  describe('with a server search and create', () => {
    @Component({
      standalone: true,
      imports: [NhMultiselectComponent],
      template: `
        <nh-multiselect
          ariaLabel="Tags"
          [search]="search"
          [create]="create"
          [selectedOptions]="known"
          [value]="value()"
          (valueChange)="value.set($event)"
          (optionsChange)="options = $event"
          (optionCreated)="created.push($event)" />
      `
    })
    class SearchHost {
      readonly known: NhSelectOption<string>[] = [{ value: 'tag-1', label: 'Backend' }];
      readonly value = signal<string[]>(['tag-1']);
      options: NhSelectOption<string>[] = [];
      readonly created: NhSelectOption<string>[] = [];
      readonly answers: Observable<readonly NhSelectOption<string>[]>[] = [];
      readonly queries: string[] = [];
      readonly search = (query: string) => {
        this.queries.push(query);
        return this.answers.shift() ?? of([]);
      };
      // "BE" names the existing Backend tag, as a server that finds a duplicate would answer.
      readonly create = (text: string) => of(text === 'BE' ? { value: 'tag-1', label: 'Backend' } : { value: `tag-${text}`, label: text });
    }

    let fixture: ComponentFixture<SearchHost>;

    beforeEach(() => {
      fixture = TestBed.createComponent(SearchHost);
      fixture.detectChanges();
    });

    it('shows a stored value by its known option and adds a search result', fakeAsync(() => {
      expect(chips(fixture)).toEqual(['Backend']);
      fixture.componentInstance.answers.push(of([{ value: 'tag-2', label: 'Frontend' }, { value: 'tag-1', label: 'Backend' }]));
      field(fixture).click();
      type(fixture, 'end');
      tick(250);
      fixture.detectChanges();
      // Text that equals no label also offers to create it.
      expect(optionTexts(fixture)).toEqual(['Frontend', 'Backend', 'Create “end”']);
      expect(selectedTexts(fixture)).toEqual(['Backend']);

      key(fixture, 'Enter');
      expect(fixture.componentInstance.value()).toEqual(['tag-1', 'tag-2']);
      expect(fixture.componentInstance.options.map(option => option.label)).toEqual(['Backend', 'Frontend']);
      // The choice starts a new search, which waits for enough characters.
      expect(element(fixture).querySelector('.nh-select-state')?.textContent).toBe('Type at least 2 characters.');
    }));

    it('creates typed text that matches no result as a new chip and never removes an existing one', fakeAsync(() => {
      fixture.componentInstance.answers.push(of([]));
      field(fixture).click();
      type(fixture, 'Docs');
      tick(250);
      fixture.detectChanges();
      expect(optionTexts(fixture)).toEqual(['Create “Docs”']);
      key(fixture, 'Enter');
      expect(fixture.componentInstance.created).toEqual([{ value: 'tag-Docs', label: 'Docs' }]);
      expect(fixture.componentInstance.value()).toEqual(['tag-1', 'tag-Docs']);
      expect(chips(fixture)).toEqual(['Backend', 'Docs']);

      // Text that names a chosen option offers no create row.
      fixture.componentInstance.answers.push(of([]));
      type(fixture, 'Backend ');
      tick(250);
      fixture.detectChanges();
      expect(optionTexts(fixture)).toEqual([]);

      // A create that answers an option that is already chosen keeps it.
      fixture.componentInstance.answers.push(of([]));
      type(fixture, 'BE');
      tick(250);
      fixture.detectChanges();
      key(fixture, 'Enter');
      expect(fixture.componentInstance.created.at(-1)).toEqual({ value: 'tag-1', label: 'Backend' });
      expect(fixture.componentInstance.value()).toEqual(['tag-1', 'tag-Docs']);
    }));

    it('says why a search failed and searches again', fakeAsync(() => {
      fixture.componentInstance.answers.push(throwError(() => new Error('offline')), of([{ value: 'tag-3', label: 'Design' }]));
      field(fixture).click();
      type(fixture, 'des');
      tick(250);
      fixture.detectChanges();
      expect(element(fixture).querySelector('[data-nh-select-error]')?.textContent).toContain('The search failed.');
      expect(optionTexts(fixture)).toEqual([]);

      (element(fixture).querySelector('[data-nh-select-retry]') as HTMLButtonElement).click();
      fixture.detectChanges();
      expect(fixture.componentInstance.queries).toEqual(['des', 'des']);
      expect(optionTexts(fixture)).toEqual(['Design', 'Create “des”']);
    }));
  });
});
