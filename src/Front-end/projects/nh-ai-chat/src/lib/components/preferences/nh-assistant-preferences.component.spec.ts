import { OverlayContainer } from '@angular/cdk/overlay';
import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockBackend, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom, of, throwError } from 'rxjs';
import { NhAssistantConfig } from '../../nh-assistant.config';
import { provideNhAssistant } from '../../provide-nh-assistant';
import { NhAssistantApiError, NhAssistantApiService } from '../../services/nh-assistant-api.service';
import { NhAssistantPanelService } from '../../services/nh-assistant-panel.service';
import { NhAssistantStore } from '../../services/nh-assistant.store';
import { NhAssistantPanelComponent } from '../panel/nh-assistant-panel.component';
import { NhAssistantPreferencesComponent } from './nh-assistant-preferences.component';

function flush(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve));
}

async function configure(config: Partial<NhAssistantConfig> = {}, canAdminister = true): Promise<void> {
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      provideTranslateService({ fallbackLang: 'en' }),
      provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token', ...config }),
      provideNhAssistantMockApi({
        agents: [{ id: 'projects', version: 1, displayNameKey: 'agents.projects', descriptionKey: 'agents.projects', canMutate: true }],
        turns: [],
        preferences: { style: 'personal', addressForm: 'formal', responseLength: 'long', customInstructions: 'Use bullet points.' },
        admin: { canAdminister }
      })
    ]
  });
  await firstValueFrom(TestBed.inject(TranslateService).use('en'));
}

describe('NhAssistantPreferencesComponent', () => {
  let fixture: ComponentFixture<NhAssistantPreferencesComponent>;

  async function render(): Promise<HTMLElement> {
    fixture = TestBed.createComponent(NhAssistantPreferencesComponent);
    fixture.detectChanges();
    for (let attempt = 0; attempt < 20 && fixture.componentInstance.loading(); attempt++) {
      await flush();
    }
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  function radio(element: HTMLElement, name: string, value: string): HTMLInputElement {
    return element.querySelector(`input[type="radio"][name$="-${name}"][value="${value}"]`) as HTMLInputElement;
  }

  it('shows the stored preferences in labelled groups', async () => {
    await configure();
    const element = await render();

    expect(radio(element, 'style', 'personal').checked).toBeTrue();
    expect(radio(element, 'address', 'formal').checked).toBeTrue();
    expect(radio(element, 'length', 'long').checked).toBeTrue();
    expect((element.querySelector('textarea') as HTMLTextAreaElement).value).toBe('Use bullet points.');
    expect(Array.from(element.querySelectorAll('legend')).map(legend => legend.textContent?.trim()))
      .toEqual(['Style', 'Form of address', 'Answer length']);
    expect((element.querySelector('button.primary') as HTMLButtonElement).disabled).withContext('unchanged').toBeTrue();
  });

  it('saves the chosen values, trims the instructions and confirms', async () => {
    await configure();
    const element = await render();
    const saved: unknown[] = [];
    fixture.componentInstance.saved.subscribe(value => saved.push(value));

    radio(element, 'style', 'direct').click();
    radio(element, 'address', 'informal').click();
    const textarea = element.querySelector('textarea') as HTMLTextAreaElement;
    textarea.value = '  Answer in English.  ';
    textarea.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    (element.querySelector('button.primary') as HTMLButtonElement).click();
    for (let attempt = 0; attempt < 20 && fixture.componentInstance.saving(); attempt++) {
      await flush();
    }
    fixture.detectChanges();

    const request = TestBed.inject(NhAssistantMockBackend).requests.find(item => item.method === 'PUT');
    expect(request?.path).toBe('preferences');
    expect(request?.body).toEqual({ style: 'direct', addressForm: 'informal', responseLength: 'long', customInstructions: 'Answer in English.' });
    expect(saved.length).toBe(1);
    expect(element.querySelector('[role="status"]')?.textContent).toContain('Your preferences are saved.');
  });

  it('blocks instructions above 1,000 characters with a counter and message', async () => {
    await configure();
    const element = await render();

    const textarea = element.querySelector('textarea') as HTMLTextAreaElement;
    textarea.value = 'x'.repeat(1001);
    textarea.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    expect(element.textContent).toContain('Your instructions are too long.');
    expect(element.textContent).toContain('1001 of 1000 characters');
    expect(textarea.getAttribute('aria-invalid')).toBe('true');
    expect((element.querySelector('button.primary') as HTMLButtonElement).disabled).toBeTrue();
  });

  it('shows a translated error when saving fails', async () => {
    await configure();
    const api = TestBed.inject(NhAssistantApiService);
    spyOn(api, 'updatePreferences').and.returnValue(throwError(() =>
      new NhAssistantApiError(400, 'assistant-validation', 'nh-assistant.errors.assistant-validation')));
    const element = await render();

    radio(element, 'style', 'direct').click();
    fixture.detectChanges();
    (element.querySelector('button.primary') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(element.querySelector('[role="alert"]')?.textContent).toContain('Some values are not valid.');
    expect((element.querySelector('button.primary') as HTMLButtonElement).disabled).toBeFalse();
  });

  it('offers a retry when loading fails', async () => {
    await configure();
    const api = TestBed.inject(NhAssistantApiService);
    const load = spyOn(api, 'getPreferences').and.returnValues(
      throwError(() => new NhAssistantApiError(0, 'assistant-network', 'nh-assistant.errors.assistant-network')),
      of({ style: 'default', addressForm: 'informal', responseLength: 'normal', customInstructions: null })
    );
    const element = await render();

    expect(element.textContent).toContain('Your preferences could not be loaded.');
    (element.querySelector('.state button') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(load).toHaveBeenCalledTimes(2);
    expect(radio(element, 'style', 'default').checked).toBeTrue();
  });
});

describe('NhAssistantPanelComponent preferences and admin link', () => {
  @Component({ standalone: true, imports: [NhAssistantPanelComponent], template: '<nh-assistant-panel />' })
  class HostComponent {}

  let fixture: ComponentFixture<HostComponent>;

  async function open(): Promise<HTMLElement> {
    fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    TestBed.inject(NhAssistantPanelService).open();
    await TestBed.inject(NhAssistantStore).initialize();
    await flush();
    fixture.detectChanges();
    return TestBed.inject(OverlayContainer).getContainerElement();
  }

  it('opens the preferences from the header', async () => {
    await configure();
    const overlay = await open();

    const button = overlay.querySelector('button[aria-label="Your assistant preferences"]') as HTMLButtonElement;
    button.click();
    fixture.detectChanges();
    await flush();
    fixture.detectChanges();

    expect(button.getAttribute('aria-expanded')).toBe('true');
    expect(overlay.querySelector('nh-assistant-preferences')?.textContent).toContain('Your preferences');
  });

  it('links to the admin route only with canAdminister and a configured route', async () => {
    await configure({ adminRoute: '/admin/assistant' });
    const overlay = await open();

    const link = overlay.querySelector('a[aria-label="Assistant administration"]');
    expect(link?.getAttribute('href')).toBe('/admin/assistant');
  });

  it('hides the admin link for users who cannot administer', async () => {
    await configure({ adminRoute: '/admin/assistant' }, false);
    const overlay = await open();

    expect(overlay.querySelector('a[aria-label="Assistant administration"]')).toBeNull();
  });

  it('hides the admin link without a configured route', async () => {
    await configure();
    const overlay = await open();

    expect(overlay.querySelector('a[aria-label="Assistant administration"]')).toBeNull();
  });
});
