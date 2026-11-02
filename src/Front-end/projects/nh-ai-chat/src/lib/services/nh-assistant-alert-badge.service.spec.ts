import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { NH_ASSISTANT_FETCH, NhAssistantFetch } from '../nh-assistant.config';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NH_ASSISTANT_ALERT_BADGE_FOCUS_DELAY_MS, NhAssistantAlertBadgeService } from './nh-assistant-alert-badge.service';
import { NhAssistantApiError, NhAssistantApiService } from './nh-assistant-api.service';

function jsonResponse(value: unknown, status = 200): Response {
  return new Response(JSON.stringify(value), { status, headers: { 'content-type': 'application/json' } });
}

async function flush(): Promise<void> {
  for (let turn = 0; turn < 5; turn++) {
    await new Promise(resolve => setTimeout(resolve));
  }
}

function summary(info: number, warning: number, urgent: number): unknown {
  return { unread: info + warning + urgent, unreadByUrgency: { info, warning, urgent } };
}

describe('NhAssistantAlertBadgeService', () => {
  let fetchSpy: jasmine.Spy<NhAssistantFetch>;
  let answers: unknown[];

  beforeEach(() => {
    answers = [];
    fetchSpy = jasmine.createSpy<NhAssistantFetch>('fetch').and.callFake(async () => {
      const answer = answers.length > 1 ? answers.shift() : answers[0];
      return answer instanceof Response ? answer : jsonResponse(answer);
    });
    TestBed.configureTestingModule({
      providers: [
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
        { provide: NH_ASSISTANT_FETCH, useFactory: () => fetchSpy }
      ]
    });
  });

  function summaryReads(): number {
    return fetchSpy.calls.allArgs().filter(([url]) => url === '/api/assistant/alerts/summary').length;
  }

  it('reads its counts once it is watched, and not before', async () => {
    answers = [summary(1, 2, 0)];
    const badge = TestBed.inject(NhAssistantAlertBadgeService);
    badge.refresh();
    await flush();
    expect(summaryReads()).withContext('nobody watches').toBe(0);
    expect(badge.summary()).toBeNull();

    const release = badge.watch();
    await flush();

    expect(summaryReads()).toBe(1);
    expect(badge.unread()).toBe(3);
    expect(badge.unreadByUrgency()).toEqual({ info: 1, warning: 2, urgent: 0 });
    expect(badge.highestUrgency()).toBe('warning');
    release();
  });

  it('reads again after the person\'s own action, on focus after a pause, and forgets the counts when nobody watches', async () => {
    answers = [summary(0, 0, 2), summary(0, 0, 1)];
    const badge = TestBed.inject(NhAssistantAlertBadgeService);
    const first = badge.watch();
    const second = badge.watch();
    await flush();
    expect(badge.highestUrgency()).toBe('urgent');

    badge.refresh();
    await flush();
    expect(summaryReads()).toBe(2);
    expect(badge.unread()).toBe(1);

    // The focus right after a read reads nothing; after the pause it reads again.
    window.dispatchEvent(new Event('focus'));
    await flush();
    expect(summaryReads()).toBe(2);
    const now = Date.now();
    spyOn(Date, 'now').and.returnValue(now + NH_ASSISTANT_ALERT_BADGE_FOCUS_DELAY_MS + 1);
    window.dispatchEvent(new Event('focus'));
    await flush();
    expect(summaryReads()).toBe(3);

    first();
    first();
    expect(badge.unread()).withContext('one watcher remains').toBe(1);
    second();
    expect(badge.summary()).toBeNull();
    expect(badge.highestUrgency()).toBeNull();
    window.dispatchEvent(new Event('focus'));
    await flush();
    expect(summaryReads()).toBe(3);
  });

  it('shows no badge while the assistant cannot be read', async () => {
    answers = [new Response(null, { status: 404 })];
    const badge = TestBed.inject(NhAssistantAlertBadgeService);
    const release = badge.watch();
    await flush();

    expect(badge.summary()).toBeNull();
    expect(badge.unread()).toBe(0);
    release();
  });
});

describe('NhAssistantApiService alerts', () => {
  let fetchSpy: jasmine.Spy<NhAssistantFetch>;

  function setup(response: Response): NhAssistantApiService {
    fetchSpy = jasmine.createSpy<NhAssistantFetch>('fetch').and.returnValue(Promise.resolve(response));
    TestBed.configureTestingModule({
      providers: [
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
        { provide: NH_ASSISTANT_FETCH, useFactory: () => fetchSpy }
      ]
    });
    return TestBed.inject(NhAssistantApiService);
  }

  it('lists the alerts with a filter whose lists repeat their parameter', async () => {
    const api = setup(jsonResponse({ items: [], total: 0, groups: [] }));

    await firstValueFrom(api.getAlerts({
      status: 'open',
      kind: ['task-due-today', 'project-late'],
      subjectType: 'project',
      subjectId: 'p 1',
      urgency: ['warning', 'urgent'],
      grouped: true,
      page: 2,
      itemsPerPage: 10
    }));

    expect(fetchSpy.calls.mostRecent().args[0]).toBe(
      '/api/assistant/alerts?status=open&kind=task-due-today&kind=project-late&subjectType=project&subjectId=p+1&urgency=warning&urgency=urgent&grouped=true&page=2&itemsPerPage=10');
  });

  it('reads the summary, marks an alert read and dismisses one', async () => {
    const api = setup(jsonResponse({ unread: 0, unreadByUrgency: { info: 0, warning: 0, urgent: 0 } }));
    await firstValueFrom(api.getAlertSummary({ subjectType: 'project', subjectId: 'p-1' }));
    expect(fetchSpy.calls.mostRecent().args[0]).toBe('/api/assistant/alerts/summary?subjectType=project&subjectId=p-1');

    fetchSpy.and.callFake(async () => jsonResponse({ id: 'a 1' }));
    await firstValueFrom(api.markAlertRead('a 1'));
    expect(fetchSpy.calls.mostRecent().args[0]).toBe('/api/assistant/alerts/a%201/read');
    expect(fetchSpy.calls.mostRecent().args[1].method).toBe('POST');
    await firstValueFrom(api.dismissAlert('a 1'));
    expect(fetchSpy.calls.mostRecent().args[0]).toBe('/api/assistant/alerts/a%201/dismiss');
  });

  it('sends the person\'s changes per kind and fails with the code of a refused change', async () => {
    const api = setup(jsonResponse({ code: 'assistant-validation', messageKey: 'nh-assistant.errors.assistant-validation' }, 400));

    const failure = await firstValueFrom(api.updateNotificationPreferences({
      alertKinds: [{ id: 'task-due-today', digestTime: '08:00', channels: { push: false } }]
    })).catch((error: unknown) => error);

    expect(failure instanceof NhAssistantApiError).toBeTrue();
    expect((failure as NhAssistantApiError).code).toBe('assistant-validation');
    const [url, init] = fetchSpy.calls.mostRecent().args;
    expect(url).toBe('/api/assistant/notifications');
    expect(init.method).toBe('PUT');
    expect(JSON.parse(init.body as string)).toEqual({ alertKinds: [{ id: 'task-due-today', digestTime: '08:00', channels: { push: false } }] });
  });
});
