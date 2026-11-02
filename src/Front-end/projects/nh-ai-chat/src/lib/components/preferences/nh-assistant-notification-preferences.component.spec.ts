import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { NotificationAlertKind } from '../../models/assistant-alert.models';
import { NotificationSettings } from '../../models/assistant-api.models';
import { provideNhAssistant } from '../../provide-nh-assistant';
import { NhAssistantNotificationPreferencesComponent } from './nh-assistant-notification-preferences.component';

function flush(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve));
}

const dueToday: NotificationAlertKind = {
  id: 'task-due-today',
  displayNameKey: 'tests.alert-kinds.task-due-today',
  urgency: 'warning',
  required: false,
  mayMute: true,
  mayChangeDigestTime: true,
  mayChooseChannels: true,
  muted: false,
  defaultDigestTime: '07:30',
  digestTime: null,
  workdaysOnly: true,
  channels: { push: true, inApp: true },
  availableChannels: ['push', 'in-app']
};

const escalated: NotificationAlertKind = {
  id: 'task-escalated',
  displayNameKey: 'tests.alert-kinds.task-escalated',
  urgency: 'urgent',
  required: true,
  mayMute: false,
  mayChangeDigestTime: false,
  mayChooseChannels: false,
  muted: false,
  defaultDigestTime: null,
  digestTime: null,
  workdaysOnly: false,
  channels: { push: true, inApp: true },
  availableChannels: ['push', 'in-app']
};

/** A kind of a server that takes several digest times and quiet hours (FB87 phase 2). */
const twice: NotificationAlertKind = {
  ...dueToday,
  id: 'task-twice',
  displayNameKey: 'tests.alert-kinds.task-twice',
  defaultDigestTimes: ['07:30', '16:00'],
  digestTimes: null,
  mayChangeQuietHours: true,
  defaultQuietHours: { start: '22:00', end: '07:00' },
  quietHours: null,
  urgentBreaksQuietHours: true
};

