import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantAdminAlertKindsComponent } from '@newheap/platform-ai-chat/admin';
import { NhAssistantMockBackend, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom, of, throwError } from 'rxjs';
import { AdminAlertDelivery, AdminAlertKind, AdminAlertKindChoices, AdminAlertKinds } from '../models/assistant-admin.models';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAdminApiService } from '../services/nh-assistant-admin-api.service';
import { NhAssistantApiError } from '../services/nh-assistant-transport';

const everything: AdminAlertKindChoices = { switchOff: true, digestTimes: true, workdaysOnly: true, immediatelyFrom: true, quietHours: true };
const digest: AdminAlertDelivery = { mode: 'daily-digest', digestTimes: ['07:30'], workdaysOnly: true, immediatelyFrom: 'urgent', quietHours: null };
const immediately: AdminAlertDelivery = { mode: 'immediately', digestTimes: [], workdaysOnly: false, immediatelyFrom: null, quietHours: null };

function kind(id: string, change: Partial<AdminAlertKind> = {}): AdminAlertKind {
  return {
    id,
    displayNameKey: `tests.alert-kinds.${id}`,
    urgency: 'warning',
    required: false,
    administratorMayChange: everything,
    codeDelivery: digest,
    settings: { enabled: true, delivery: null, version: 0, changedBy: null, changedAt: null, reason: null },
    enabled: true,
    delivery: digest,
    numbers: { raised: 48, delivered: 41, read: 30, dismissed: 12, resolvedBeforeReading: 9, withheld: 7, muted: 5 },
    shadow: [],
    ...change
  };
}

const view: AdminAlertKinds = {
  from: '2026-10-03T00:00:00Z',
  to: '2026-11-02T00:00:00Z',
  kinds: [
    kind('invoice-overdue', {
      urgency: 'urgent',
      required: true,
      administratorMayChange: { ...everything, switchOff: false },
      codeDelivery: immediately,
      delivery: immediately,
      numbers: { raised: 4, delivered: 4, read: 3, dismissed: 1, resolvedBeforeReading: 0, withheld: 0, muted: 0 }
    }),
    kind('report-reminder', {
      urgency: 'info',
      administratorMayChange: { switchOff: false, digestTimes: false, workdaysOnly: false, immediatelyFrom: false, quietHours: false }
    }),
    kind('task-due-today', {
      shadow: [
        { agentId: 'deadline-watch', agentDisplayName: 'Deadline watch', wouldHaveSent: 17 },
        { agentId: 'removed-agent', agentDisplayName: null, wouldHaveSent: 2 }
      ]
    })
  ]
};

function flush(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve));
}

