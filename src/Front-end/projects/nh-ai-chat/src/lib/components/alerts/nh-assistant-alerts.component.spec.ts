import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockAlert, NhAssistantMockBackend, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { AlertListStatus, AlertSubjectTarget } from '../../models/assistant-alert.models';
import { provideNhAssistant } from '../../provide-nh-assistant';
import { NhAssistantAlertBadgeService } from '../../services/nh-assistant-alert-badge.service';
import { NhAssistantAlertDensity, NhAssistantAlertsComponent } from './nh-assistant-alerts.component';

function flush(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve));
}

const project = { subjectType: 'project', subjectId: 'p-1', displayName: 'Launch', subjectTypeName: 'Project', subjectLink: '/projects/p-1' };

/** Three alerts of one project, the urgent one the most recent, and a resolved one about another project. */
const alerts: NhAssistantMockAlert[] = [
  {
    id: 'a-report', kindId: 'task-due-today', urgency: 'warning', title: 'Write the report is due today', text: 'Two tasks are open.',
    subjectType: 'task', subjectId: 't-1', subjectDisplayName: 'Write the report', subjectTypeName: 'Task', subjectLink: '/tasks/t-1',
    parent: project, updatedAt: '2026-11-02T07:00:00Z'
  },
  {
    id: 'a-invoice', kindId: 'task-due-today', urgency: 'urgent', title: 'Send the invoice is due today', text: null,
    subjectType: 'task', subjectId: 't-2', subjectDisplayName: 'Send the invoice', subjectTypeName: 'Task', subjectLink: '/tasks/t-2',
    parent: project, updatedAt: '2026-11-02T07:20:00Z'
  },
  {
    id: 'a-room', kindId: 'task-due-today', urgency: 'info', title: 'Book the room is due today', status: 'resolved',
    subjectType: 'task', subjectId: 't-3', subjectDisplayName: 'Book the room', subjectTypeName: 'Task', subjectLink: null,
    parent: { subjectType: 'project', subjectId: 'p-2', displayName: 'Fair' }, updatedAt: '2026-11-02T07:10:00Z'
  }
];

@Component({
  standalone: true,
  imports: [NhAssistantAlertsComponent],
  template: `<nh-assistant-alerts [status]="status()" [density]="density()" [grouped]="grouped()" (openSubject)="opened.push($event)" />`
})
class HostComponent {
  readonly status = signal<AlertListStatus | null>(null);
  readonly density = signal<NhAssistantAlertDensity>('comfortable');
  readonly grouped = signal(true);
  readonly opened: AlertSubjectTarget[] = [];
}

