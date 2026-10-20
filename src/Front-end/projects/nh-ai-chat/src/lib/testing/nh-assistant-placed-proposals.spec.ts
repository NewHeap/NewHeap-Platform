import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantPanelService } from '../services/nh-assistant-panel.service';
import {
  NhAssistantAgentInboxComponent,
  NhAssistantAgentProposalsComponent,
  NhAssistantSubjectSuggestionsComponent
} from '../../../agent-runs/src/public-api';
import { AgentProposal } from '../models/assistant-agent-run.models';

/**
 * Proposals placed at their object (W5): a run for the whole application, or for another object,
 * leaves a proposal at the object it concerns. The cards in the inbox, the thread view and on the
 * object's page say which run placed it and open that run's conversation; the mock API serves and
 * decides such proposals as the server does.
 */
const north = '7f0c0000-0000-4000-8000-0000000000a1';

function scenario(): NhAssistantMockScenario {
  return {
    agents: [{ id: 'follow-up', version: 1, displayNameKey: 'Project follow-up', descriptionKey: 'Counts the open projects.', canMutate: true }],
    turns: [],
    subjectTypes: [{
      id: 'project',
      names: { en: 'Project', nl: 'Project' },
      link: '/projects/{id}',
      subjects: [{ id: 'p-1', displayName: 'PRJ-1 Pietje' }, { id: 'p-2', displayName: 'PRJ-2 Puk' }]
    }],
    proposals: [
      {
        approvalId: 'placed-1',
        agentId: 'follow-up',
        subjectType: 'project',
        subjectId: 'p-1',
        subjectDisplayName: 'PRJ-1 Pietje',
        subjectTypeName: 'Project',
        conversationId: 'thread-p-1',
        runId: 'run-weekly',
        toolId: 'projects.change-status',
        presentation: { toolDisplayName: 'Change project status', summary: 'Complete PRJ-1 Pietje.', fields: [], notice: null },
        rationale: 'All 3 tasks of the project are completed, and 6 projects are open.',
        placedBy: {
          conversationId: 'thread-application',
          runId: 'run-weekly',
          subjectType: 'nh-system',
          subjectId: 'global',
          subjectDisplayName: 'The whole application',
          subjectTypeName: 'The whole application'
        }
      },
      {
        approvalId: 'placed-2',
        agentId: 'follow-up',
        subjectType: 'project',
        subjectId: 'p-2',
        subjectDisplayName: 'PRJ-2 Puk',
        subjectTypeName: 'Project',
        conversationId: 'thread-p-2',
        runId: 'run-weekly',
        toolId: 'projects.change-status',
        canDecide: false,
        reasonCode: 'assistant-four-eyes-required',
        placedBy: { conversationId: 'thread-application', runId: 'run-weekly', subjectType: 'nh-system', subjectId: 'global', subjectDisplayName: 'The whole application' }
      },
      {
        approvalId: 'own-1',
        agentId: 'follow-up',
        subjectType: 'project',
        subjectId: 'p-1',
        conversationId: 'thread-p-1',
        runId: 'run-project',
        toolId: 'projects.change-status'
      }
    ]
  };
}

function flush(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve));
}