function text(element: Element | null): string {
  return element?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

describe('NhAssistantAdminAlertKindsComponent', () => {
  let admin: jasmine.SpyObj<NhAssistantAdminApiService>;

  async function render(kinds: AdminAlertKinds = view): Promise<{ fixture: ComponentFixture<NhAssistantAdminAlertKindsComponent>; element: HTMLElement }> {
    admin = jasmine.createSpyObj<NhAssistantAdminApiService>('NhAssistantAdminApiService', ['getAlertKinds', 'updateAlertKind']);
    admin.getAlertKinds.and.returnValue(of(structuredClone(kinds)));
    TestBed.configureTestingModule({
      providers: [
        provideTranslateService({ fallbackLang: 'en' }),
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
        { provide: NhAssistantAdminApiService, useValue: admin }
      ]
    });
    await firstValueFrom(TestBed.inject(TranslateService).use('en'));
    const fixture = TestBed.createComponent(NhAssistantAdminAlertKindsComponent);
    fixture.detectChanges();
    await flush();
    fixture.detectChanges();
    return { fixture, element: fixture.nativeElement as HTMLElement };
  }

  function row(element: HTMLElement, kindId: string): HTMLElement {
    return element.querySelector(`tbody[data-kind="${kindId}"]`) as HTMLElement;
  }

  async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
    fixture.detectChanges();
    await flush();
    fixture.detectChanges();
  }

  function setValue(input: HTMLInputElement | HTMLSelectElement, value: string, event = 'change'): void {
    input.value = value;
    input.dispatchEvent(new Event(event));
  }

  it('lists every kind with its delivery, the numbers of the last 30 days and what agents in shadow would have sent', async () => {
    const { element } = await render();

    const [filter] = admin.getAlertKinds.calls.mostRecent().args;
    expect(Date.parse(filter!.to!) - Date.parse(filter!.from!)).toBe(30 * 24 * 60 * 60 * 1000);
    expect(text(element.querySelector('[data-audience]'))).toContain('Only the assistant\'s administrators see this view.');
    expect(Array.from(element.querySelectorAll('tbody[data-kind]')).map(item => item.getAttribute('data-kind')))
      .toEqual(['invoice-overdue', 'report-reminder', 'task-due-today']);

    const tasks = row(element, 'task-due-today');
    // A kind without a translation of its name shows its id.
    expect(text(tasks.querySelector('.kind-name'))).toContain('task-due-today');
    expect(text(tasks.querySelector('[data-urgency]'))).toBe('Warning');
    expect(text(tasks.querySelector('[data-state]'))).toBe('On');
    expect(text(tasks.querySelector('[data-delivery]'))).toContain('In a digest at 07:30 on working days');
    expect(text(tasks.querySelector('[data-delivery]'))).toContain('Urgent and higher at once');
    expect(text(tasks.querySelector('[data-delivery]'))).toContain('As in code');
    expect(['raised', 'delivered', 'read', 'dismissed', 'resolved-before-reading'].map(name => text(tasks.querySelector(`[data-${name}]`))))
      .toEqual(['48', '41', '30', '12', '9']);
    expect(text(tasks.querySelector('[data-withheld]'))).toBe('7 5 muted');
    expect(Array.from(tasks.querySelectorAll('[data-shadow] li')).map(text)).toEqual([
      'Deadline watch: 17 would have been sent',
      'removed-agent: 2 would have been sent'
    ]);
    expect(text(row(element, 'invoice-overdue').querySelector('[data-delivery]'))).toContain('As soon as an alert is raised');
    expect(text(row(element, 'invoice-overdue').querySelector('[data-shadow]'))).toBe('None');
  });

  it('keeps the switch of a required kind and the controls the code keeps disabled, and says why', async () => {
    const { fixture, element } = await render();

    const invoice = row(element, 'invoice-overdue');
    expect(text(invoice.querySelector('[data-required]'))).toBe('Required');
    const invoiceSwitch = invoice.querySelector('[data-switch]') as HTMLButtonElement;
    expect(invoiceSwitch.disabled).toBeTrue();
    expect(text(invoice.querySelector('[data-switch-hint]'))).toBe('A required kind cannot be switched off.');
    expect(invoiceSwitch.getAttribute('aria-describedby')).toBe(invoice.querySelector('[data-switch-hint]')!.id);

    const report = row(element, 'report-reminder');
    expect((report.querySelector('[data-switch]') as HTMLButtonElement).disabled).toBeTrue();
    expect(text(report.querySelector('[data-switch-hint]'))).toBe('The code of this kind does not let administrators switch it off.');
    expect((report.querySelector('[data-edit]') as HTMLButtonElement).disabled).toBeTrue();
    expect(text(report.querySelector('[data-edit-hint]'))).toBe('The code of this kind does not let administrators change its delivery.');

    // A kind whose code lets the administrator change its quiet hours only.
    admin.getAlertKinds.and.returnValue(of({
      ...view,
      kinds: [kind('task-due-today', { administratorMayChange: { switchOff: true, digestTimes: false, workdaysOnly: false, immediatelyFrom: false, quietHours: true } })]
    }));
    (element.querySelector('.header-actions button') as HTMLButtonElement).click();
    await settle(fixture);
    (row(element, 'task-due-today').querySelector('[data-edit]') as HTMLButtonElement).click();
    fixture.detectChanges();
    const editor = element.querySelector('[data-editor]')!;
    expect((editor.querySelector('[data-mode]') as HTMLFieldSetElement).disabled).toBeTrue();
    expect((editor.querySelector('[data-digest-times]') as HTMLFieldSetElement).disabled).toBeTrue();
    expect((editor.querySelector('[data-workdays]') as HTMLInputElement).disabled).toBeTrue();
    expect((editor.querySelector('[data-immediately-from]') as HTMLSelectElement).disabled).toBeTrue();
    expect((editor.querySelector('[data-quiet-hours]') as HTMLFieldSetElement).disabled).toBeFalse();
    expect(text(editor.querySelector('[data-mode] .hint'))).toBe('The code of this kind does not let administrators change this.');
  });

  it('saves a delivery within the bounds with the version it was made on and keeps the numbers', async () => {
    const { fixture, element } = await render();
    admin.updateAlertKind.and.callFake((kindId, change) => of(kind(kindId, {
      settings: { enabled: change.enabled, delivery: change.delivery, version: 1, changedBy: 'admin-1', changedAt: '2026-11-02T08:00:00Z', reason: change.reason ?? null },
      delivery: change.delivery!,
      numbers: null,
      shadow: []
    })));

    (row(element, 'task-due-today').querySelector('[data-edit]') as HTMLButtonElement).click();
    fixture.detectChanges();
    const editor = () => element.querySelector('[data-editor]')!;
    (editor().querySelector('[data-add-time]') as HTMLButtonElement).click();
    fixture.detectChanges();
    const times = editor().querySelectorAll('[data-digest-times] input[type="time"]');
    expect(times.length).toBe(2);
    setValue(times[1] as HTMLInputElement, '13:00');
    fixture.detectChanges();
    setValue(editor().querySelector('[data-immediately-from]') as HTMLSelectElement, '');
    const quiet = editor().querySelector('[data-quiet-hours] input[type="checkbox"]') as HTMLInputElement;
    quiet.checked = true;
    quiet.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    setValue(editor().querySelector('input[id$="-reason"]') as HTMLInputElement, ' Lunch digest ', 'input');
    fixture.detectChanges();
    (editor().querySelector('[data-save]') as HTMLButtonElement).click();
    await settle(fixture);

    expect(admin.updateAlertKind).toHaveBeenCalledOnceWith('task-due-today', {
      enabled: true,
      delivery: { mode: 'daily-digest', digestTimes: ['07:30', '13:00'], workdaysOnly: true, immediatelyFrom: null, quietHours: { start: '22:00', end: '07:00' } },
      expectedVersion: 0,
      reason: 'Lunch digest'
    });
    const tasks = row(element, 'task-due-today');
    expect(element.querySelector('[data-editor]')).toBeNull();
    expect(text(tasks.querySelector('[data-delivery]'))).toContain('In a digest at 07:30, 13:00 on working days');
    expect(text(tasks.querySelector('[data-delivery]'))).toContain('Quiet from 22:00 to 07:00');
    expect(text(tasks.querySelector('[data-delivery]'))).toContain('Changed by admin-1');
    // The answer of a change has no numbers; the period's numbers stay.
    expect(text(tasks.querySelector('[data-raised]'))).toBe('48');
    expect(tasks.querySelectorAll('[data-shadow] li').length).toBe(2);
    expect(text(tasks.querySelector('[data-saved]'))).toContain('are saved.');
  });

  it('refuses digest times the server would refuse before it sends them', async () => {
    const { fixture, element } = await render();

    (row(element, 'task-due-today').querySelector('[data-edit]') as HTMLButtonElement).click();
    fixture.detectChanges();
    const editor = element.querySelector('[data-editor]')!;
    (editor.querySelector('[data-add-time]') as HTMLButtonElement).click();
    fixture.detectChanges();
    setValue(editor.querySelectorAll('[data-digest-times] input[type="time"]')[1] as HTMLInputElement, '07:30');
    fixture.detectChanges();

    expect(text(editor.querySelector('[data-problem]'))).toBe('Enter one to four different times, such as 07:30.');
    expect((editor.querySelector('[data-save]') as HTMLButtonElement).disabled).toBeTrue();
    expect(admin.updateAlertKind).not.toHaveBeenCalled();
  });

  it('switches a kind off and lets a kind follow its code again', async () => {
    const changed = kind('task-due-today', {
      settings: { enabled: true, delivery: { ...digest, digestTimes: ['08:00'] }, version: 3, changedBy: 'admin-1', changedAt: '2026-11-01T08:00:00Z', reason: null },
      delivery: { ...digest, digestTimes: ['08:00'] }
    });
    const { fixture, element } = await render({ ...view, kinds: [changed] });
    admin.updateAlertKind.and.callFake((kindId, change) => of(kind(kindId, {
      settings: { ...changed.settings, enabled: change.enabled, delivery: change.delivery, version: change.expectedVersion + 1 },
      enabled: change.enabled,
      delivery: change.delivery ?? digest,
      numbers: null
    })));

    (row(element, 'task-due-today').querySelector('[data-switch]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(admin.updateAlertKind.calls.mostRecent().args).toEqual([
      'task-due-today',
      { enabled: false, delivery: { ...digest, digestTimes: ['08:00'] }, expectedVersion: 3, reason: null }
    ]);
    expect(text(row(element, 'task-due-today').querySelector('[data-state]'))).toBe('Off');
    expect(text(row(element, 'task-due-today').querySelector('[data-switch]'))).toBe('Switch on');

    (row(element, 'task-due-today').querySelector('[data-edit]') as HTMLButtonElement).click();
    fixture.detectChanges();
    (element.querySelector('[data-reset]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(admin.updateAlertKind.calls.mostRecent().args).toEqual([
      'task-due-today',
      { enabled: false, delivery: null, expectedVersion: 4, reason: null }
    ]);
    expect(text(row(element, 'task-due-today').querySelector('[data-delivery]'))).toContain('In a digest at 07:30 on working days');
  });

  it('shows the text of a refused change, such as a stale version or a part the code keeps', async () => {
    const { fixture, element } = await render();
    admin.updateAlertKind.and.returnValue(throwError(() =>
      new NhAssistantApiError(409, 'assistant-version-conflict', 'nh-assistant.errors.assistant-version-conflict')));

    (row(element, 'task-due-today').querySelector('[data-switch]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(text(row(element, 'task-due-today').querySelector('[data-failure]')))
      .toBe('Someone else changed this in the meantime. Reload and apply your change again.');
    expect(row(element, 'task-due-today').querySelector('[data-failure]')!.getAttribute('role')).toBe('alert');

    admin.updateAlertKind.and.returnValue(throwError(() => new NhAssistantApiError(
      400,
      'assistant-alert-kind-delivery-not-allowed',
      'nh-assistant.errors.assistant-alert-kind-delivery-not-allowed',
      { 'delivery.quietHours': ['not-allowed'] })));
    (row(element, 'task-due-today').querySelector('[data-edit]') as HTMLButtonElement).click();
    fixture.detectChanges();
    (element.querySelector('[data-editor] [data-save]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(text(row(element, 'task-due-today').querySelector('[data-failure]')))
      .toBe('The code of this kind of alert does not let administrators change this part of its delivery.');
    // The editor stays open with the change, to correct it.
    expect(element.querySelector('[data-editor]')).not.toBeNull();
  });

  it('shows an error with a retry, and the empty state of an application without alert kinds', async () => {
    const { fixture, element } = await render();

    admin.getAlertKinds.and.returnValue(throwError(() => new NhAssistantApiError(403, 'assistant-forbidden', 'nh-assistant.errors.assistant-forbidden')));
    setValue(element.querySelector('select[id$="-period"]') as HTMLSelectElement, '7');
    fixture.detectChanges();
    const [filter] = admin.getAlertKinds.calls.mostRecent().args;
    expect(Date.parse(filter!.to!) - Date.parse(filter!.from!)).toBe(7 * 24 * 60 * 60 * 1000);
    expect(element.querySelector('[role="alert"]')).not.toBeNull();

    admin.getAlertKinds.and.returnValue(of({ ...view, kinds: [] }));
    (element.querySelector('[role="alert"] button') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(text(element.querySelector('.state'))).toBe('The application declares no kinds of alert.');
  });
});

describe('the mock API of the administration of alert kinds', () => {
  async function api(): Promise<NhAssistantAdminApiService> {
    TestBed.configureTestingModule({
      providers: [
        provideTranslateService({ fallbackLang: 'en' }),
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
        provideNhAssistantMockApi({ agents: [], turns: [] })
      ]
    });
    return TestBed.inject(NhAssistantAdminApiService);
  }

  async function refusal(promise: Promise<unknown>): Promise<NhAssistantApiError> {
    try {
      await promise;
    } catch (error) {
      return error as NhAssistantApiError;
    }
    throw new Error('The change was not refused.');
  }

  it('serves an example and changes a kind within the bounds of its code with the next version', async () => {
    const admin = await api();

    const all = await firstValueFrom(admin.getAlertKinds());
    expect(all.kinds.map(item => item.id)).toEqual(['invoice-overdue', 'report-reminder', 'task-due-today']);
    expect(all.kinds[2].numbers?.raised).toBe(48);
    expect(all.kinds[2].shadow[0].wouldHaveSent).toBe(17);

    const lunch: AdminAlertDelivery = { ...digest, digestTimes: ['13:00', '07:30'] };
    const changed = await firstValueFrom(admin.updateAlertKind('task-due-today', { enabled: false, delivery: lunch, expectedVersion: 0 }));
    expect(changed.settings.version).toBe(1);
    expect(changed.enabled).toBeFalse();
    expect(changed.delivery.digestTimes).toEqual(['07:30', '13:00']);
    expect(changed.numbers).toBeNull();

    // A delivery equal to the code's is kept as the code's.
    const back = await firstValueFrom(admin.updateAlertKind('task-due-today', { enabled: true, delivery: digest, expectedVersion: 1 }));
    expect(back.settings.delivery).toBeNull();
    expect(back.settings.version).toBe(2);
    expect(TestBed.inject(NhAssistantMockBackend).requests.some(item => item.method === 'PUT' && item.path === 'admin/alert-kinds/task-due-today')).toBeTrue();
  });

  it('refuses what the code does not allow, a malformed delivery and a stale version with the server codes', async () => {
    const admin = await api();

    const required = await refusal(firstValueFrom(admin.updateAlertKind('invoice-overdue', { enabled: false, delivery: null, expectedVersion: 0 })));
    expect([required.status, required.code]).toEqual([400, 'assistant-alert-kind-switch-off-not-allowed']);

    const kept = await refusal(firstValueFrom(admin.updateAlertKind('report-reminder', {
      enabled: true,
      delivery: { mode: 'daily-digest', digestTimes: ['16:00'], workdaysOnly: false, immediatelyFrom: null, quietHours: { start: '22:00', end: '07:00' } },
      expectedVersion: 0
    })));
    expect([kept.status, kept.code, kept.fieldErrors]).toEqual([400, 'assistant-alert-kind-delivery-not-allowed', { 'delivery.quietHours': ['not-allowed'] }]);

    const malformed = await refusal(firstValueFrom(admin.updateAlertKind('task-due-today', {
      enabled: true,
      delivery: { ...digest, digestTimes: ['07:30', '07:30'] },
      expectedVersion: 0
    })));
    expect([malformed.status, malformed.code]).toEqual([400, 'assistant-alert-kind-delivery-invalid']);

    const stale = await refusal(firstValueFrom(admin.updateAlertKind('task-due-today', { enabled: false, delivery: null, expectedVersion: 5 })));
    expect([stale.status, stale.code]).toEqual([409, 'assistant-version-conflict']);

    const unknown = await refusal(firstValueFrom(admin.updateAlertKind('no-such-kind', { enabled: true, delivery: null, expectedVersion: 0 })));
    expect([unknown.status, unknown.code]).toEqual([404, 'assistant-alert-kind-unknown']);
  });
});