describe('NhAssistantAlertsComponent', () => {
  let fixture: ComponentFixture<HostComponent>;

  async function render(items: NhAssistantMockAlert[] = alerts, enabled = true): Promise<HTMLElement> {
    TestBed.configureTestingModule({
      providers: [
        provideTranslateService({ fallbackLang: 'en' }),
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
        provideNhAssistantMockApi({ agents: [], turns: [], enabled, alerts: { items, kinds: [] } })
      ]
    });
    await firstValueFrom(TestBed.inject(TranslateService).use('en'));
    TestBed.inject(TranslateService).setTranslation('en', { 'nh-assistant': { 'alert-kinds': { 'task-due-today': { name: 'Task due today' } } } }, true);
    fixture = TestBed.createComponent(HostComponent);
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

  function titles(element: HTMLElement): string[] {
    return Array.from(element.querySelectorAll('[data-alert-open]')).map(button => button.textContent?.trim() ?? '');
  }

  function requests(): { method: string; path: string }[] {
    return TestBed.inject(NhAssistantMockBackend).requests.map(request => ({ method: request.method, path: request.path }));
  }

  it('lists the alerts per project, the most recent first, with their urgency, kind and state', async () => {
    const element = await render();

    expect(titles(element)).toEqual(['Send the invoice is due today', 'Write the report is due today', 'Book the room is due today']);
    expect(Array.from(element.querySelectorAll('[data-alert-group]')).map(group => group.textContent?.replace(/\s+/g, ' ').trim()))
      .toEqual(['Project Launch 2 of 2', 'Fair 1 of 1']);
    const urgent = element.querySelector('[data-alert-id="a-invoice"]') as HTMLElement;
    expect(urgent.getAttribute('data-urgency')).toBe('urgent');
    expect(urgent.querySelector('[data-alert-state]')?.textContent?.trim()).toBe('New');
    expect(urgent.textContent).toContain('Task due today');
    expect(urgent.textContent?.replace(/\s+/g, ' ')).toContain('Task: Send the invoice');
    expect(urgent.querySelector('.urgency .visually-hidden')?.textContent?.trim()).toBe('Urgent');
    expect(element.querySelector('[data-alert-id="a-report"] .text')?.textContent).toContain('Two tasks are open.');
    // A resolved alert shows as such, can be dismissed but not marked read.
    const resolved = element.querySelector('[data-alert-id="a-room"]') as HTMLElement;
    expect(resolved.classList).toContain('closed');
    expect(resolved.querySelector('[data-alert-state]')?.textContent?.trim()).toBe('Resolved');
    expect(resolved.querySelector('[data-alert-read]')).toBeNull();
    expect(resolved.querySelector('[data-alert-dismiss]')?.getAttribute('aria-label')).toBe('Dismiss');
    expect(requests()).toContain({ method: 'GET', path: 'alerts' });
  });

  it('marks an alert read, dismisses one and updates the badge after each action', async () => {
    const element = await render();
    const badge = TestBed.inject(NhAssistantAlertBadgeService);
    const release = badge.watch();
    await settle();
    expect(badge.unread()).toBe(2);
    expect(badge.highestUrgency()).toBe('urgent');

    (element.querySelector('[data-alert-id="a-invoice"] [data-alert-read]') as HTMLButtonElement).click();
    await settle();
    expect(element.querySelector('[data-alert-id="a-invoice"]')?.getAttribute('data-status')).toBe('read');
    expect(element.querySelector('[data-alert-id="a-invoice"] [data-alert-read]')).toBeNull();
    expect(badge.unread()).toBe(1);
    expect(badge.highestUrgency()).toBe('warning');

    (element.querySelector('[data-alert-id="a-report"] [data-alert-dismiss]') as HTMLButtonElement).click();
    await settle();
    expect(titles(element)).toEqual(['Send the invoice is due today', 'Book the room is due today']);
    expect(badge.unread()).toBe(0);
    expect(requests().filter(request => request.method === 'POST').map(request => request.path))
      .toEqual(['alerts/a-invoice/read', 'alerts/a-report/dismiss']);
    release();
  });

  it('emits the subject to open for the host and marks the alert read, without navigating', async () => {
    const element = await render();

    (element.querySelector('[data-alert-id="a-report"] [data-alert-open]') as HTMLButtonElement).click();
    await settle();

    const opened = fixture.componentInstance.opened;
    expect(opened.length).toBe(1);
    expect(opened[0]).toEqual(jasmine.objectContaining({ subjectType: 'task', subjectId: 't-1', link: '/tasks/t-1' }));
    expect(opened[0].alert.id).toBe('a-report');
    expect(element.querySelector('[data-alert-id="a-report"]')?.getAttribute('data-status')).toBe('read');
  });

  it('lets the person choose the status, and says what an empty status shows', async () => {
    const element = await render();
    const filter = element.querySelector('[data-alert-status-filter]') as HTMLSelectElement;
    expect(Array.from(filter.options).map(option => option.textContent?.trim()))
      .toEqual(['All alerts', 'Unread', 'Open', 'Resolved or expired', 'Dismissed']);

    filter.value = 'dismissed';
    filter.dispatchEvent(new Event('change'));
    await settle();

    expect(titles(element)).toEqual([]);
    expect(element.querySelector('[data-alerts-empty]')?.textContent?.trim()).toBe('You have not dismissed any alerts.');
    expect(TestBed.inject(NhAssistantMockBackend).requests.some(request => request.path === 'alerts' && request.method === 'GET')).toBeTrue();

    fixture.componentInstance.status.set('closed');
    fixture.detectChanges();
    await settle();
    expect(titles(element)).toEqual(['Book the room is due today']);
  });

  it('shows one row per alert without its text in the compact density, and a flat list without groups', async () => {
    const element = await render();
    fixture.componentInstance.density.set('compact');
    fixture.componentInstance.grouped.set(false);
    fixture.detectChanges();
    await settle();

    expect(element.querySelector('.alerts')?.classList).toContain('compact');
    expect(element.querySelector('[data-alert-group]')).toBeNull();
    expect(element.querySelector('.text')).toBeNull();
    expect(element.querySelector('[data-alert-id="a-report"] [data-alert-open]')?.getAttribute('title')).toBe('Two tasks are open.');
    expect(titles(element)).toEqual(['Send the invoice is due today', 'Book the room is due today', 'Write the report is due today']);
  });

  it('shows an untitled alert by its subject, an empty list and a load failure with a retry', async () => {
    let element = await render([{ ...alerts[0], title: null }]);
    expect(titles(element)).toEqual(['An alert about Write the report']);

    TestBed.resetTestingModule();
    element = await render([]);
    expect(element.querySelector('[data-alerts-empty]')?.textContent?.trim()).toBe('You have no alerts.');

    TestBed.resetTestingModule();
    element = await render(alerts, false);
    const failure = element.querySelector('.state[role="alert"]') as HTMLElement;
    expect(failure.textContent).toContain('Your alerts could not be loaded.');
    expect(failure.querySelector('button')?.textContent?.trim()).toBe('Try again');
  });
});
