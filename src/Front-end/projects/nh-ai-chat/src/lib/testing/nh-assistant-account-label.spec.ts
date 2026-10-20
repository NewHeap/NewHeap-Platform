import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { firstValueFrom, of, throwError } from 'rxjs';
import { NH_ASSISTANT_TRANSLATIONS } from '../i18n/nh-assistant-translations';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAdminApiService } from '../services/nh-assistant-admin-api.service';
import { NhAssistantApiError } from '../services/nh-assistant-transport';
import {
  NhAssistantAccountLabelComponent,
  NhAssistantAccountNames
} from '../../../admin/src/nh-assistant-account-names';

/**
 * Accounts in the administration: the name only, with the account ID as its tooltip and a button
 * that copies it; the ID itself, labeled as an account ID, only while no name is known.
 */
@Component({
  standalone: true,
  imports: [NhAssistantAccountLabelComponent],
  template: `<nh-assistant-account-label [actorId]="actorId()" [name]="name()" />`
})
class AccountLabelHostComponent {
  readonly actorId = signal('0f3c2a6e-6b1d-4d7a-9b51-3c1f0e2d4a77');
  readonly name = signal<string | null>('Operations');
}

@Component({ standalone: true, template: '' })
class AccountNamesHostComponent {
  readonly names = new NhAssistantAccountNames();
}

describe('Accounts in the administration', () => {
  let admin: jasmine.SpyObj<NhAssistantAdminApiService>;

  beforeEach(async () => {
    admin = jasmine.createSpyObj<NhAssistantAdminApiService>('NhAssistantAdminApiService', ['getAccounts']);
    TestBed.configureTestingModule({
      providers: [
        provideTranslateService({ fallbackLang: 'en' }),
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
        { provide: NhAssistantAdminApiService, useValue: admin }
      ]
    });
    const translate = TestBed.inject(TranslateService);
    translate.setTranslation('en', NH_ASSISTANT_TRANSLATIONS.en, true);
    await firstValueFrom(translate.use('en'));
  });

  function text(element: Element | null | undefined): string {
    return element?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  }

  function render(): { fixture: ComponentFixture<AccountLabelHostComponent>; element: HTMLElement } {
    const fixture = TestBed.createComponent(AccountLabelHostComponent);
    fixture.detectChanges();
    return { fixture, element: fixture.nativeElement as HTMLElement };
  }

  it('shows a known account by its name only, with the account ID as its tooltip', () => {
    const { element } = render();

    expect(text(element)).toBe('Operations');
    expect(text(element)).not.toContain('0f3c2a6e');
    const name = element.querySelector('[data-account-label-name]') as HTMLElement;
    expect(name.getAttribute('title')).toBe('Account ID: 0f3c2a6e-6b1d-4d7a-9b51-3c1f0e2d4a77');
    const copy = element.querySelector('[data-account-copy-id]') as HTMLButtonElement;
    expect(copy.type).toBe('button');
    expect(copy.getAttribute('aria-label')).toBe('Copy the account ID of Operations');
  });

  it('copies the account ID and says so', async () => {
    const writeText = spyOn(navigator.clipboard, 'writeText').and.returnValue(Promise.resolve());
    const { fixture, element } = render();

    (element.querySelector('[data-account-copy-id]') as HTMLButtonElement).click();
    await new Promise(resolve => setTimeout(resolve));
    fixture.detectChanges();

    expect(writeText).toHaveBeenCalledOnceWith('0f3c2a6e-6b1d-4d7a-9b51-3c1f0e2d4a77');
    expect(text(element.querySelector('[role="status"]'))).toBe('Account ID copied');
    expect(element.querySelector('[data-account-label-revealed]')).toBeNull();
  });

  it('shows the account ID to copy by hand when the browser refuses the clipboard', async () => {
    spyOn(navigator.clipboard, 'writeText').and.returnValue(Promise.reject(new Error('Denied.')));
    const { fixture, element } = render();

    (element.querySelector('[data-account-copy-id]') as HTMLButtonElement).click();
    await new Promise(resolve => setTimeout(resolve));
    fixture.detectChanges();

    expect(text(element.querySelector('[data-account-label-revealed]'))).toBe('Account ID 0f3c2a6e-6b1d-4d7a-9b51-3c1f0e2d4a77');
    expect(text(element.querySelector('[role="status"]'))).toBe('');
  });

  it('shows the account ID, clearly labeled, only while no name is known', () => {
    const { fixture, element } = render();
    fixture.componentInstance.name.set(null);
    fixture.detectChanges();

    expect(text(element)).toBe('Account ID 0f3c2a6e-6b1d-4d7a-9b51-3c1f0e2d4a77');
    expect(element.querySelector('[data-account-label-id]')).not.toBeNull();
    expect(element.querySelector('[data-account-copy-id]')).toBeNull();
    expect(element.querySelector('[data-account-label-name]')).toBeNull();
  });

  it('names accounts in sentences and labels the ID of an account without a name', () => {
    admin.getAccounts.and.returnValue(of([{ actorId: 'service-1', displayName: 'Operations account', detail: null }]));
    const names = TestBed.createComponent(AccountNamesHostComponent).componentInstance.names;

    names.load(['service-1', 'removed-account', null, 'service-1']);

    expect(admin.getAccounts).toHaveBeenCalledOnceWith(['service-1', 'removed-account']);
    expect(names.nameOf('service-1')).toBe('Operations account');
    expect(names.idTitleOf('service-1')).toBe('Account ID: service-1');
    expect(names.nameOf('removed-account')).toBe('account ID removed-account');
    expect(names.idTitleOf('removed-account')).toBeNull();
    expect(names.nameOf(null)).toBe('');
  });

  it('labels the IDs while the names cannot be read', () => {
    admin.getAccounts.and.returnValue(throwError(() => new NhAssistantApiError(503, 'assistant-unavailable', 'nh-assistant.errors.unavailable')));
    const names = TestBed.createComponent(AccountNamesHostComponent).componentInstance.names;

    names.load(['service-1']);

    expect(names.error()?.code).toBe('assistant-unavailable');
    expect(names.nameOf('service-1')).toBe('account ID service-1');
  });
});
