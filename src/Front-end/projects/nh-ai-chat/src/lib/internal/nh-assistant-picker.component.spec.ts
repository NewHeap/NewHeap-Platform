import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { firstValueFrom, of } from 'rxjs';
import { NH_ASSISTANT_TRANSLATIONS } from '../i18n/nh-assistant-translations';
import { NhAssistantPickerComponent, NhAssistantPickerOption } from './nh-assistant-picker.component';

@Component({
  standalone: true,
  imports: [NhAssistantPickerComponent],
  template: `
    <label for="rules">Rules</label>
    <nh-assistant-picker inputId="rules" [options]="options" [value]="value()" noneLabel="The model decides" (valueChange)="value.set($event)" />
    <label for="accounts">Account</label>
    <nh-assistant-picker inputId="accounts" [value]="account()" [search]="search" (valueChange)="account.set($event)" />
  `
})
class PickerHost {
  readonly options: NhAssistantPickerOption[] = [
    { value: 'project-kickoff', label: 'project-kickoff', description: 'Starts draft projects.' },
    { value: 'project-progress-note', label: 'project-progress-note', description: 'Notes progress.' }
  ];
  readonly value = signal('');
  readonly account = signal<string | null>(null);
  readonly queries: string[] = [];
  readonly search = (query: string) => {
    this.queries.push(query);
    return of([{ value: 'service-1', label: 'Operations account', description: 'operations@example.test' }]);
  };
}

describe('NhAssistantPickerComponent', () => {
  let fixture: ComponentFixture<PickerHost>;
  let element: HTMLElement;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideTranslateService({ fallbackLang: 'en' })] });
    const translate = TestBed.inject(TranslateService);
    translate.setTranslation('en', NH_ASSISTANT_TRANSLATIONS.en, true);
    await firstValueFrom(translate.use('en'));
    fixture = TestBed.createComponent(PickerHost);
    fixture.detectChanges();
    element = fixture.nativeElement as HTMLElement;
  });

  function field(id: string): HTMLInputElement {
    return element.querySelector(`#${id}`) as HTMLInputElement;
  }

  function key(input: HTMLInputElement, name: string): void {
    input.dispatchEvent(new KeyboardEvent('keydown', { key: name, bubbles: true, cancelable: true }));
    fixture.detectChanges();
  }

  it('is a labelled combobox whose list follows the arrow keys and chooses with Enter', () => {
    const input = field('rules');
    expect(input.getAttribute('role')).toBe('combobox');
    expect(input.getAttribute('aria-expanded')).toBe('false');
    expect(input.value).toBe('The model decides');

    key(input, 'ArrowDown');
    expect(input.getAttribute('aria-expanded')).toBe('true');
    const list = element.querySelector(`#${input.getAttribute('aria-controls')}`) as HTMLElement;
    expect(list.getAttribute('role')).toBe('listbox');
    expect(Array.from(list.querySelectorAll('[role="option"]')).map(option => option.textContent?.trim()))
      .toEqual(['The model decides', 'project-kickoffStarts draft projects.', 'project-progress-noteNotes progress.']);

    key(input, 'ArrowDown');
    const active = input.getAttribute('aria-activedescendant');
    expect(element.querySelector(`#${active}`)?.textContent).toContain('project-progress-note');
    key(input, 'Enter');
    expect(fixture.componentInstance.value()).toBe('project-progress-note');
    expect(input.getAttribute('aria-expanded')).toBe('false');
    expect(input.value).toBe('project-progress-note');
    expect(element.textContent).toContain('Notes progress.');
  });

  it('filters on name and description and keeps the value on Escape', () => {
    const input = field('rules');
    input.click();
    input.value = 'draft';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect(Array.from(element.querySelectorAll('[role="option"] .label')).map(label => label.textContent)).toEqual(['project-kickoff']);

    key(input, 'Escape');
    expect(input.getAttribute('aria-expanded')).toBe('false');
    expect(fixture.componentInstance.value()).toBe('');
    expect(input.value).toBe('The model decides');
  });

  it('searches the server after two characters and a short pause', async () => {
    const input = field('accounts');
    input.click();
    input.value = 'o';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect(element.textContent).toContain('Type at least two characters.');

    input.value = 'oper';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect(element.textContent).toContain('Searching…');
    await new Promise(resolve => setTimeout(resolve, 300));
    fixture.detectChanges();
    expect(fixture.componentInstance.queries).toEqual(['oper']);

    (element.querySelector('[role="option"]') as HTMLElement).click();
    fixture.detectChanges();
    expect(fixture.componentInstance.account()).toBe('service-1');
    expect(input.value).toBe('Operations account');
  });
});
