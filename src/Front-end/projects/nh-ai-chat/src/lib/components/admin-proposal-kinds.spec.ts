import { Component, Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantAdminProposalKindsComponent } from '@newheap/platform-ai-chat/admin';
import { NhAssistantMockBackend, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom, of, throwError } from 'rxjs';
import { nhAssistantDurationText } from '../../../admin/src/proposal-kinds/nh-assistant-decision-time';
import { ProposalKindNumbers, ProposalKinds, RoutineCandidate } from '../models/assistant-admin.models';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAdminApiService } from '../services/nh-assistant-admin-api.service';
import { NhAssistantApiError } from '../services/nh-assistant-transport';

function numbers(approved: number, rejected: number, medianSeconds: number, percentile90Seconds: number, lastRejectedAt: string | null = null): ProposalKindNumbers {
  const decisions = approved + rejected;
  return {
    pending: 1,
    approved,
    rejected,
    decisions,
    expired: 0,
    withdrawn: 0,
    invalidated: 0,
    staleAfterApproval: 0,
    executionFailed: 0,
    approvalRate: approved / decisions,
    rejectionRate: rejected / decisions,
    decisionTime: { sampleSize: decisions, medianSeconds, percentile90Seconds, sampled: false },
    lastRejectedAt
  };
}

function agent(agentId: string, toolId: string, kindNumbers: ProposalKindNumbers, unmet: RoutineCandidate['unmetThresholds']): RoutineCandidate {
  return {
    agentId,
    agentDisplayName: agentId === 'intake' ? 'Intake reviewer' : null,
    agentSource: agentId === 'intake' ? 'admin' : null,
    toolId,
    numbers: kindNumbers,
    candidate: unmet.length === 0,
    unmetThresholds: unmet
  };
}

const view: ProposalKinds = {
  from: '2026-10-03T00:00:00Z',
  to: '2026-11-02T00:00:00Z',
  thresholds: { minimumDecisions: 50, minimumApprovalRate: 0.98, quietPeriodDays: 7, maximumStaleOrFailedAfterApproval: 0 },
  kinds: [
    {
      toolId: 'projects.change-status',
      displayName: 'Change project status',
      numbers: numbers(70, 0, 95, 240),
      agents: [
        agent('intake', 'projects.change-status', numbers(64, 0, 95, 240), []),
        agent('removed-agent', 'projects.change-status', numbers(6, 0, 400, 3_900), ['too-few-decisions'])
      ]
    },
    {
      toolId: 'projects.add-task',
      displayName: 'projects.add-task',
      numbers: numbers(9, 3, 1_260, 90_000, '2026-10-31T09:00:00Z'),
      agents: [
        agent('intake', 'projects.add-task', numbers(9, 3, 1_260, 90_000, '2026-10-31T09:00:00Z'), ['too-few-decisions', 'approval-rate-too-low', 'recent-rejection'])
      ]
    }
  ]
};

function flush(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve));
}

@Component({
  standalone: true,
  imports: [NhAssistantAdminProposalKindsComponent],
  template: `
    <ng-template #actions let-candidate let-kind="kind">
      <button type="button" data-action>{{ candidate.agentId }}/{{ kind.toolId }}/{{ candidate.candidate }}</button>
    </ng-template>
    <nh-assistant-admin-proposal-kinds [candidateActions]="actions" />
  `
})
class ActionsHostComponent {}

