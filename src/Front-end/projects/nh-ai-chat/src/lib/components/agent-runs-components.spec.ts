import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideTranslateService } from '@ngx-translate/core';
import {
  NhAssistantAgentConsultationsComponent,
  NhAssistantAgentInboxComponent,
  NhAssistantAgentProposalsComponent,
  NhAssistantAgentThreadViewComponent
} from '@newheap/platform-ai-chat/agent-runs';
import { Subject, of } from 'rxjs';
import {
  AgentConsultation,
  AgentInbox,
  AgentProposal,
  AgentProposalFeedback,
  AgentThread,
  AgentThreadLookup
} from '../models/assistant-agent-run.models';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAgentRunApiService } from '../services/nh-assistant-agent-run-api.service';
import { NhAssistantProposalFeedbackComponent } from './proposal-feedback/nh-assistant-proposal-feedback.component';

function proposal(status: AgentProposal['status'], id = 'approval-1'): AgentProposal {
  return {
    approvalId: id,
    proposalId: `proposal-${id}`,
    proposalHash: `hash-${id}`,
    conversationId: 'conversation-1',
    runId: 'run-1',
    agentId: 'intake',
    subjectType: 'project',
    subjectId: 'project-1',
    subjectDisplayName: 'Project One',
    toolId: 'projects.request-review',
    summary: 'Approval is required before this tool can run.',
    presentation: null,
    argumentsPreview: '{"projectId":"project-1"}',
    targets: [],
    status,
    mode: 'deferred',
    policyId: 'project-intake',
    grantSource: 'agent',
    createdAt: '2026-10-13T10:00:00Z',
    expiresAt: '2026-10-16T10:00:00Z',
    initiatorActorId: null,
    decidedByActorId: null,
    decidedAt: null,
    executionResultCode: null,
    executedAt: null
  };
}

function thread(activeRun: AgentThread['activeRun']): AgentThread {
  return {
    conversationId: 'conversation-1',
    agentId: 'intake',
    subjectType: 'project',
    subjectId: 'project-1',
    subjectDisplayName: 'Project One',
    accountability: 'agent',
    accountableOwnerId: 'agent:intake',
    role: 'approver',
    activeRun,
    waitingInputs: 0
  };
}

@Component({
  standalone: true,
  imports: [NhAssistantAgentProposalsComponent],
  template: `
    <nh-assistant-agent-proposals
      [proposals]="proposals()"
      [canDecide]="canDecide()"
      [selectable]="selectable()"
      (changed)="changes = changes + 1" />
  `
})
class ProposalsHostComponent {
  readonly proposals = signal<AgentProposal[]>([]);
  readonly canDecide = signal(false);
  readonly selectable = signal(false);
  changes = 0;
}

const ownFeedback: AgentProposalFeedback = {
  approvalId: 'approval-1',
  category: 'not-now',
  group: 'right-but-not-now',
  text: 'After the review',
  bulk: false,
  createdAt: '2026-10-13T11:00:00Z',
  updatedAt: '2026-10-13T11:00:00Z'
};

@Component({
  standalone: true,
  imports: [NhAssistantAgentConsultationsComponent],
  template: `<nh-assistant-agent-consultations conversationId="conversation-1" />`
})
class ConsultationsHostComponent {}

function consultation(status: AgentConsultation['status'], askerDisplayName: string | null): AgentConsultation {
  return {
    id: `consultation-${status}`,
    askerActorId: 'user-7',
    askerDisplayName,
    askingAgentId: 'project-assistant',
    depth: 1,
    status,
    failureCode: status === 'failed' ? 'assistant-consult-timeout' : null,
    toolCalls: 2,
    inputTokens: 100,
    outputTokens: 20,
    proposalCount: status === 'completed' ? 1 : 0,
    createdAt: '2026-10-13T11:00:00Z',
    endedAt: '2026-10-13T11:00:05Z'
  };
}

@Component({
  standalone: true,
  imports: [NhAssistantAgentThreadViewComponent],
  template: `<nh-assistant-agent-thread-view agentId="intake" subjectType="project" subjectId="project-1" (openConversation)="opened = $event" />`
})
class ThreadHostComponent {
  opened: string | null = null;
}

function buttons(fixture: ComponentFixture<unknown>): HTMLButtonElement[] {
  return Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('button'));
}

function button(fixture: ComponentFixture<unknown>, key: string): HTMLButtonElement | undefined {
  return buttons(fixture).find(item => item.textContent?.includes(key));
}

