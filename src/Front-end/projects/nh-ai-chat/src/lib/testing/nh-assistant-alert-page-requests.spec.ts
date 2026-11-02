import { Component, DestroyRef, inject } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { NhAssistantAlertsComponent } from '../components/alerts/nh-assistant-alerts.component';
import { NhAssistantNotificationPreferencesComponent } from '../components/preferences/nh-assistant-notification-preferences.component';
import { NotificationAlertKind } from '../models/assistant-alert.models';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAlertBadgeService } from '../services/nh-assistant-alert-badge.service';
import { NhAssistantPushService } from '../services/nh-assistant-push.service';
import { NhAssistantStore } from '../services/nh-assistant.store';

function flush(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve));
}

const dueToday: NotificationAlertKind = {
  id: 'project-due-today',
  displayNameKey: 'tests.alert-kinds.project-due-today',
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
  availableChannels: ['push', 'in-app'],
  defaultDigestTimes: ['07:30', '16:00'],
  digestTimes: null,
  mayChangeQuietHours: true,
  defaultQuietHours: null,
  quietHours: null,
  urgentBreaksQuietHours: false
};

const scenario: NhAssistantMockScenario = {
  agents: [],
  turns: [],
  collaboration: { push: true },
  alerts: {
    kinds: [dueToday],
    items: [
      {
        id: 'a-launch', kindId: 'project-due-today', urgency: 'warning', title: 'Launch is due today',
        subjectType: 'project', subjectId: 'p-1', subjectDisplayName: 'Launch', updatedAt: '2026-11-02T07:00:00Z'
      }
    ]
  }
};

/**
 * The page a host shows its alerts on, as the sample's alerts page does: the list next to the person's
 * notification choices, while the shell watches the badge.
 */
@Component({
  standalone: true,
  imports: [NhAssistantAlertsComponent, NhAssistantNotificationPreferencesComponent],
  template: `<nh-assistant-alerts /><nh-assistant-notification-preferences />`
})
class AlertsPageComponent {
  constructor() {
    const release = inject(NhAssistantAlertBadgeService).watch();
    inject(DestroyRef).onDestroy(release);
  }
}

/**
 * FB87-13: loading the alerts page sends every request once. The assistant's start activates push for
 * the person while the page's notification choices load the same settings; they share one
 * `GET notifications`, and the badge and the list read once each.
 */
describe('The requests of the alerts page', () => {
  let fixture: ComponentFixture<AlertsPageComponent> | undefined;

  function configure(): void {
    TestBed.configureTestingModule({
      providers: [
        provideTranslateService({ fallbackLang: 'en' }),
        provideNhAssistant({
          apiBaseUrl: '/api/assistant',
          getAccessToken: () => 'token',
          push: { serviceWorkerUrl: '/nh-assistant/nh-assistant-push-worker.js' }
        }),
        provideNhAssistantMockApi(scenario)
      ]
    });
  }

  async function settle(): Promise<void> {
    for (let attempt = 0; attempt < 20; attempt++) {
      await flush();
      fixture?.detectChanges();
    }
  }

  /** How often the page asked for each `GET` path. */
  function gets(): Record<string, number> {
    const counts: Record<string, number> = {};
    for (const request of TestBed.inject(NhAssistantMockBackend).requests.filter(item => item.method === 'GET')) {
      counts[request.path] = (counts[request.path] ?? 0) + 1;
    }
    return counts;
  }

  afterEach(() => {
    fixture?.destroy();
    fixture = undefined;
  });

  it('asks once for each when the assistant starts with the page', async () => {
    configure();
    await firstValueFrom(TestBed.inject(TranslateService).use('en'));

    // The shell starts the assistant and the router shows the page in the same turn.
    void TestBed.inject(NhAssistantStore).initialize();
    fixture = TestBed.createComponent(AlertsPageComponent);
    fixture.detectChanges();
    await settle();

    expect(gets()).toEqual({ status: 1, conversations: 1, notifications: 1, alerts: 1, 'alerts/summary': 1 });
    expect(TestBed.inject(NhAssistantPushService).settings()?.alertKinds?.map(kind => kind.id)).toEqual(['project-due-today']);
    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelectorAll('[data-alert]').length).toBe(1);
    expect(element.querySelectorAll('[data-alert-kind]').length).toBe(1);
  });

  it('asks once for each when the page shows before the assistant starts', async () => {
    configure();
    await firstValueFrom(TestBed.inject(TranslateService).use('en'));

    fixture = TestBed.createComponent(AlertsPageComponent);
    fixture.detectChanges();
    await settle();
    void TestBed.inject(NhAssistantStore).initialize();
    await settle();

    expect(gets()).toEqual({ status: 1, conversations: 1, notifications: 1, alerts: 1, 'alerts/summary': 1 });
  });

  it('reads the settings once more when the person opens the page later, and nothing else twice', async () => {
    configure();
    await firstValueFrom(TestBed.inject(TranslateService).use('en'));
    await TestBed.inject(NhAssistantStore).initialize();
    await settle();
    expect(gets()).toEqual({ status: 1, conversations: 1, notifications: 1 });

    fixture = TestBed.createComponent(AlertsPageComponent);
    fixture.detectChanges();
    await settle();

    // The page shows the choices as they are now; push stays activated and asks nothing more.
    expect(gets()).toEqual({ status: 1, conversations: 1, notifications: 2, alerts: 1, 'alerts/summary': 1 });
  });

  it('shows a mute set on the list in the choices without loading the page again', async () => {
    configure();
    await firstValueFrom(TestBed.inject(TranslateService).use('en'));
    void TestBed.inject(NhAssistantStore).initialize();
    fixture = TestBed.createComponent(AlertsPageComponent);
    fixture.detectChanges();
    await settle();
    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('[data-subject-mutes]')).toBeNull();

    (element.querySelector('[data-alert-id="a-launch"] [data-alert-mute]') as HTMLButtonElement).click();
    fixture.detectChanges();
    (element.querySelector('[data-alert-mute-days="30"]') as HTMLButtonElement).click();
    await settle();

    expect(element.querySelectorAll('[data-subject-mute]').length).toBe(1);
    // The dismissal, then the list, the badge and the settings once each.
    expect(gets()).toEqual({ status: 1, conversations: 1, notifications: 2, alerts: 2, 'alerts/summary': 2 });
  });
});