async function configure(script = scenario()): Promise<void> {
  TestBed.configureTestingModule({
    providers: [
      provideTranslateService({ fallbackLang: 'en' }),
      provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
      provideNhAssistantMockApi(script)
    ]
  });
  await firstValueFrom(TestBed.inject(TranslateService).use('en'));
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

function origin(card: Element): string {
  return card.querySelector('.placed-by span')?.textContent?.trim() ?? '';
}

function proposal(id: string, placedBy: AgentProposal['placedBy']): AgentProposal {
  return {
    approvalId: id,
    proposalId: `proposal-${id}`,
    proposalHash: `hash-${id}`,
    conversationId: 'thread-p-1',
    runId: 'run-1',
    agentId: 'follow-up',
    subjectType: 'project',
    subjectId: 'p-1',
    subjectDisplayName: 'PRJ-1 Pietje',
    toolId: 'projects.change-status',
    summary: 'Approval is required before this tool can run.',
    presentation: null,
    argumentsPreview: '{}',
    targets: [],
    status: 'pending',
    mode: 'deferred',
    policyId: null,
    grantSource: 'agent',
    createdAt: '2026-10-20T07:00:00Z',
    expiresAt: '2026-10-23T07:00:00Z',
    initiatorActorId: null,
    decidedByActorId: null,
    decidedAt: null,
    executionResultCode: null,
    executedAt: null,
    placedBy
  };
}

@Component({
  standalone: true,
  imports: [NhAssistantAgentProposalsComponent],
  template: `
    <div class="page" style="width: 375px">
      <nh-assistant-agent-proposals [proposals]="proposals()" [canDecide]="true" (openConversation)="opened.push($event)" />
    </div>
  `
})
class ProposalsPageHost {
  readonly proposals = signal<AgentProposal[]>([]);
  readonly opened: string[] = [];
}

@Component({
  standalone: true,
  imports: [NhAssistantAgentInboxComponent],
  template: `<nh-assistant-agent-inbox subjectType="project" (openConversation)="opened.push($event)" />`
})
class InboxHost {
  readonly opened: string[] = [];
}

@Component({
  standalone: true,
  imports: [NhAssistantSubjectSuggestionsComponent],
  template: `<nh-assistant-subject-suggestions subjectType="project" subjectId="p-1" (runConversationOpened)="opened.push($event)" />`
})
class SuggestionsHost {
  readonly opened: string[] = [];
}

describe('Proposals placed at their object', () => {
  it('say which run placed them, for the whole application, a division or another object, and open its conversation', async () => {
    await configure();
    const fixture = TestBed.createComponent(ProposalsPageHost);
    fixture.componentInstance.proposals.set([
      proposal('application', { conversationId: 'thread-application', runId: 'run-1', subjectType: 'nh-system', subjectId: 'global', subjectDisplayName: 'The whole application' }),
      proposal('division', { conversationId: 'thread-north', runId: 'run-2', subjectType: 'nh-system', subjectId: `division-${north.replace(/-/g, '')}`, subjectDisplayName: 'North' }),
      proposal('quote', { conversationId: 'thread-quote', runId: 'run-3', subjectType: 'quote', subjectTypeName: 'Quote' }),
      proposal('unnamed', { conversationId: 'thread-gone', runId: null, subjectType: null }),
      proposal('own', null)
    ]);
    const element = await settle(fixture);

    const cards = Array.from(element.querySelectorAll('li.card'));
    expect(cards.map(origin)).toEqual([
      'Proposed by a run for the whole application',
      'Proposed by a run for the whole application in North',
      'Proposed by a run for another object: Quote',
      'Proposed by a run for another object',
      ''
    ]);
    expect(cards[4].querySelector('.placed-by')).toBeNull();

    // The button names what it opens and emits the run's conversation.
    const open = cards[1].querySelector('.placed-by button') as HTMLButtonElement;
    expect(open.textContent?.trim()).toBe("Open the run's conversation");
    expect(open.getAttribute('aria-label')).toBe('Open the conversation of the run that proposed this');
    open.click();
    expect(fixture.componentInstance.opened).toEqual(['thread-north']);

    // At 375px the line wraps instead of scrolling sideways.
    const page = element.querySelector('.page') as HTMLElement;
    expect(page.scrollWidth).toBeLessThanOrEqual(page.clientWidth + 1);
    for (const line of Array.from(element.querySelectorAll<HTMLElement>('.placed-by'))) {
      expect(line.getBoundingClientRect().right).toBeLessThanOrEqual(page.getBoundingClientRect().right + 0.5);
    }
  });

  it('reach the inbox of the object\'s approvers, who open the run that placed them and decide them', async () => {
    await configure();
    const fixture = TestBed.createComponent(InboxHost);
    let element = await settle(fixture);

    // The inbox lists the pending proposals the caller may decide, with their origin.
    const cards = Array.from(element.querySelectorAll('li.card'));
    expect(cards.map(card => card.getAttribute('data-approval-id'))).toEqual(['own-1', 'placed-1']);
    expect(origin(cards[1])).toBe('Proposed by a run for the whole application');
    expect(origin(cards[0])).toBe('');
    (cards[1].querySelector('.placed-by button') as HTMLButtonElement).click();
    expect(fixture.componentInstance.opened).toEqual(['thread-application']);

    // Approving it executes it in the mock and drops it from the inbox.
    const approve = Array.from(cards[1].querySelectorAll('button')).find(item => item.textContent?.trim() === 'Approve') as HTMLButtonElement;
    approve.click();
    element = await settle(fixture);
    expect(Array.from(element.querySelectorAll('li.card')).map(card => card.getAttribute('data-approval-id'))).toEqual(['own-1']);
    const decided = await call('GET', 'approvals/placed-1');
    expect(decided.body.proposal.status).toBe('executed');
    expect(decided.body.proposal.placedBy.subjectType).toBe('nh-system');
  });

  it('show on the object\'s page with the viewer\'s own right and open the run\'s conversation in the panel', async () => {
    await configure();
    const panel = TestBed.inject(NhAssistantPanelService);
    const opened = spyOn(panel, 'open');
    const fixture = TestBed.createComponent(SuggestionsHost);
    const element = await settle(fixture);

    const cards = Array.from(element.querySelectorAll('li.card'));
    expect(cards.map(card => card.getAttribute('data-approval-id'))).toEqual(['placed-1', 'own-1']);
    expect(origin(cards[0])).toBe('Proposed by a run for the whole application');
    (cards[0].querySelector('.placed-by button') as HTMLButtonElement).click();
    expect(opened).toHaveBeenCalledOnceWith('thread-application');
    expect(fixture.componentInstance.opened).toEqual(['thread-application']);
  });
});

describe('The mock API with proposals placed at their object', () => {
  it('serves the inbox, a thread\'s proposals and a subject\'s open proposals as the server does', async () => {
    await configure();

    const inbox = await call('GET', 'approvals/inbox?subjectType=project&itemsPerPage=10');
    expect(inbox.status).toBe(200);
    expect(inbox.body.total).toBe(2);
    expect(inbox.body.items.map((item: AgentProposal) => item.approvalId)).toEqual(['own-1', 'placed-1']);
    expect(inbox.body.items[1].placedBy.conversationId).toBe('thread-application');
    expect((await call('GET', 'approvals/inbox?runId=run-weekly')).body.total).toBe(1);
    expect((await call('GET', 'approvals/inbox?itemsPerPage=101')).body.code).toBe('assistant-message-invalid');
    expect((await call('GET', 'approvals/inbox?subjectType=Project')).status).toBe(400);

    const thread = await call('GET', 'threads/thread-p-1/proposals');
    expect(thread.body.map((item: AgentProposal) => item.approvalId)).toEqual(['own-1', 'placed-1']);
    const work = await call('GET', 'subjects/project/p-2/work');
    expect(work.body.openProposals).toEqual([jasmine.objectContaining({ canDecide: false, reasonCode: 'assistant-four-eyes-required' })]);
    const lookup = await call('POST', 'approvals/lookup', { approvalIds: ['placed-2', 'missing', 'placed-1'] });
    expect(lookup.body.items.map((item: { proposal: AgentProposal }) => item.proposal.approvalId)).toEqual(['placed-2', 'placed-1']);
  });

  it('decides as the server does', async () => {
    await configure();
    const decide = (id: string, body: unknown) => call('POST', `approvals/${id}/decisions`, body);

    expect((await decide('placed-1', { decision: 'maybe' })).body.code).toBe('assistant-approval-decision-invalid');
    expect((await decide('placed-1', { decision: 'approve', reason: 'x'.repeat(501) })).status).toBe(400);
    expect((await decide('missing', { decision: 'approve' })).status).toBe(404);
    const stale = await decide('placed-1', { decision: 'approve', expectedProposalHash: 'other' });
    expect([stale.status, stale.body.code]).toEqual([409, 'assistant-proposal-hash-mismatch']);
    const refused = await decide('placed-2', { decision: 'reject' });
    expect([refused.status, refused.body.code]).toEqual([403, 'assistant-four-eyes-required']);

    const approved = await decide('placed-1', { decision: 'approve', expectedProposalHash: 'sha256:placed-1' });
    expect(approved.status).toBe(200);
    expect([approved.body.status, approved.body.decidedByActorId]).toEqual(['executed', 'mock-user']);
    expect((await decide('placed-1', { decision: 'reject' })).body.code).toBe('assistant-approval-not-pending');

    const bulk = await call('POST', 'approvals/decisions', { approvalIds: ['own-1', 'placed-2'], decision: 'reject' });
    expect(bulk.body.results).toEqual([
      { approvalId: 'own-1', succeeded: true, code: null, status: 'rejected' },
      { approvalId: 'placed-2', succeeded: false, code: 'assistant-four-eyes-required', status: null }
    ]);
    expect((await call('POST', 'approvals/decisions', { approvalIds: [], decision: 'reject' })).status).toBe(400);
  });
});
