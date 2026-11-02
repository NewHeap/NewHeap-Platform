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
    translate.setTranslation('en', { tests: { 'alert-kinds': { 'task-due-today': 'Task due today', 'task-escalated': 'Task escalated' } } }, true);
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

  it('lists no kinds for a server without alerts and keeps working', async () => {
    const element = await render(null);

    expect(element.querySelector('[data-no-alert-kinds]')?.textContent?.trim()).toBe('There are no alerts to choose for.');
    expect(element.querySelector('[data-time-zone]')).toBeNull();
    expect(element.querySelector('[data-save-notifications]')).toBeNull();
  });
});