describe('agent-run components', () => {
  let api: jasmine.SpyObj<NhAssistantAgentRunApiService>;

  beforeEach(() => {
    api = jasmine.createSpyObj<NhAssistantAgentRunApiService>('NhAssistantAgentRunApiService', [
      'getThread',
      'getInbox',
      'getProposals',
      'getConsultations',
      'startAgent',
      'decide',
      'decideMany',
      'resolve',
      'giveFeedback',
      'giveFeedbackMany'
    ]);
    TestBed.configureTestingModule({
      providers: [
        provideTranslateService(),
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token', translations: 'host' }),
        { provide: NhAssistantAgentRunApiService, useValue: api }
      ]
    });
  });

  describe('nh-assistant-agent-proposals', () => {
    let fixture: ComponentFixture<ProposalsHostComponent>;

    beforeEach(() => {
      fixture = TestBed.createComponent(ProposalsHostComponent);
    });

    it('offers decisions only to people who may decide', () => {
      fixture.componentInstance.proposals.set([proposal('pending')]);
      fixture.detectChanges();
      expect(button(fixture, 'proposal.approve')).toBeUndefined();

      fixture.componentInstance.canDecide.set(true);
      fixture.detectChanges();
      expect(button(fixture, 'proposal.approve')).toBeDefined();
      expect(button(fixture, 'proposal.reject')).toBeDefined();
    });

    it('explains a proposal in the words of the application and keeps the input under technical details', () => {
      fixture.componentInstance.proposals.set([{
        ...proposal('pending'),
        presentation: {
          toolDisplayName: 'Change project status',
          summary: 'Move Pietje from Draft to Active.',
          fields: [{ label: 'New status', value: 'Active' }],
          notice: null
        }
      }]);
      fixture.detectChanges();

      const element = fixture.nativeElement as HTMLElement;
      expect(element.querySelector('.card-header')!.textContent).toContain('Change project status');
      expect(element.querySelector('.summary')!.textContent).toContain('Move Pietje from Draft to Active.');
      expect(element.querySelector('.presentation-fields dd')!.textContent).toContain('Active');
      expect(element.textContent).not.toContain('Approval is required before this tool can run.');
      const details = element.querySelector('details.technical-details') as HTMLDetailsElement;
      expect(details.open).toBeFalse();
      expect(details.querySelector('pre')!.textContent).toContain('"projectId":"project-1"');
    });

    it('falls back to a neutral explanation when no presenter handled the tool', () => {
      fixture.componentInstance.proposals.set([proposal('pending')]);
      fixture.detectChanges();

      const element = fixture.nativeElement as HTMLElement;
      expect(element.querySelector('.summary')!.textContent).toContain('nh-assistant.approval.fallback-summary');
      expect(element.querySelector('details.technical-details pre')).not.toBeNull();
    });

    it('approves the proposal it shows and reports the change', () => {
      const pending = proposal('pending');
      api.decide.and.returnValue(of({ ...pending, status: 'approved' }));
      fixture.componentInstance.proposals.set([pending]);
      fixture.componentInstance.canDecide.set(true);
      fixture.detectChanges();

      button(fixture, 'proposal.approve')!.click();
      fixture.detectChanges();

      expect(api.decide).toHaveBeenCalledOnceWith(pending, 'approve');
      expect(fixture.componentInstance.changes).toBe(1);
    });

    it('lets an approver resolve an execution with an unknown outcome without running it again', () => {
      const unknown = proposal('execution-unknown');
      api.resolve.and.returnValue(of({ ...unknown, status: 'resolved' }));
      fixture.componentInstance.proposals.set([unknown]);
      fixture.componentInstance.canDecide.set(true);
      fixture.detectChanges();

      expect(button(fixture, 'proposal.approve')).toBeUndefined();
      button(fixture, 'proposal.mark-resolved')!.click();

      expect(api.resolve).toHaveBeenCalledOnceWith('approval-1', 'mark-resolved');
    });

    function feedbackPanels(): NhAssistantProposalFeedbackComponent[] {
      return fixture.debugElement
        .queryAll(By.directive(NhAssistantProposalFeedbackComponent))
        .map(debug => debug.componentInstance as NhAssistantProposalFeedbackComponent);
    }

    it('decides in one action and asks what was wrong after a rejection, also once the list drops it', () => {
      const pending = proposal('pending');
      api.decide.and.returnValue(of({ ...pending, status: 'rejected', canGiveFeedback: true }));
      fixture.componentInstance.proposals.set([pending]);
      fixture.componentInstance.canDecide.set(true);
      fixture.detectChanges();
      const element = fixture.nativeElement as HTMLElement;
      expect(element.querySelector('input[type="text"]')).toBeNull();
      expect(feedbackPanels().length).toBe(0);

      button(fixture, 'proposal.reject')!.click();
      fixture.detectChanges();

      expect(api.decide).toHaveBeenCalledOnceWith(pending, 'reject');
      expect(feedbackPanels().map(panel => panel.approvalIds())).toEqual([['approval-1']]);
      expect(element.querySelector('.feedback-prompt nh-assistant-proposal-feedback')).not.toBeNull();

      // The inbox reloads without the rejected proposal; the question stays.
      fixture.componentInstance.proposals.set([]);
      fixture.detectChanges();
      expect(feedbackPanels().length).toBe(1);

      Array.from(element.querySelectorAll('button')).find(item => item.textContent?.includes('proposal-feedback.skip'))!.click();
      fixture.detectChanges();
      expect(feedbackPanels().length).toBe(0);
    });

    it('does not ask after an approval', () => {
      const pending = proposal('pending');
      api.decide.and.returnValue(of({ ...pending, status: 'approved', canGiveFeedback: false }));
      fixture.componentInstance.proposals.set([pending]);
      fixture.componentInstance.canDecide.set(true);
      fixture.detectChanges();

      button(fixture, 'proposal.approve')!.click();
      fixture.detectChanges();

      expect(feedbackPanels().length).toBe(0);
    });

    it('asks once for proposals rejected together', () => {
      api.decideMany.and.returnValue(of({
        results: [
          { approvalId: 'approval-1', succeeded: true, code: null, status: 'rejected' },
          { approvalId: 'approval-2', succeeded: true, code: null, status: 'rejected' },
          { approvalId: 'approval-3', succeeded: false, code: 'assistant-approval-not-pending', status: null }
        ]
      }));
      fixture.componentInstance.proposals.set([proposal('pending', 'approval-1'), proposal('pending', 'approval-2'), proposal('pending', 'approval-3')]);
      fixture.componentInstance.canDecide.set(true);
      fixture.componentInstance.selectable.set(true);
      fixture.detectChanges();

      ((fixture.nativeElement as HTMLElement).querySelector('.toolbar input[type="checkbox"]') as HTMLInputElement).click();
      fixture.detectChanges();
      button(fixture, 'inbox.reject-selected')!.click();
      fixture.detectChanges();

      expect(api.decideMany).toHaveBeenCalledOnceWith(['approval-1', 'approval-2', 'approval-3'], 'reject');
      expect(feedbackPanels().map(panel => panel.approvalIds())).toEqual([['approval-1', 'approval-2']]);
    });

    it('offers to change the caller\'s own feedback on a closed proposal and reports the change', () => {
      api.giveFeedback.and.returnValue(of({ ...ownFeedback, category: 'already-done' }));
      fixture.componentInstance.proposals.set([{ ...proposal('rejected'), canGiveFeedback: true, feedback: ownFeedback }]);
      fixture.detectChanges();
      const card = (fixture.nativeElement as HTMLElement).querySelector('li.card') as HTMLElement;
      expect(card.querySelector('.meta')!.textContent).toContain('proposal.your-feedback');
      expect(button(fixture, 'proposal.give-feedback')).toBeUndefined();

      button(fixture, 'proposal.change-feedback')!.click();
      fixture.detectChanges();

      expect(card.querySelector('nh-assistant-proposal-feedback')).not.toBeNull();
      expect(feedbackPanels()[0].initial()).toEqual(ownFeedback);
      expect(button(fixture, 'proposal.change-feedback')).toBeUndefined();

      (card.querySelector('input[value="already-done"]') as HTMLInputElement).click();
      fixture.detectChanges();
      (card.querySelector('button[type="submit"]') as HTMLButtonElement).click();
      fixture.detectChanges();

      expect(api.giveFeedback).toHaveBeenCalledOnceWith('approval-1', 'already-done', 'After the review');
      expect(fixture.componentInstance.changes).toBe(1);
      expect(card.querySelector('[role="status"]')!.textContent).toContain('proposal-feedback.thanks');
    });

    it('offers feedback on closed proposals only when the server allows it', () => {
      fixture.componentInstance.proposals.set([proposal('rejected'), { ...proposal('expired', 'approval-2'), canGiveFeedback: true }]);
      fixture.detectChanges();

      const cards = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('li.card'));
      expect(cards[0].textContent).not.toContain('proposal.give-feedback');
      expect(cards[1].textContent).toContain('proposal.give-feedback');
    });
  });

  describe('nh-assistant-agent-inbox', () => {
    it('keeps the feedback question after the rejected proposal left the inbox', () => {
      const pending = proposal('pending');
      const refreshing = new Subject<AgentInbox>();
      api.getInbox.and.returnValues(of({ items: [pending], total: 1 }), of({ items: [], total: 0 }), refreshing);
      api.decide.and.returnValue(of({ ...pending, status: 'rejected', canGiveFeedback: true }));
      const fixture = TestBed.createComponent(NhAssistantAgentInboxComponent);
      fixture.detectChanges();

      button(fixture, 'proposal.reject')!.click();
      fixture.detectChanges();

      const element = fixture.nativeElement as HTMLElement;
      expect(api.getInbox).toHaveBeenCalledTimes(2);
      expect(element.querySelector('li.card')).toBeNull();
      expect(element.textContent).toContain('inbox.empty');
      expect(element.querySelector('.feedback-prompt nh-assistant-proposal-feedback')).not.toBeNull();

      button(fixture, 'agent-runs.refresh')!.click();
      fixture.detectChanges();
      // While the inbox reloads, the open question stays in place instead of a loading state.
      expect(api.getInbox).toHaveBeenCalledTimes(3);
      expect(element.querySelector('.feedback-prompt nh-assistant-proposal-feedback')).not.toBeNull();
      expect(element.querySelector('[role="status"].state')).toBeNull();
    });
  });

  describe('nh-assistant-agent-consultations', () => {
    it('shows who consulted the agent through which assistant, never the question', () => {
      api.getConsultations.and.returnValue(of({
        items: [consultation('completed', 'Pat Planner'), consultation('failed', null)],
        total: 2
      }));
      const fixture = TestBed.createComponent(ConsultationsHostComponent);
      fixture.detectChanges();

      const cards = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('li.card'));
      expect(api.getConsultations).toHaveBeenCalledOnceWith('conversation-1');
      expect(cards.length).toBe(2);
      expect(cards[0].textContent).toContain('consultation.status.completed');
      expect(cards[0].querySelector('.badge.success')).not.toBeNull();
      expect(cards[1].querySelector('.badge.danger')).not.toBeNull();
      expect((fixture.nativeElement as HTMLElement).textContent).toContain('consultation.private');
    });

    it('explains that nobody consulted the agent yet', () => {
      api.getConsultations.and.returnValue(of({ items: [], total: 0 }));
      const fixture = TestBed.createComponent(ConsultationsHostComponent);
      fixture.detectChanges();

      expect((fixture.nativeElement as HTMLElement).textContent).toContain('consultation.empty');
    });
  });

  describe('nh-assistant-agent-thread-view', () => {
    function lookup(activeRun: AgentThread['activeRun']): AgentThreadLookup {
      return { thread: thread(activeRun), canStart: true, role: 'approver' };
    }

    it('starts the agent with the trimmed message and opens the conversation', () => {
      api.getThread.and.returnValue(of(lookup(null)));
      api.getProposals.and.returnValue(of([]));
      api.startAgent.and.returnValue(of({
        triggerId: 'manual',
        outcome: 'started',
        runId: 'run-1',
        conversationId: 'conversation-1',
        reasonCode: null
      }));
      const fixture = TestBed.createComponent(ThreadHostComponent);
      fixture.detectChanges();

      const textarea = (fixture.nativeElement as HTMLElement).querySelector('textarea')!;
      textarea.value = '  Check the deadline.  ';
      textarea.dispatchEvent(new Event('input'));
      button(fixture, 'thread.start')!.click();
      button(fixture, 'thread.open-conversation')!.click();

      expect(api.startAgent).toHaveBeenCalledOnceWith({
        agentId: 'intake',
        subjectType: 'project',
        subjectId: 'project-1',
        message: 'Check the deadline.'
      });
      expect(fixture.componentInstance.opened).toBe('conversation-1');
    });

    it('drops the start notice once the announced run has ended', () => {
      const running = lookup({
        id: 'run-1',
        conversationId: 'conversation-1',
        triggerId: 'manual',
        status: 'running',
        failureClass: null,
        failureCode: null,
        initiatorActorId: 'me',
        accountableOwnerId: 'agent:intake',
        attempt: 1,
        agentVersion: 1,
        toolCalls: 0,
        inputTokens: 0,
        outputTokens: 0,
        proposalCount: 0,
        summary: null,
        shadow: false,
        createdAt: '2026-10-13T10:00:00Z',
        startedAt: '2026-10-13T10:00:01Z',
        endedAt: null,
        backgroundOperationId: 'operation-1'
      });
      api.getThread.and.returnValues(of(lookup(null)), of(running), of(lookup(null)));
      api.getProposals.and.returnValue(of([]));
      api.startAgent.and.returnValue(of({
        triggerId: 'manual',
        outcome: 'started',
        runId: 'run-1',
        conversationId: 'conversation-1',
        reasonCode: null
      }));
      const fixture = TestBed.createComponent(ThreadHostComponent);
      fixture.detectChanges();

      button(fixture, 'thread.start')!.click();
      fixture.detectChanges();
      expect((fixture.nativeElement as HTMLElement).querySelector('[role="status"].success')).not.toBeNull();

      button(fixture, 'agent-runs.refresh')!.click();
      fixture.detectChanges();
      expect((fixture.nativeElement as HTMLElement).querySelector('[role="status"].success')).toBeNull();
    });
  });
});
