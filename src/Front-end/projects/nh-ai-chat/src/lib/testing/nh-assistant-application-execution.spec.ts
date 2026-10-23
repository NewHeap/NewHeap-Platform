import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { NhAssistantAgentProposalsComponent } from '../../../agent-runs/src/public-api';
import { AgentProposal, AgentSubjectWorkProposal } from '../models/assistant-agent-run.models';
import { provideNhAssistant } from '../provide-nh-assistant';

/**
 * A proposal the application executes (S5, `ExecuteInApplication` on the server): approved, it waits
 * for the application's report as `awaiting-application`; when the report did not come in time it is
 * `execution-unknown`, and an approver hands it to the application again instead of running it. The
 * cards say so in English and Dutch, and the mock API answers as the server does.
 */
function scenario(): NhAssistantMockScenario {
  return {
    agents: [{ id: 'purchasing', version: 1, displayNameKey: 'Purchasing', descriptionKey: 'Orders hardware for projects.', canMutate: true }],
    turns: [],
    subjectTypes: [{
      id: 'project',
      names: { en: 'Project', nl: 'Project' },
      subjects: [{ id: 'p-1', displayName: 'PRJ-1 Pietje' }]
    }],
    proposals: [
      {
        approvalId: 'order-1',
        agentId: 'purchasing',
        subjectType: 'project',
        subjectId: 'p-1',
        toolId: 'projects.order-hardware',
        executedByApplication: true
      },
      {
        approvalId: 'order-2',
        agentId: 'purchasing',
        subjectType: 'project',
        subjectId: 'p-1',
        toolId: 'projects.order-hardware',
        status: 'execution-unknown',
        executionResultCode: 'report-overdue',
        executedByApplication: true
      },
      {
        approvalId: 'status-1',
        agentId: 'purchasing',
        subjectType: 'project',
        subjectId: 'p-1',
        toolId: 'projects.change-status',
        status: 'execution-unknown'
      }
    ]
  };
}

@Component({
  standalone: true,
  imports: [NhAssistantAgentProposalsComponent],
  template: `<nh-assistant-agent-proposals [proposals]="proposals()" [canDecide]="true" (changed)="changes = changes + 1" />`
})
class ProposalsHost {
  readonly proposals = signal<AgentProposal[]>([]);
  changes = 0;
}

function flush(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve));
}

async function configure(language: 'en' | 'nl'): Promise<void> {
  TestBed.configureTestingModule({
    providers: [
      provideTranslateService({ fallbackLang: 'en' }),
      provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
      provideNhAssistantMockApi(scenario())
    ]
  });
  await firstValueFrom(TestBed.inject(TranslateService).use(language));
}

async function settle(fixture: ComponentFixture<unknown>): Promise<HTMLElement> {
  for (let round = 0; round < 6; round++) {
    fixture.detectChanges();
    await flush();
  }
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

async function call(method: string, path: string, body?: unknown): Promise<{ status: number; body: any }> {
  const response = await TestBed.inject(NhAssistantMockBackend).fetch(`/api/assistant/${path}`, {
    method,
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  const text = await response.text();
  return { status: response.status, body: text ? JSON.parse(text) : null };
}

async function read(approvalId: string): Promise<AgentProposal> {
  return ((await call('GET', `approvals/${approvalId}`)).body as AgentSubjectWorkProposal).proposal;
}

function card(element: HTMLElement, index: number): HTMLElement {
  return element.querySelectorAll('li.card')[index] as HTMLElement;
}

function buttonsOf(element: HTMLElement): string[] {
  return Array.from(element.querySelectorAll('button')).map(item => item.textContent?.trim() ?? '');
}

describe('A proposal the application executes', () => {
  it('waits for the application after its approval, and the mock resolves it as the server does', async () => {
    await configure('en');

    const approved = await call('POST', 'approvals/order-1/decisions', { decision: 'approve', expectedProposalHash: 'sha256:order-1' });
    expect(approved.status).toBe(200);
    expect(approved.body.status).toBe('awaiting-application');
    expect(approved.body.executionReportDueAt).toEqual(jasmine.any(String));
    expect(approved.body.executedAt).toBeNull();
    // Only an uncertain proposal is resolved, and one the application executes is handed to it again.
    expect((await call('POST', 'approvals/order-1/resolve', { action: 'hand-to-application' })).body.code)
      .toBe('assistant-execution-not-uncertain');
    expect((await call('POST', 'approvals/order-2/resolve', { action: 're-execute' })).body.code)
      .toBe('assistant-approval-decision-invalid');
    expect((await call('POST', 'approvals/status-1/resolve', { action: 'hand-to-application' })).body.code)
      .toBe('assistant-approval-decision-invalid');

    const handed = await call('POST', 'approvals/order-2/resolve', { action: 'hand-to-application' });

    expect(handed.status).toBe(200);
    expect(handed.body.status).toBe('awaiting-application');
    expect((await call('POST', 'approvals/status-1/resolve', { action: 're-execute' })).body.status).toBe('executed');
  });

  it('says in English that it waits for the application and hands an uncertain one to it again', async () => {
    await configure('en');
    await call('POST', 'approvals/order-1/decisions', { decision: 'approve' });
    const fixture = TestBed.createComponent(ProposalsHost);
    fixture.componentInstance.proposals.set([await read('order-1'), await read('order-2'), await read('status-1')]);
    const element = await settle(fixture);

    const waiting = card(element, 0);
    expect(waiting.querySelector('.card-header')!.textContent).toContain('With the application');
    expect(waiting.querySelector('[data-proposal-awaiting-application]')!.textContent)
      .toContain('The application carries out this approved proposal and reports back when it is done.');
    expect(waiting.querySelector('[data-proposal-report-due]')!.textContent).toContain('Report due');
    expect(buttonsOf(waiting)).not.toContain('Hand to the application again');

    const uncertain = card(element, 1);
    expect(uncertain.querySelector('[data-proposal-unknown-application]')!.textContent)
      .toContain('The application did not confirm that it carried this out.');
    expect(buttonsOf(uncertain)).toEqual(['Mark as resolved', 'Hand to the application again']);
    // A proposal that runs in the tool's function keeps running again.
    expect(buttonsOf(card(element, 2))).toEqual(['Mark as resolved', 'Run again']);

    (uncertain.querySelector('[data-proposal-hand-to-application]') as HTMLButtonElement).click();
    await settle(fixture);

    expect(fixture.componentInstance.changes).toBe(1);
    expect((await read('order-2')).status).toBe('awaiting-application');
  });

  it('says the same in Dutch', async () => {
    await configure('nl');
    await call('POST', 'approvals/order-1/decisions', { decision: 'approve' });
    const fixture = TestBed.createComponent(ProposalsHost);
    fixture.componentInstance.proposals.set([await read('order-1'), await read('order-2')]);
    const element = await settle(fixture);

    expect(card(element, 0).querySelector('.card-header')!.textContent).toContain('Bij de applicatie');
    expect(card(element, 0).querySelector('[data-proposal-report-due]')!.textContent).toContain('Melding verwacht');
    expect(buttonsOf(card(element, 1))).toEqual(['Als afgehandeld markeren', 'Opnieuw aan de applicatie geven']);
  });
});