describe('NhAssistantNotificationPreferencesComponent', () => {
  let fixture: ComponentFixture<NhAssistantNotificationPreferencesComponent>;

  async function render(
    alerts: NhAssistantMockScenario['alerts'] | null = { kinds: [dueToday, escalated], timeZone: 'Europe/Amsterdam' }
  ): Promise<HTMLElement> {
    TestBed.configureTestingModule({
      providers: [
        provideTranslateService({ fallbackLang: 'en' }),
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
        provideNhAssistantMockApi({ agents: [], turns: [], alerts: alerts ?? undefined })
      ]
    });
    const translate = TestBed.inject(TranslateService);
    await firstValueFrom(translate.use('en'));
    translate.setTranslation('en', { tests: { 'alert-kinds': { 'task-due-today': 'Task due today', 'task-escalated': 'Task escalated', 'task-twice': 'Task twice' } } }, true);
    fixture = TestBed.createComponent(NhAssistantNotificationPreferencesComponent);
    fixture.detectChanges();
    await settle();
    return fixture.nativeElement as HTMLElement;
  }

  async function settle(): Promise<void> {
    for (let attempt = 0; attempt < 10; attempt++) {
      await flush();
      fixture.detectChanges();
    }
  }

  function kind(element: HTMLElement, id: string): HTMLElement {
    return element.querySelector(`[data-kind-id="${id}"]`) as HTMLElement;
  }

  function change(input: HTMLInputElement, value: string | boolean): void {
    if (typeof value === 'boolean') {
      input.checked = value;
    } else {
      input.value = value;
    }
    input.dispatchEvent(new Event('change'));
    fixture.detectChanges();
  }

  it('shows the kinds with their delivery and the person\'s time zone, and no choice a kind does not allow', async () => {
    const element = await render();

    expect(element.querySelector('[data-time-zone]')?.textContent?.trim()).toBe('Times are in your time zone, Europe/Amsterdam.');
    const due = kind(element, 'task-due-today');
    expect(due.querySelector('strong')?.textContent?.trim()).toBe('Task due today');
    expect(due.querySelector('[data-kind-delivery]')?.textContent?.trim()).toBe('Delivered in the daily overview at 07:30 on working days.');
    expect((due.querySelector('[data-kind-received]') as HTMLInputElement).checked).toBeTrue();
    expect((due.querySelector('[data-kind-digest-time]') as HTMLInputElement).value).withContext('the default shows').toBe('07:30');
    expect(due.querySelector('[data-kind-reset]')).withContext('nothing to reset').toBeNull();
    expect(due.querySelectorAll('[data-kind-channel]').length).toBe(2);
    // A required kind is always received and offers no choices.
    const required = kind(element, 'task-escalated');
    expect(required.querySelector('[data-kind-required]')?.textContent?.trim()).toBe('Required');
    expect(required.querySelector('[data-kind-received]')).toBeNull();
    expect(required.querySelector('[data-kind-digest-time]')).toBeNull();
    expect(required.querySelector('[data-kind-channel]')).toBeNull();
    expect(required.querySelector('[data-kind-delivery]')?.textContent?.trim()).toBe('Delivered as soon as they are raised.');
    expect((element.querySelector('[data-save-notifications]') as HTMLButtonElement).disabled).withContext('unchanged').toBeTrue();
  });

  it('saves only what the person changed and shows the stored choices', async () => {
    const element = await render();
    const saved: NotificationSettings[] = [];
    fixture.componentInstance.saved.subscribe(settings => saved.push(settings));
    const due = kind(element, 'task-due-today');

    change(due.querySelector('[data-kind-digest-time]') as HTMLInputElement, '08:15');
    change(due.querySelector('[data-kind-channel="push"]') as HTMLInputElement, false);
    expect(due.querySelector('[data-kind-delivery]')?.textContent?.trim()).toBe('Delivered in the daily overview at 08:15 on working days.');
    (element.querySelector('[data-save-notifications]') as HTMLButtonElement).click();
    await settle();

    const put = TestBed.inject(NhAssistantMockBackend).requests.filter(request => request.method === 'PUT' && request.path === 'notifications');
    expect(put.map(request => request.body)).toEqual([{ alertKinds: [{ id: 'task-due-today', digestTime: '08:15', channels: { push: false } }] }]);
    expect(element.querySelector('.message.success')?.textContent?.trim()).toBe('Your notification choices are saved.');
    expect(saved[0].alertKinds?.[0]).toEqual(jasmine.objectContaining({ digestTime: '08:15', channels: { push: false, inApp: true } }));
    expect((element.querySelector('[data-save-notifications]') as HTMLButtonElement).disabled).withContext('saved').toBeTrue();

    // Back to the kind's default, and a muted kind's other choices are disabled.
    (kind(element, 'task-due-today').querySelector('[data-kind-reset]') as HTMLButtonElement).click();
    change(kind(element, 'task-due-today').querySelector('[data-kind-received]') as HTMLInputElement, false);
    expect((kind(element, 'task-due-today').querySelector('[data-kind-digest-time]') as HTMLInputElement).disabled).toBeTrue();
    (element.querySelector('[data-save-notifications]') as HTMLButtonElement).click();
    await settle();
    expect(TestBed.inject(NhAssistantMockBackend).requests.filter(request => request.method === 'PUT').pop()?.body)
      .toEqual({ alertKinds: [{ id: 'task-due-today', muted: true, digestTime: null }] });
  });

  it('refuses a time that is no time of day, and shows a refused change', async () => {
    const element = await render({ kinds: [{ ...dueToday, mayChooseChannels: false }], timeZone: 'Europe/Amsterdam' });
    const time = kind(element, 'task-due-today').querySelector('[data-kind-digest-time]') as HTMLInputElement;

    // A browser without a time field lets the person type anything.
    fixture.componentInstance.setDigestTime(dueToday, { target: { value: '25:00' } } as unknown as Event);
    fixture.detectChanges();
    expect(time.getAttribute('aria-invalid')).toBe('true');
    expect(kind(element, 'task-due-today').textContent).toContain('Enter a time such as 07:30.');
    expect((element.querySelector('[data-save-notifications]') as HTMLButtonElement).disabled).toBeTrue();

    change(time, '06:45');
    // The server's kind no longer lets the person choose a time: the save is refused with its code.
    TestBed.inject(NhAssistantMockBackend).setAlertKind({ ...dueToday, mayChangeDigestTime: false });
    (element.querySelector('[data-save-notifications]') as HTMLButtonElement).click();
    await settle();
    expect(element.querySelector('.message.error')?.getAttribute('role')).toBe('alert');
    expect(kind(element, 'task-due-today').textContent?.replace(/\s+/g, ' ')).toContain('Channels: Desktop notifications, Notifications in the application');
  });

  it('edits up to four digest times and quiet hours of a kind, and sends them as a list', async () => {
    const element = await render({ kinds: [twice], timeZone: 'Europe/Amsterdam' });
    const backend = TestBed.inject(NhAssistantMockBackend);
    const item = () => kind(element, 'task-twice');
    const times = () => Array.from(item().querySelectorAll('[data-kind-digest-time]')) as HTMLInputElement[];
    expect(item().querySelector('[data-kind-delivery]')?.textContent?.trim()).toBe('Delivered in the daily overviews at 07:30, 16:00 on working days.');
    expect(times().map(input => input.value)).toEqual(['07:30', '16:00']);
    expect(item().textContent).toContain('Default: 07:30, 16:00');
    expect((item().querySelector('[data-kind-quiet-start]') as HTMLInputElement).value).toBe('22:00');
    expect((item().querySelector('[data-kind-quiet-end]') as HTMLInputElement).value).toBe('07:00');
    expect(item().querySelector('[data-kind-urgent-through]')?.textContent?.trim()).toBe('Urgent alerts still reach you.');

    // A third time an hour after the last, the second moved to noon, and quiet hours from 21:00.
    (item().querySelector('[data-kind-add-time]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(times().map(input => input.value)).toEqual(['07:30', '16:00', '17:00']);
    change(times()[1], '12:00');
    change(item().querySelector('[data-kind-quiet-start]') as HTMLInputElement, '21:00');
    expect(item().querySelector('[data-kind-delivery]')?.textContent?.trim()).toBe('Delivered in the daily overviews at 07:30, 12:00, 17:00 on working days.');
    (element.querySelector('[data-save-notifications]') as HTMLButtonElement).click();
    await settle();
    expect(backend.requests.filter(request => request.method === 'PUT').pop()?.body).toEqual({
      alertKinds: [{ id: 'task-twice', digestTimes: ['07:30', '12:00', '17:00'], quietHours: { start: '21:00', end: '07:00' } }]
    });
    expect(times().map(input => input.value)).toEqual(['07:30', '12:00', '17:00']);

    // A fourth time is the last; a time given twice is refused before saving.
    (item().querySelector('[data-kind-add-time]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(times().length).toBe(4);
    expect(item().querySelector('[data-kind-add-time]')).withContext('four times a day at most').toBeNull();
    change(times()[3], '12:00');
    expect(times()[3].getAttribute('aria-invalid')).toBe('true');
    expect((element.querySelector('[data-save-notifications]') as HTMLButtonElement).disabled).toBeTrue();
    (item().querySelectorAll('[data-kind-remove-time]')[3] as HTMLButtonElement).click();
    fixture.detectChanges();
    expect((element.querySelector('[data-save-notifications]') as HTMLButtonElement).disabled).withContext('as stored').toBeTrue();

    // Back to the kind's times and quiet hours.
    (item().querySelector('[data-kind-reset]') as HTMLButtonElement).click();
    (item().querySelector('[data-kind-quiet-reset]') as HTMLButtonElement).click();
    fixture.detectChanges();
    (element.querySelector('[data-save-notifications]') as HTMLButtonElement).click();
    await settle();
    expect(backend.requests.filter(request => request.method === 'PUT').pop()?.body)
      .toEqual({ alertKinds: [{ id: 'task-twice', digestTimes: null, quietHours: null }] });
  });

  it('shows the quiet hours of a kind the person may not change, and refuses quiet hours that are none', async () => {
    const element = await render({ kinds: [{ ...twice, mayChangeQuietHours: false, urgentBreaksQuietHours: false }], timeZone: 'Europe/Amsterdam' });
    const item = kind(element, 'task-twice');
    expect(item.querySelector('[data-kind-quiet-start]')).toBeNull();
    expect(item.querySelector('[data-kind-quiet-hours-fixed]')?.textContent?.trim()).toBe('Nothing is delivered from 22:00 until 07:00.');

    TestBed.resetTestingModule();
    const editable = await render({ kinds: [twice], timeZone: 'Europe/Amsterdam' });
    const end = kind(editable, 'task-twice').querySelector('[data-kind-quiet-end]') as HTMLInputElement;
    change(end, '22:00');
    expect(end.getAttribute('aria-invalid')).toBe('true');
    expect(kind(editable, 'task-twice').textContent).toContain('Enter two different times such as 22:00 and 07:00.');
    expect((editable.querySelector('[data-save-notifications]') as HTMLButtonElement).disabled).toBeTrue();
  });

  it('lists the objects the person muted and lifts one with the next save', async () => {
    const until = new Date(Date.now() + 7 * 24 * 60 * 60_000).toISOString();
    const element = await render({
      kinds: [twice],
      timeZone: 'Europe/Amsterdam',
      subjectMutes: [
        {
          id: 'mute-1', kindId: 'task-twice', displayNameKey: 'tests.alert-kinds.task-twice', subjectType: 'project', subjectId: 'p-1',
          subjectDisplayName: 'Launch', subjectTypeName: 'Project', mutedAt: new Date().toISOString(), until
        },
        {
          id: 'mute-2', kindId: 'task-twice', displayNameKey: 'tests.alert-kinds.task-twice', subjectType: 'project', subjectId: 'p-2',
          subjectDisplayName: 'Fair', mutedAt: new Date(Date.now() - 60_000).toISOString(), until
        }
      ]
    });
    const mutes = () => Array.from(element.querySelectorAll('[data-subject-mute]')) as HTMLElement[];
    expect(mutes().map(mute => mute.querySelector('strong')?.textContent?.replace(/\s+/g, ' ').trim())).toEqual(['Project Launch', 'Fair']);
    expect(mutes()[0].textContent).toContain('until');

    const lift = mutes()[0].querySelector('[data-lift-mute]') as HTMLButtonElement;
    expect(lift.textContent?.trim()).toBe('Lift');
    lift.click();
    fixture.detectChanges();
    expect(mutes()[0].textContent).toContain('lifted when you save');
    expect(lift.getAttribute('aria-pressed')).toBe('true');
    (element.querySelector('[data-save-notifications]') as HTMLButtonElement).click();
    await settle();

    expect(TestBed.inject(NhAssistantMockBackend).requests.filter(request => request.method === 'PUT').pop()?.body)
      .toEqual({ liftSubjectMutes: ['mute-1'] });
    expect(mutes().map(mute => mute.getAttribute('data-mute-id'))).toEqual(['mute-2']);
  });

  it('lists no kinds for a server without alerts and keeps working', async () => {
    const element = await render(null);

    expect(element.querySelector('[data-no-alert-kinds]')?.textContent?.trim()).toBe('There are no alerts to choose for.');
    expect(element.querySelector('[data-time-zone]')).toBeNull();
    expect(element.querySelector('[data-save-notifications]')).toBeNull();
  });
});