describe('NhAssistantAdminProposalKindsComponent', () => {
  let admin: jasmine.SpyObj<NhAssistantAdminApiService>;

  async function configure(): Promise<void> {
    admin = jasmine.createSpyObj<NhAssistantAdminApiService>('NhAssistantAdminApiService', ['getProposalKinds']);
    admin.getProposalKinds.and.returnValue(of(view));
    TestBed.configureTestingModule({
      providers: [
        provideTranslateService({ fallbackLang: 'en' }),
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
        { provide: NhAssistantAdminApiService, useValue: admin }
      ]
    });
    await firstValueFrom(TestBed.inject(TranslateService).use('en'));
  }

  async function render<T>(type: Type<T>): Promise<ComponentFixture<T>> {
    const fixture = TestBed.createComponent(type);
    fixture.detectChanges();
    await flush();
    fixture.detectChanges();
    return fixture;
  }

  function text(element: Element | null): string {
    return element?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  }

  it('loads the last 30 days, explains the thresholds and says the candidates are suggestions for administrators', async () => {
    await configure();
    const fixture = await render(NhAssistantAdminProposalKindsComponent);
    const element = fixture.nativeElement as HTMLElement;

    const [filter] = admin.getProposalKinds.calls.mostRecent().args;
    expect(Date.parse(filter!.to!) - Date.parse(filter!.from!)).toBe(30 * 24 * 60 * 60 * 1000);
    expect(filter!.agentId).toBeUndefined();

    const notice = text(element.querySelector('[data-suggestions-only]'));
    expect(notice).toContain('These are suggestions from the numbers.');
    expect(notice).toContain('changes nothing about how proposals are decided');
    expect(notice).toContain('Only the assistant\'s administrators see this view.');
    expect(element.querySelector('[data-suggestions-only]')?.getAttribute('role')).toBe('note');

    const thresholds = Array.from(element.querySelectorAll('[data-thresholds] li')).map(text);
    expect(thresholds).toEqual([
      'At least 50 decisions (approved or rejected), so the numbers say enough.',
      'At least 98% of them approved.',
      'No rejection in the last 7 days of the period: people no longer correct the agent.',
      'No approved proposal closed as stale right before it ran, or failed when it ran.'
    ]);
  });

  it('shows each kind with the name the cards show, its numbers and decision times, and opens the kinds with a candidate', async () => {
    await configure();
    const fixture = await render(NhAssistantAdminProposalKindsComponent);
    const element = fixture.nativeElement as HTMLElement;

    const status = element.querySelector('tbody[data-kind="projects.change-status"]')!;
    const kindRow = text(status.querySelector('.kind-row'));
    expect(kindRow).toContain('Change project status');
    expect(kindRow).toContain('projects.change-status');
    expect(kindRow).toContain('1 min');
    expect(kindRow).toContain('4 min');
    expect(kindRow).toContain('1 of 2 agents');
    // The kind with a candidate is open; the other one waits for a click.
    const toggle = status.querySelector('.kind-toggle') as HTMLButtonElement;
    expect(toggle.getAttribute('aria-expanded')).toBe('true');

    const intake = status.querySelector('tr[data-agent-id="intake"]')!;
    expect(intake.getAttribute('data-candidate')).toBe('true');
    expect(text(intake.querySelector('th'))).toContain('Intake reviewer');
    expect(text(intake.querySelector('[data-status]'))).toBe('Candidate');
    // An agent that no longer exists is named by its id.
    const removed = status.querySelector('tr[data-agent-id="removed-agent"]')!;
    expect(text(removed.querySelector('th'))).toBe('removed-agent');
    expect(text(removed.querySelector('[data-median]'))).toBe('6 min');
    expect(text(removed.querySelector('[data-percentile-90]'))).toBe('1 h 5 min');
    const reason = removed.querySelector('[data-reason="too-few-decisions"]')!;
    expect(text(reason)).toBe('Too few decisions');
    expect(reason.getAttribute('title')).toBe('Fewer than 50 decisions in the period.');

    const tasks = element.querySelector('tbody[data-kind="projects.add-task"]')!;
    // Without a stored presentation the tool id is the name, written once.
    expect(text(tasks.querySelector('.kind-name'))).toBe('projects.add-task');
    expect(tasks.querySelectorAll('tr[nhAssistantRoutineCandidateRow]').length).toBe(0);
    (tasks.querySelector('.kind-toggle') as HTMLButtonElement).click();
    fixture.detectChanges();
    const taskRow = tasks.querySelector('tr[data-agent-id="intake"]')!;
    expect(Array.from(taskRow.querySelectorAll('[data-reason]')).map(item => item.getAttribute('data-reason'))).toEqual([
      'too-few-decisions',
      'approval-rate-too-low',
      'recent-rejection'
    ]);
    expect(text(taskRow.querySelector('[data-percentile-90]'))).toBe('1 d 1 h');
    expect(text(taskRow.querySelector('.last-rejected'))).toContain('Last rejected on');
    expect(text(taskRow.querySelector('td:nth-of-type(4)'))).toBe('25%');

    // Only the kinds with a candidate.
    const onlyCandidates = element.querySelector('.only-candidates input') as HTMLInputElement;
    onlyCandidates.checked = true;
    onlyCandidates.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    expect(Array.from(element.querySelectorAll('tbody[data-kind]')).map(item => item.getAttribute('data-kind'))).toEqual(['projects.change-status']);
    // Without an action template the rows have no action column.
    expect(element.querySelector('td.actions')).toBeNull();
  });

  it('gives each agent row an action column with the candidate when the host passes a template', async () => {
    await configure();
    const fixture = await render(ActionsHostComponent);
    fixture.detectChanges();
    await flush();
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;

    expect(text(element.querySelector('thead'))).toContain('Actions');
    const actions = Array.from(element.querySelectorAll('tr[data-agent-id] td.actions [data-action]')).map(text);
    expect(actions).toEqual(['intake/projects.change-status/true', 'removed-agent/projects.change-status/false']);
  });

  it('reloads for another period and shows an error with a retry, and an empty period', async () => {
    await configure();
    const fixture = await render(NhAssistantAdminProposalKindsComponent);
    const element = fixture.nativeElement as HTMLElement;

    admin.getProposalKinds.and.returnValue(throwError(() => new NhAssistantApiError(403, 'assistant-forbidden', 'nh-assistant.errors.assistant-forbidden')));
    const period = element.querySelector('select[id$="-period"]') as HTMLSelectElement;
    period.value = '7';
    period.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    const [filter] = admin.getProposalKinds.calls.mostRecent().args;
    expect(Date.parse(filter!.to!) - Date.parse(filter!.from!)).toBe(7 * 24 * 60 * 60 * 1000);
    expect(element.querySelector('[role="alert"]')).not.toBeNull();

    admin.getProposalKinds.and.returnValue(of({ ...view, kinds: [] }));
    (element.querySelector('[role="alert"] button') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(text(element.querySelector('.state'))).toBe('No agent made proposals in this period.');
  });

  it('writes decision times in the two largest units that matter', () => {
    expect(nhAssistantDurationText(42)).toEqual({ key: 'nh-assistant.admin.proposal-kinds.duration.seconds', params: { count: 42 } });
    expect(nhAssistantDurationText(3_599)).toEqual({ key: 'nh-assistant.admin.proposal-kinds.duration.minutes', params: { count: 59 } });
    expect(nhAssistantDurationText(7_260)).toEqual({ key: 'nh-assistant.admin.proposal-kinds.duration.hours', params: { hours: 2, minutes: 1 } });
    expect(nhAssistantDurationText(3 * 86_400 + 7_200)).toEqual({ key: 'nh-assistant.admin.proposal-kinds.duration.days', params: { days: 3, hours: 2 } });
  });
});

describe('the mock API of the view per kind of action', () => {
  it('shows a routine kind with a candidate and narrows to an agent or a kind', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideTranslateService({ fallbackLang: 'en' }),
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
        provideNhAssistantMockApi({
          agents: [{ id: 'projects', version: 1, displayNameKey: 'agents.projects', descriptionKey: 'agents.projects', canMutate: true }],
          turns: [],
          admin: {
            agents: [{
              source: 'code', id: 'projects', displayName: 'Project assistant', description: 'Projects', instructions: 'Help.',
              toolSelectors: [], mcpServerIds: [], requiredPolicy: null, autonomy: 'propose', isEnabled: true
            }]
          }
        })
      ]
    });
    const api = TestBed.inject(NhAssistantAdminApiService);

    const all = await firstValueFrom(api.getProposalKinds());
    expect(all.thresholds.minimumDecisions).toBe(50);
    expect(all.kinds.map(kind => kind.toolId)).toEqual(['projects.change-status', 'projects.add-task']);
    expect(all.kinds[0].agents[0]).toEqual(jasmine.objectContaining({ agentId: 'projects', toolId: 'projects.change-status', candidate: true }));
    expect(all.kinds[1].agents[0].unmetThresholds).toContain('recent-rejection');

    const tasks = await firstValueFrom(api.getProposalKinds({ toolId: 'projects.add-task' }));
    expect(tasks.kinds.map(kind => kind.toolId)).toEqual(['projects.add-task']);
    const nobody = await firstValueFrom(api.getProposalKinds({ agentId: 'someone-else' }));
    expect(nobody.kinds).toEqual([]);
    const request = TestBed.inject(NhAssistantMockBackend).requests.find(item => item.path === 'admin/proposal-kinds');
    expect(request?.method).toBe('GET');
  });
});
