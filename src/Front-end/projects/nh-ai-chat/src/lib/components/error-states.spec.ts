import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { Observable, firstValueFrom, of, throwError } from 'rxjs';
import { NhAssistantAccountPickerComponent } from '../../../admin/src/agents/run-config/nh-assistant-account-picker.component';
import { NH_ASSISTANT_ADMIN_TRANSLATIONS } from '../../../admin/src/i18n/nh-assistant-admin-translations';
import { NhAssistantSubjectPickerComponent } from '../../../agent-runs/src/internal/nh-assistant-subject-picker.component';
import { NH_ASSISTANT_TRANSLATIONS } from '../i18n/nh-assistant-translations';
import { NhAssistantPickerComponent, NhAssistantPickerOption } from '../internal/nh-assistant-picker.component';
import { AgentSubjectWork } from '../models/assistant-agent-run.models';
import { AgentSubjectType, AssistantAccount } from '../models/assistant-admin.models';
import { ConnectionRequiredPart } from '../models/assistant-api.models';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAdminApiService } from '../services/nh-assistant-admin-api.service';
import { NhAssistantAgentRunApiService } from '../services/nh-assistant-agent-run-api.service';
import { NhAssistantMcpConnectService } from '../services/nh-assistant-mcp-connect.service';
import { NhAssistantApiError } from '../services/nh-assistant-transport';
import { NhAssistantConnectionRequiredComponent } from './connection-required/nh-assistant-connection-required.component';

/**
 * A failure never reads as an empty answer: a failed search says why and searches again, a failed
 * lookup of an account or an object names its code instead of "unknown", with the generic text as
 * fallback, and the card of a connection shows how a redirect-mode connection of its server ended.
 */
function flush(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve));
}

