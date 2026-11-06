import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { NhComboboxComponent, NhSelectOption } from '@newheap/platform-select';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { Observable, firstValueFrom, of, throwError } from 'rxjs';
import { NH_ASSISTANT_TRANSLATIONS } from '../i18n/nh-assistant-translations';
import { NhAssistantApiError } from '../services/nh-assistant-transport';
import { provideNhAssistantSelect } from './nh-assistant-select';

@Component({
  standalone: true,
  imports: [NhComboboxComponent],
  providers: [provideNhAssistantSelect()],
  template: `
    <label for="rules">Rules</label>
    <nh-combobox inputId="rules" [options]="options" [value]="value()" noneLabel="The model decides" noneValue="" (valueChange)="value.set($event ?? '')" />
    <label for="accounts">Account</label>
    <nh-combobox inputId="accounts" [search]="search" />
  `
})
class SelectHost {
  readonly options: NhSelectOption<string>[] = [
    { value: 'project-kickoff', label: 'project-kickoff', description: 'Starts draft projects.' },
    { value: 'project-progress-note', label: 'project-progress-note', description: 'Notes progress.' }
  ];
  readonly value = signal('');
  readonly answers: Observable<NhSelectOption<string>[]>[] = [];
  readonly search = () => this.answers.shift() ?? of([]);
}

/**
 * The comboboxes of the administration speak the assistant's language: their texts come from the
 * `nh-assistant.picker` keys and follow a language change, and a failed search names the translated
 * text of its code, with the generic text as fallback.
 */
describe('The assistant texts of nh-combobox', () => {
  let fixture: ComponentFixture<SelectHost>;
  let translate: TranslateService;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideTranslateService({ fallbackLang: 'en' })] });
    translate = TestBed.inject(TranslateService);
    translate.setTranslation('en', NH_ASSISTANT_TRANSLATIONS.en, true);
    translate.setTranslation('nl', NH_ASSISTANT_TRANSLATIONS.nl, true);
    await firstValueFrom(translate.use('en'));
    fixture = TestBed.createComponent(SelectHost);
    fixture.detectChanges();
  });

  function field(id: string): HTMLInputElement {
    return (fixture.nativeElement as HTMLElement).querySelector(`#${id}`) as HTMLInputElement;
  }

  function type(input: HTMLInputElement, text: string): void {
    input.value = text;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  it('keeps the empty choice as an empty value and chooses with the keyboard', () => {
    const input = field('rules');
    expect(input.value).toBe('The model decides');
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true, cancelable: true }));
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'End', bubbles: true, cancelable: true }));
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }));
    fixture.detectChanges();
    expect(fixture.componentInstance.value()).toBe('project-progress-note');
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Notes progress.');
  });

  it('uses the assistant texts and follows a language change', async () => {
    const accounts = field('accounts');
    accounts.click();
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('.nh-select-state')?.textContent).toBe('Type at least two characters.');
    expect(element.querySelector('.nh-select-toggle')?.getAttribute('aria-label')).toBe('Show the choices');

    await firstValueFrom(translate.use('nl'));
    fixture.detectChanges();
    expect(element.querySelector('.nh-select-state')?.textContent).toBe('Typ minstens twee tekens.');
    expect(element.querySelector('.nh-select-toggle')?.getAttribute('aria-label')).toBe('Toon de keuzes');
  });

  it('names a failed search by the translated text of its code, with the generic text as fallback', fakeAsync(() => {
    const host = fixture.componentInstance;
    host.answers.push(
      throwError(() => new NhAssistantApiError(0, 'assistant-network', 'nh-assistant.errors.assistant-network')),
      throwError(() => new NhAssistantApiError(500, 'host-specific-failure', 'host.errors.unknown')));
    const accounts = field('accounts');
    accounts.click();
    type(accounts, 'oper');
    tick(250);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('[data-nh-select-error]')?.textContent).toContain(translate.instant('nh-assistant.errors.assistant-network'));
    expect(element.querySelector('[data-nh-select-retry]')?.textContent?.trim()).toBe('Search again');

    type(accounts, 'opera');
    tick(250);
    fixture.detectChanges();
    expect(element.querySelector('[data-nh-select-error]')?.textContent).toContain(translate.instant('nh-assistant.errors.generic'));
  }));
});