async function settle(fixture: ComponentFixture<unknown>, rounds = 4): Promise<HTMLElement> {
  for (let round = 0; round < rounds; round++) {
    fixture.detectChanges();
    await flush();
  }
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

function apiError(status: number, code: string): NhAssistantApiError {
  return new NhAssistantApiError(status, code, `nh-assistant.errors.${code}`);
}

async function useEnglish(): Promise<void> {
  const translate = TestBed.inject(TranslateService);
  translate.setTranslation('en', NH_ASSISTANT_TRANSLATIONS.en, true);
  translate.setTranslation('en', NH_ASSISTANT_ADMIN_TRANSLATIONS.en, true);
  await firstValueFrom(translate.use('en'));
}

function text(element: Element | null | undefined): string {
  return element?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

@Component({
  standalone: true,
  imports: [NhAssistantPickerComponent],
  template: `<div style="width: 375px"><nh-assistant-picker inputId="accounts" [search]="search" describedBy="accounts-hint" /></div>`
})
class SearchHost {
  readonly answers: Observable<NhAssistantPickerOption[]>[] = [];
  readonly queries: string[] = [];
  readonly search = (query: string) => {
    this.queries.push(query);
    return this.answers.shift() ?? of([]);
  };
}

describe('A picker whose search fails', () => {
  it('says why with the code, never "nothing found", and searches again', async () => {
    TestBed.configureTestingModule({ providers: [provideTranslateService({ fallbackLang: 'en' })] });
    await useEnglish();
    const fixture = TestBed.createComponent(SearchHost);
    const host = fixture.componentInstance;
    host.answers.push(
      throwError(() => apiError(0, 'assistant-network')),
      of([{ value: 'service-1', label: 'Operations account' }]));
    const element = await settle(fixture);
    const input = element.querySelector('#accounts') as HTMLInputElement;

    input.value = 'oper';
    input.dispatchEvent(new Event('input'));
    await new Promise(resolve => setTimeout(resolve, 300));
    await settle(fixture);

    const error = element.querySelector('[data-search-error]') as HTMLElement;
    expect(text(error)).toContain('The assistant cannot be reached.');
    expect(error.getAttribute('role')).toBe('alert');
    expect(text(element.querySelector('[role="listbox"]'))).toBe('The search failed.');
    expect(text(element.querySelector('[role="listbox"]'))).not.toContain('Nothing found');
    expect(input.getAttribute('aria-describedby')).toBe(`accounts-hint ${error.id}`);
    // At 375px the reason and its action wrap instead of scrolling sideways.
    expect(error.scrollWidth).toBeLessThanOrEqual(error.clientWidth + 1);

    (element.querySelector('[data-search-retry]') as HTMLButtonElement).click();
    await new Promise(resolve => setTimeout(resolve, 300));
    await settle(fixture);

    expect(host.queries).toEqual(['oper', 'oper']);
    expect(element.querySelector('[data-search-error]')).toBeNull();
    expect(text(element.querySelector('[role="option"]'))).toBe('Operations account');
    expect(input.getAttribute('aria-describedby')).toBe('accounts-hint');
  });
});

@Component({
  standalone: true,
  imports: [NhAssistantAccountPickerComponent],
  template: `<nh-assistant-account-picker inputId="account" [value]="value()" />`
})
class AccountHost {
  readonly value = signal<string | null>('service-1');
}

describe('The account picker whose lookup fails', () => {
  it('names the failure instead of an unknown account and looks the name up again', async () => {
    const answers: Observable<AssistantAccount[]>[] = [
      throwError(() => apiError(403, 'assistant-forbidden')),
      of([{ actorId: 'service-1', displayName: 'Operations account', detail: null }])
    ];
    const admin = { getAccounts: jasmine.createSpy('getAccounts').and.callFake(() => answers.shift()!) };
    TestBed.configureTestingModule({
      providers: [provideTranslateService({ fallbackLang: 'en' }), { provide: NhAssistantAdminApiService, useValue: admin }]
    });
    await useEnglish();
    const fixture = TestBed.createComponent(AccountHost);
    await settle(fixture);
    await new Promise(resolve => setTimeout(resolve, 350));
    const element = await settle(fixture);

    const error = element.querySelector('[data-account-lookup-error]') as HTMLElement;
    expect(text(error)).toContain(TestBed.inject(TranslateService).instant('nh-assistant.errors.assistant-forbidden'));
    expect(error.getAttribute('role')).toBe('alert');
    expect(text(element)).not.toContain('The application does not know this account.');
    expect((element.querySelector('#account') as HTMLInputElement).getAttribute('aria-describedby')).toBe(error.id);

    (element.querySelector('[data-account-lookup-retry]') as HTMLButtonElement).click();
    await settle(fixture);

    expect(admin.getAccounts).toHaveBeenCalledTimes(2);
    expect(element.querySelector('[data-account-lookup-error]')).toBeNull();
    expect(text(element)).toContain('Account: Operations account');
  });
});

const ticketType: AgentSubjectType = {
  id: 'ticket', name: 'Ticket', pluralName: 'Tickets', entityType: null, entityTypeFull: null, link: null, searchable: false, isSystem: false
};

@Component({
  standalone: true,
  imports: [NhAssistantSubjectPickerComponent],
  template: `<nh-assistant-subject-picker inputId="ticket" [subjectType]="type" />`
})
class SubjectHost {
  readonly type = ticketType;
}

describe('The subject picker whose lookup fails', () => {
  let answers: Observable<AgentSubjectWork>[];

  beforeEach(async () => {
    answers = [];
    const api = { getSubjectWork: jasmine.createSpy('getSubjectWork').and.callFake(() => answers.shift()!) };
    TestBed.configureTestingModule({
      providers: [provideTranslateService({ fallbackLang: 'en' }), { provide: NhAssistantAgentRunApiService, useValue: api }]
    });
    await useEnglish();
  });

  async function lookUp(fixture: ComponentFixture<SubjectHost>, id: string): Promise<HTMLElement> {
    const element = await settle(fixture);
    const input = element.querySelector('#ticket') as HTMLInputElement;
    input.value = id;
    input.dispatchEvent(new Event('input'));
    input.dispatchEvent(new Event('change'));
    return settle(fixture);
  }

  it('names an unknown object and offers no lookup again', async () => {
    answers.push(throwError(() => apiError(404, 'assistant-subject-not-found')));
    const element = await lookUp(TestBed.createComponent(SubjectHost), 'T-404');

    expect(text(element.querySelector('[data-lookup-error]'))).toBe('No object with this id was found, or you may not see it.');
    expect(element.querySelector('[data-lookup-retry]')).toBeNull();
    expect((element.querySelector('#ticket') as HTMLInputElement).getAttribute('aria-invalid')).toBe('true');
  });

  it('names an invalid reference with its code', async () => {
    answers.push(throwError(() => apiError(400, 'assistant-subject-invalid')));
    const element = await lookUp(TestBed.createComponent(SubjectHost), 'T 1');

    expect(text(element.querySelector('[data-lookup-error]'))).toBe('This is not a valid reference to an object. Check the kind of object and its id.');
    expect(element.querySelector('[data-lookup-retry]')).toBeNull();
  });

  it('falls back to the generic text for a code without one and looks up again', async () => {
    const work: AgentSubjectWork = { subjectType: 'ticket', subjectId: 'T-1', subjectDisplayName: 'Printer jam', role: 'reader', agents: [], openProposals: [] };
    answers.push(throwError(() => apiError(503, 'assistant-application-specific')), of(work));
    const fixture = TestBed.createComponent(SubjectHost);
    const element = await lookUp(fixture, 'T-1');

    expect(text(element.querySelector('[data-lookup-error]'))).toBe('Something went wrong. Please try again.');
    (element.querySelector('[data-lookup-retry]') as HTMLButtonElement).click();
    await settle(fixture);

    expect(element.querySelector('[data-lookup-error]')).toBeNull();
    expect(text(element)).toContain('Found: Printer jam');
  });
});

@Component({
  standalone: true,
  imports: [NhAssistantConnectionRequiredComponent],
  template: `
    <nh-assistant-connection-required [part]="calendar" />
    <nh-assistant-connection-required [part]="mail" />
  `
})
class CardsHost {
  readonly calendar: ConnectionRequiredPart = { type: 'connection-required', serverId: 'calendar', displayName: 'Team calendar', reason: 'not-connected' };
  readonly mail: ConnectionRequiredPart = { type: 'connection-required', serverId: 'mail', displayName: 'Mail', reason: 'not-connected' };
}

describe('The card of a connection after a redirect', () => {
  const script: NhAssistantMockScenario = { agents: [], turns: [] };

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideTranslateService({ fallbackLang: 'en' }),
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token', mcpConnectMode: 'redirect' }),
        provideNhAssistantMockApi(script)
      ]
    });
    await firstValueFrom(TestBed.inject(TranslateService).use('en'));
  });

  function cards(element: HTMLElement): HTMLElement[] {
    return Array.from(element.querySelectorAll<HTMLElement>('nh-assistant-connection-required'));
  }

  it('shows the connection of its own server as connected', async () => {
    const fixture = TestBed.createComponent(CardsHost);
    TestBed.inject(NhAssistantMcpConnectService).returned.set({
      completion: { serverId: 'calendar', ownerKind: 'user', status: 'active', grantedScopes: null },
      serverId: 'calendar'
    });
    const element = await settle(fixture);

    const [calendar, mail] = cards(element);
    expect(calendar.getAttribute('data-state')).toBe('connected');
    expect(text(calendar)).toContain('Team calendar is connected.');
    expect(mail.getAttribute('data-state')).toBe('idle');
  });

  it('shows a failed connection of its own server with the translated code', async () => {
    const fixture = TestBed.createComponent(CardsHost);
    TestBed.inject(NhAssistantMcpConnectService).returned.set({ code: 'assistant-mcp-authorization-failed', serverId: 'calendar' });
    const element = await settle(fixture);

    const [calendar, mail] = cards(element);
    expect(calendar.getAttribute('data-state')).toBe('failed');
    const error = calendar.querySelector('[data-connection-error]') as HTMLElement;
    expect(error.getAttribute('role')).toBe('alert');
    expect(text(error)).toContain(TestBed.inject(TranslateService).instant('nh-assistant.errors.assistant-mcp-authorization-failed'));
    expect(mail.querySelector('[data-connection-error]')).toBeNull();
  });
});
