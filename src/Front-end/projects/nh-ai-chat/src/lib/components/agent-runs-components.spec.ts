import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed, fakeAsync, flushMicrotasks, tick } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import {
  NhAssistantAgentConsultationsComponent,
  NhAssistantAgentInboxComponent,
  NhAssistantAgentProposalsComponent,
  NhAssistantAgentThreadViewComponent,
  NhAssistantRunTimelineComponent,
  NhAssistantSubjectSuggestionsComponent
} from '@newheap/platform-ai-chat/agent-runs';
import { NEVER, Subject, of, throwError } from 'rxjs';
import {
  AgentConsultation,
  AgentInbox,
  AgentProposal,
  AgentProposalFeedback,
  AgentProposalLookup,
  AgentRun,
  AgentSubjectWork,
  AgentThread,
  AgentThreadLookup
} from '../models/assistant-agent-run.models';
import { LiveAgentWorkChanged } from '../models/assistant-live.models';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAgentRunApiService } from '../services/nh-assistant-agent-run-api.service';
import { NhAssistantApiError } from '../services/nh-assistant-api.service';
import { NhAssistantLiveService, NhAssistantLiveState } from '../services/nh-assistant-live.service';
import { NhAssistantPanelService } from '../services/nh-assistant-panel.service';
import { NhAssistantStore } from '../services/nh-assistant.store';
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

/** The answer of a lookup that finds `proposals`, none of which the caller may decide. */
function lookedUp(...proposals: AgentProposal[]): AgentProposalLookup {
  return { items: proposals.map(item => ({ proposal: item, canDecide: false, reasonCode: null })) };
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

@Component({
  standalone: true,
  imports: [NhAssistantSubjectSuggestionsComponent],
  template: `
    <nh-assistant-subject-suggestions
      subjectType="project"
      subjectId="project-1"
      agentId="intake"
      (conversationOpened)="opened = $event" />
  `
})
class SuggestionsHostComponent {
  opened: AgentThreadLookup | null = null;
}

/** A connected hub whose agent-work updates and reconnects the test sends. */
class FakeLiveService {
  readonly agentWork = new Subject<LiveAgentWorkChanged>();
  readonly resync = new Subject<void>();
  readonly changed$ = NEVER;
  readonly read$ = NEVER;
  readonly removed$ = NEVER;
  readonly events$ = NEVER;
  readonly agentWork$ = this.agentWork.asObservable();
  readonly resync$ = this.resync.asObservable();
  readonly state = signal<NhAssistantLiveState>('connected').asReadonly();

  connect(): Promise<void> {
    return Promise.resolve();
  }

  stop(): Promise<void> {
    return Promise.resolve();
  }
}

function work(conversationId: string, reason: LiveAgentWorkChanged['reason']): LiveAgentWorkChanged {
  return {
    conversationId,
    reason,
    approvalId: reason === 'run' ? null : 'approval-1',
    runId: reason === 'run' ? 'run-1' : null,
    subjectType: 'project',
    subjectId: 'project-1'
  };
}

function buttons(fixture: ComponentFixture<unknown>): HTMLButtonElement[] {
  return Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('button'));
}

function button(fixture: ComponentFixture<unknown>, key: string): HTMLButtonElement | undefined {
  return buttons(fixture).find(item => item.textContent?.includes(key));
}

describe('agent-run components', () => {
  let api: jasmine.SpyObj<NhAssistantAgentRunApiService>;
  let live: FakeLiveService;

  beforeEach(() => {
    live = new FakeLiveService();
    api = jasmine.createSpyObj<NhAssistantAgentRunApiService>('NhAssistantAgentRunApiService', [
      'getThread',
      'getSubjectWork',
      'getInbox',
      'getProposals',
      'lookupProposals',
      'getConsultations',
      'getRuns',
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
        { provide: NhAssistantAgentRunApiService, useValue: api },
        { provide: NhAssistantLiveService, useValue: live }
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

    it('shows why a stale proposal no longer fits and offers no decision', () => {
      fixture.componentInstance.proposals.set([{
        ...proposal('stale'),
        staleKind: 'already-done',
        staleExplanation: 'Completed by Henk on 14 October.',
        executionResultCode: 'task-completed'
      }]);
      fixture.componentInstance.canDecide.set(true);
      fixture.detectChanges();

      const element = fixture.nativeElement as HTMLElement;
      expect(element.querySelector('.card-header')!.textContent).toContain('nh-assistant.approval.stale-kind.already-done');
      expect(element.querySelector('.stale-explanation')!.textContent).toContain('Completed by Henk on 14 October.');
      expect(button(fixture, 'proposal.approve')).toBeUndefined();
      // The list shows everything it is given; only lists of open proposals keep closed cards.
      expect(element.querySelector('li.card.closed')).toBeNull();
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

    it('names who decided from the member snapshot and falls back to the id', () => {
      const translate = TestBed.inject(TranslateService);
      translate.setTranslation('en', { 'nh-assistant': { 'agent-runs': { proposal: { 'decided-by': 'Decided by {{actor}}' } } } });
      translate.use('en');
      fixture.componentInstance.proposals.set([
        { ...proposal('approved'), decidedByActorId: 'user-7', decidedByDisplayName: 'Pat Planner' },
        { ...proposal('rejected', 'approval-2'), decidedByActorId: 'user-8', decidedByDisplayName: null },
        { ...proposal('rejected', 'approval-3'), decidedByActorId: 'user-9' }
      ]);
      fixture.detectChanges();

      const meta = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('li.card .meta'))
        .map(item => item.textContent ?? '');
      expect(meta[0]).toContain('Decided by Pat Planner');
      expect(meta[0]).not.toContain('user-7');
      expect(meta[1]).toContain('Decided by user-8');
      expect(meta[2]).toContain('Decided by user-9');
    });

    it('shows why the agent\'s rules proposed it and who overtook a stale proposal', () => {
      const translate = TestBed.inject(TranslateService);
      translate.setTranslation('en', {
        'nh-assistant': {
          approval: { rationale: 'Why: {{rationale}}' },
          'agent-runs': { proposal: { 'stale-by': 'Overtaken by {{actor}}' } }
        }
      });
      translate.use('en');
      fixture.componentInstance.proposals.set([
        { ...proposal('pending'), rationale: 'All 4 tasks of the project are completed.' },
        {
          ...proposal('stale', 'approval-2'),
          staleKind: 'already-done',
          staleByActorId: 'member-1',
          staleByDisplayName: 'Pat Planner'
        },
        { ...proposal('stale', 'approval-3'), staleKind: 'changed', staleByActorId: 'member-2', staleByDisplayName: null }
      ]);
      fixture.detectChanges();

      const cards = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('li.card'));
      expect(cards[0].querySelector('.rationale')!.textContent).toContain('Why: All 4 tasks of the project are completed.');
      expect(cards[1].querySelector('.rationale')).toBeNull();
      expect(cards[1].querySelector('.meta')!.textContent).toContain('Overtaken by Pat Planner');
      expect(cards[2].querySelector('.meta')!.textContent).toContain('Overtaken by member-2');
    });

    it('reads a superseded proposal as replaced and moves focus to the newer one', () => {
      fixture.componentInstance.proposals.set([
        { ...proposal('withdrawn', 'approval-1'), supersededByApprovalId: 'approval-2' },
        proposal('pending', 'approval-2'),
        { ...proposal('withdrawn', 'approval-3'), supersededByApprovalId: 'approval-elsewhere' },
        proposal('withdrawn', 'approval-4')
      ]);
      fixture.detectChanges();

      const element = fixture.nativeElement as HTMLElement;
      const cards = Array.from(element.querySelectorAll<HTMLElement>('li.card'));
      expect(cards[0].querySelector('.card-header')!.textContent).toContain('nh-assistant.approval.superseded');
      expect(cards[2].querySelector('.card-header')!.textContent).toContain('nh-assistant.approval.superseded');
      expect(cards[3].querySelector('.card-header')!.textContent).toContain('proposal.status.withdrawn');
      // Only a successor in the list can be shown.
      expect(cards[2].textContent).not.toContain('proposal.show-successor');

      button(fixture, 'proposal.show-successor')!.click();
      expect(document.activeElement).toBe(cards[1]);
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

    it('reloads once a burst of agent work settled and after a reconnect', fakeAsync(() => {
      api.getInbox.and.returnValue(of({ items: [proposal('pending')], total: 1 }));
      const fixture = TestBed.createComponent(NhAssistantAgentInboxComponent);
      fixture.detectChanges();
      expect(api.getInbox).toHaveBeenCalledTimes(1);

      // Work on any thread may change the inbox.
      live.agentWork.next(work('conversation-1', 'proposal'));
      tick(200);
      live.agentWork.next(work('conversation-9', 'run'));
      tick(299);
      expect(api.getInbox).toHaveBeenCalledTimes(1);

      tick(1);
      expect(api.getInbox).toHaveBeenCalledTimes(2);

      live.resync.next();
      tick(300);
      expect(api.getInbox).toHaveBeenCalledTimes(3);
    }));

    it('keeps a proposal that closed as stale while shown as a closed card until it is dismissed', fakeAsync(() => {
      const done = proposal('pending', 'approval-1');
      const decided = proposal('pending', 'approval-2');
      api.getInbox.and.returnValues(of({ items: [done, decided], total: 2 }), of({ items: [], total: 0 }));
      api.lookupProposals.and.returnValue(of(lookedUp(
        { ...done, status: 'stale', staleKind: 'already-done', staleExplanation: 'Completed by Henk.' },
        { ...decided, status: 'approved', decidedByActorId: 'colleague' }
      )));
      const fixture = TestBed.createComponent(NhAssistantAgentInboxComponent);
      fixture.detectChanges();

      live.agentWork.next(work('conversation-1', 'proposal'));
      tick(300);
      fixture.detectChanges();

      // The proposal a colleague decided leaves; the one that was already done stays with the reason.
      // Both are read by id in one lookup, never through their thread.
      const element = fixture.nativeElement as HTMLElement;
      expect(api.lookupProposals).toHaveBeenCalledOnceWith(['approval-1', 'approval-2']);
      expect(api.getProposals).not.toHaveBeenCalled();
      const closed = Array.from(element.querySelectorAll<HTMLElement>('li.card'));
      expect(closed.length).toBe(1);
      expect(closed[0].classList).toContain('closed');
      expect(closed[0].textContent).toContain('nh-assistant.approval.stale-kind.already-done');
      expect(closed[0].textContent).toContain('Completed by Henk.');
      expect(closed[0].querySelector('[role="status"]')!.textContent).toContain('proposal.closed');
      expect(button(fixture, 'proposal.approve')).toBeUndefined();

      button(fixture, 'proposal.dismiss')!.click();
      fixture.detectChanges();
      expect(element.querySelector('li.card')).toBeNull();
      expect(element.textContent).toContain('inbox.empty');
    }));

    it('keeps a proposal that a newer one replaced as a closed card next to its successor', fakeAsync(() => {
      const older = proposal('pending', 'approval-1');
      const newer = proposal('pending', 'approval-2');
      api.getInbox.and.returnValues(of({ items: [older], total: 1 }), of({ items: [newer], total: 1 }));
      api.lookupProposals.and.returnValue(of(lookedUp(
        { ...older, status: 'withdrawn', supersededByApprovalId: 'approval-2', executionResultCode: 'assistant-proposal-superseded' }
      )));
      const fixture = TestBed.createComponent(NhAssistantAgentInboxComponent);
      fixture.detectChanges();

      live.agentWork.next(work('conversation-1', 'proposal'));
      tick(300);
      fixture.detectChanges();

      const cards = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll<HTMLElement>('li.card'));
      expect(api.lookupProposals).toHaveBeenCalledOnceWith(['approval-1']);
      expect(cards.length).toBe(2);
      expect(cards[0].classList).toContain('closed');
      expect(cards[0].textContent).toContain('nh-assistant.approval.superseded');
      expect(cards[1].classList).not.toContain('closed');

      button(fixture, 'proposal.show-successor')!.click();
      expect(document.activeElement).toBe(cards[1]);
    }));

    it('shows why an approval was refused because the work was already done', () => {
      const pending = proposal('pending');
      api.getInbox.and.returnValues(of({ items: [pending], total: 1 }), of({ items: [], total: 0 }));
      api.decide.and.returnValue(throwError(() =>
        new NhAssistantApiError(409, 'assistant-proposal-stale', 'nh-assistant.errors.assistant-proposal-stale')));
      api.lookupProposals.and.returnValue(of(lookedUp(
        { ...pending, status: 'stale', staleKind: 'already-done', staleExplanation: 'Completed by Henk.' }
      )));
      const fixture = TestBed.createComponent(NhAssistantAgentInboxComponent);
      fixture.detectChanges();

      button(fixture, 'proposal.approve')!.click();
      fixture.detectChanges();

      const element = fixture.nativeElement as HTMLElement;
      expect(element.querySelector('[role="alert"]')!.textContent).toContain('assistant-proposal-stale');
      const card = element.querySelector<HTMLElement>('li.card.closed')!;
      expect(card.textContent).toContain('Completed by Henk.');
      expect(button(fixture, 'proposal.approve')).toBeUndefined();
    });

    it('keeps the list in place, without an error, when a live reload fails', fakeAsync(() => {
      api.getInbox.and.returnValues(of({ items: [proposal('pending')], total: 1 }), throwError(() => new Error('offline')));
      const fixture = TestBed.createComponent(NhAssistantAgentInboxComponent);
      fixture.detectChanges();

      live.agentWork.next(work('conversation-1', 'proposal'));
      tick(300);
      fixture.detectChanges();

      const element = fixture.nativeElement as HTMLElement;
      expect(api.getInbox).toHaveBeenCalledTimes(2);
      expect(element.querySelectorAll('li.card').length).toBe(1);
      expect(element.querySelector('[role="alert"]')).toBeNull();
    }));

    it('leaves the reload to its own decision in flight and keeps the feedback question afterwards', fakeAsync(() => {
      const pending = proposal('pending');
      const deciding = new Subject<AgentProposal>();
      api.getInbox.and.returnValues(of({ items: [pending], total: 1 }), of({ items: [], total: 0 }), of({ items: [], total: 0 }));
      api.decide.and.returnValue(deciding);
      const fixture = TestBed.createComponent(NhAssistantAgentInboxComponent);
      fixture.detectChanges();

      button(fixture, 'proposal.reject')!.click();
      live.agentWork.next(work('conversation-1', 'proposal'));
      tick(300);
      expect(api.getInbox).toHaveBeenCalledTimes(1);

      deciding.next({ ...pending, status: 'rejected', canGiveFeedback: true });
      deciding.complete();
      fixture.detectChanges();
      expect(api.getInbox).toHaveBeenCalledTimes(2);

      live.agentWork.next(work('conversation-1', 'run'));
      tick(300);
      fixture.detectChanges();

      expect(api.getInbox).toHaveBeenCalledTimes(3);
      expect((fixture.nativeElement as HTMLElement).querySelector('.feedback-prompt nh-assistant-proposal-feedback')).not.toBeNull();
    }));
  });

  describe('nh-assistant-run-timeline', () => {
    function run(id: string, kind?: AgentRun['kind']): AgentRun {
      return {
        id,
        conversationId: 'conversation-1',
        triggerId: 'project-tasks-completed',
        status: 'completed',
        failureClass: null,
        failureCode: null,
        initiatorActorId: null,
        accountableOwnerId: 'agent:project-closer',
        attempt: 1,
        agentVersion: 1,
        toolCalls: 1,
        inputTokens: 0,
        outputTokens: 0,
        proposalCount: 1,
        summary: 'Proposed to complete the project.',
        shadow: false,
        createdAt: '2026-10-14T10:00:00Z',
        startedAt: '2026-10-14T10:00:01Z',
        endedAt: '2026-10-14T10:00:02Z',
        backgroundOperationId: null,
        ...(kind ? { kind } : {})
      };
    }

    it('tells runs of the agent\'s rules from runs of the model', () => {
      api.getRuns.and.returnValue(of({
        items: [run('run-1', 'rule'), run('run-2', 'rule-then-model'), run('run-3', 'model'), run('run-4')],
        total: 4
      }));
      const fixture = TestBed.createComponent(NhAssistantRunTimelineComponent);
      fixture.componentRef.setInput('conversationId', 'conversation-1');
      fixture.detectChanges();

      const headers = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('li.card .card-header'))
        .map(header => header.textContent ?? '');
      expect(headers[0]).toContain('nh-assistant.agent-runs.run.kind.rule');
      expect(headers[1]).toContain('nh-assistant.agent-runs.run.kind.rule-then-model');
      // Model runs, also from servers without kinds, carry no extra badge.
      expect(headers[2]).not.toContain('run.kind');
      expect(headers[3]).not.toContain('run.kind');
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

    it('reloads its own thread after live agent work, not after work on other threads', fakeAsync(() => {
      api.getThread.and.returnValue(of(lookup(null)));
      api.getProposals.and.returnValue(of([proposal('pending')]));
      const fixture = TestBed.createComponent(ThreadHostComponent);
      fixture.detectChanges();
      expect(api.getThread).toHaveBeenCalledTimes(1);
      expect(api.getProposals).toHaveBeenCalledTimes(1);

      live.agentWork.next(work('conversation-2', 'proposal'));
      tick(300);
      expect(api.getThread).toHaveBeenCalledTimes(1);

      live.agentWork.next(work('conversation-1', 'run'));
      tick(100);
      live.agentWork.next(work('conversation-1', 'feedback'));
      tick(300);
      fixture.detectChanges();

      expect(api.getThread).toHaveBeenCalledTimes(2);
      expect(api.getProposals).toHaveBeenCalledTimes(2);
      expect((fixture.nativeElement as HTMLElement).querySelector('[role="status"].state')).toBeNull();
      expect(button(fixture, 'agent-runs.refresh')!.disabled).toBeFalse();

      live.resync.next();
      tick(300);
      expect(api.getThread).toHaveBeenCalledTimes(3);
    }));

    it('finds the thread that a run started on its subject', fakeAsync(() => {
      api.getThread.and.returnValues(of({ thread: null, canStart: true, role: 'approver' }), of(lookup(null)));
      api.getProposals.and.returnValue(of([]));
      const fixture = TestBed.createComponent(ThreadHostComponent);
      fixture.detectChanges();
      const element = fixture.nativeElement as HTMLElement;
      expect(element.textContent).toContain('thread.no-thread');

      live.agentWork.next(work('conversation-1', 'proposal'));
      tick(300);
      expect(api.getThread).toHaveBeenCalledTimes(1);

      live.agentWork.next(work('conversation-1', 'run'));
      tick(300);
      fixture.detectChanges();

      expect(api.getThread).toHaveBeenCalledTimes(2);
      expect(element.textContent).not.toContain('thread.no-thread');
    }));

    it('reloads only the thread while its own decision is in flight', fakeAsync(() => {
      const pending = proposal('pending');
      const rejected: AgentProposal = { ...pending, status: 'rejected', canGiveFeedback: true };
      const deciding = new Subject<AgentProposal>();
      api.getThread.and.returnValue(of(lookup(null)));
      api.getProposals.and.returnValues(of([pending]), of([rejected]));
      api.decide.and.returnValue(deciding);
      const fixture = TestBed.createComponent(ThreadHostComponent);
      fixture.detectChanges();

      button(fixture, 'proposal.reject')!.click();
      live.agentWork.next(work('conversation-1', 'proposal'));
      tick(300);
      expect(api.getThread).toHaveBeenCalledTimes(2);
      expect(api.getProposals).toHaveBeenCalledTimes(1);

      deciding.next(rejected);
      deciding.complete();
      fixture.detectChanges();

      expect(api.getProposals).toHaveBeenCalledTimes(2);
      expect((fixture.nativeElement as HTMLElement).querySelector('.feedback-prompt nh-assistant-proposal-feedback')).not.toBeNull();
    }));

    it('keeps the feedback question and the proposals when a live reload fails', fakeAsync(() => {
      const pending = proposal('pending');
      const rejected: AgentProposal = { ...pending, status: 'rejected', canGiveFeedback: true };
      api.getThread.and.returnValue(of(lookup(null)));
      api.getProposals.and.returnValues(of([pending]), of([rejected]), throwError(() => new Error('offline')));
      api.decide.and.returnValue(of(rejected));
      const fixture = TestBed.createComponent(ThreadHostComponent);
      fixture.detectChanges();
      const element = fixture.nativeElement as HTMLElement;

      button(fixture, 'proposal.reject')!.click();
      fixture.detectChanges();
      expect(element.querySelector('.feedback-prompt nh-assistant-proposal-feedback')).not.toBeNull();

      live.agentWork.next(work('conversation-1', 'proposal'));
      tick(300);
      fixture.detectChanges();

      expect(api.getProposals).toHaveBeenCalledTimes(3);
      expect(element.querySelector('.feedback-prompt nh-assistant-proposal-feedback')).not.toBeNull();
      expect(element.querySelectorAll('li.card').length).toBe(1);
      expect(element.querySelector('[role="alert"]')).toBeNull();
    }));
  });

  describe('nh-assistant-subject-suggestions', () => {
    let panel: jasmine.SpyObj<NhAssistantPanelService>;

    function subjectWork(overrides: Partial<AgentSubjectWork> = {}): AgentSubjectWork {
      return {
        subjectType: 'project',
        subjectId: 'project-1',
        subjectDisplayName: 'Project One',
        role: 'approver',
        agents: [
          { agentId: 'intake', thread: thread(null), canStart: true },
          { agentId: 'advisor', thread: null, canStart: false }
        ],
        openProposals: [
          { proposal: proposal('pending', 'approval-1'), canDecide: true, reasonCode: null },
          { proposal: proposal('pending', 'approval-2'), canDecide: false, reasonCode: 'assistant-four-eyes-required' },
          { proposal: proposal('pending', 'approval-3'), canDecide: false, reasonCode: 'assistant-approval-policy-denied' }
        ],
        ...overrides
      };
    }

    function cards(fixture: ComponentFixture<unknown>): HTMLElement[] {
      return Array.from((fixture.nativeElement as HTMLElement).querySelectorAll<HTMLElement>('li.card'));
    }

    beforeEach(() => {
      panel = jasmine.createSpyObj<NhAssistantPanelService>('NhAssistantPanelService', ['openSubject', 'open']);
      TestBed.overrideProvider(NhAssistantPanelService, { useValue: panel });
      TestBed.overrideProvider(NhAssistantStore, {
        useValue: { enabled: signal(true), agents: signal([]), initialize: () => Promise.resolve() }
      });
    });

    it('shows the open proposals of every agent with the viewer\'s own right and says who decides otherwise', () => {
      api.getSubjectWork.and.returnValue(of(subjectWork()));
      const fixture = TestBed.createComponent(SuggestionsHostComponent);
      fixture.detectChanges();

      expect(api.getSubjectWork).toHaveBeenCalledOnceWith('project', 'project-1', 'intake');
      const [mine, fourEyes, policy] = cards(fixture);
      expect(mine.textContent).toContain('proposal.approve');
      expect(mine.querySelector('.waiting-for')).toBeNull();
      expect(fourEyes.textContent).not.toContain('proposal.approve');
      expect(fourEyes.querySelector('.waiting-for')?.textContent).toContain('approval.waiting-for-other-approver');
      expect(policy.textContent).not.toContain('proposal.approve');
      expect(policy.querySelector('.waiting-for')?.textContent).toContain('approval.waiting-for-approver');

      const agents = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('li.subject-agent'));
      expect(agents.map(agent => agent.querySelector('strong')?.textContent?.trim())).toEqual(['intake', 'advisor']);
      expect(agents[1].textContent).toContain('thread.no-thread');
    });

    it('opens the conversation with an agent about the subject and reports it', fakeAsync(() => {
      const opened: AgentThreadLookup = { thread: thread(null), canStart: true, role: 'approver' };
      api.getSubjectWork.and.returnValue(of(subjectWork()));
      panel.openSubject.and.resolveTo(opened);
      const fixture = TestBed.createComponent(SuggestionsHostComponent);
      fixture.detectChanges();

      button(fixture, 'thread.open-conversation')!.click();
      flushMicrotasks();
      fixture.detectChanges();

      expect(panel.openSubject).toHaveBeenCalledOnceWith('intake', { type: 'project', id: 'project-1' });
      expect(fixture.componentInstance.opened).toBe(opened);
      expect((fixture.nativeElement as HTMLElement).querySelector('[role="alert"]')).toBeNull();
    }));

    it('shows a refused conversation where the person asked', fakeAsync(() => {
      api.getSubjectWork.and.returnValue(of(subjectWork()));
      panel.openSubject.and.rejectWith(new NhAssistantApiError(
        403,
        'assistant-subject-access-denied',
        'nh-assistant.errors.assistant-subject-access-denied'));
      const fixture = TestBed.createComponent(SuggestionsHostComponent);
      fixture.detectChanges();

      button(fixture, 'thread.open-conversation')!.click();
      flushMicrotasks();
      fixture.detectChanges();

      expect(fixture.componentInstance.opened).toBeNull();
      expect((fixture.nativeElement as HTMLElement).querySelector('[role="alert"]')?.textContent)
        .toContain('assistant-subject-access-denied');
    }));

    it('lets a reader open only an existing conversation and a participant start the agent', () => {
      api.getSubjectWork.and.returnValue(of(subjectWork({
        role: 'reader',
        agents: [{ agentId: 'intake', thread: null, canStart: false }],
        openProposals: []
      })));
      const fixture = TestBed.createComponent(SuggestionsHostComponent);
      fixture.detectChanges();
      expect(button(fixture, 'thread.open-conversation')).toBeUndefined();
      expect(button(fixture, 'thread.start')).toBeUndefined();
      expect((fixture.nativeElement as HTMLElement).textContent).toContain('subject.empty');

      api.getSubjectWork.and.returnValue(of(subjectWork({
        role: 'participant',
        agents: [{ agentId: 'intake', thread: null, canStart: true }]
      })));
      api.startAgent.and.returnValue(of({
        triggerId: 'trigger-1',
        outcome: 'started',
        runId: 'run-1',
        conversationId: 'conversation-1',
        reasonCode: null
      }));
      button(fixture, 'agent-runs.refresh')!.click();
      fixture.detectChanges();
      button(fixture, 'thread.start')!.click();
      fixture.detectChanges();

      expect(api.startAgent).toHaveBeenCalledOnceWith({ agentId: 'intake', subjectType: 'project', subjectId: 'project-1' });
      expect(button(fixture, 'thread.open-conversation')).toBeDefined();
    });

    it('asks what was wrong after a rejection, also once the proposal left the list', () => {
      const pending = proposal('pending', 'approval-1');
      api.getSubjectWork.and.returnValues(
        of(subjectWork({ openProposals: [{ proposal: pending, canDecide: true, reasonCode: null }] })),
        of(subjectWork({ openProposals: [] })));
      api.decide.and.returnValue(of({ ...pending, status: 'rejected', canGiveFeedback: true }));
      const fixture = TestBed.createComponent(SuggestionsHostComponent);
      fixture.detectChanges();

      button(fixture, 'proposal.reject')!.click();
      fixture.detectChanges();

      const element = fixture.nativeElement as HTMLElement;
      expect(api.getSubjectWork).toHaveBeenCalledTimes(2);
      expect(cards(fixture).length).toBe(0);
      expect(element.textContent).toContain('subject.empty');
      expect(element.querySelector('.feedback-prompt nh-assistant-proposal-feedback')).not.toBeNull();
    });

    it('keeps an open proposal that closed as stale as a closed card with the reason', fakeAsync(() => {
      const pending = proposal('pending', 'approval-1');
      api.getSubjectWork.and.returnValues(
        of(subjectWork({ openProposals: [{ proposal: pending, canDecide: true, reasonCode: null }] })),
        of(subjectWork({ openProposals: [] })));
      api.lookupProposals.and.returnValue(of(lookedUp(
        { ...pending, status: 'stale', staleKind: 'not-applicable', staleExplanation: 'The project was archived.' }
      )));
      const fixture = TestBed.createComponent(SuggestionsHostComponent);
      fixture.detectChanges();

      live.agentWork.next(work('conversation-1', 'proposal'));
      tick(300);
      fixture.detectChanges();

      const [card] = cards(fixture);
      expect(api.getProposals).not.toHaveBeenCalled();
      expect(card.classList).toContain('closed');
      expect(card.textContent).toContain('nh-assistant.approval.stale-kind.not-applicable');
      expect(card.textContent).toContain('The project was archived.');
    }));

    it('reloads after live agent work on its own subject only', fakeAsync(() => {
      api.getSubjectWork.and.returnValue(of(subjectWork()));
      const fixture = TestBed.createComponent(SuggestionsHostComponent);
      fixture.detectChanges();

      live.agentWork.next({ ...work('conversation-9', 'proposal'), subjectId: 'project-2' });
      live.agentWork.next({ ...work('conversation-8', 'proposal'), subjectType: 'task' });
      tick(300);
      expect(api.getSubjectWork).toHaveBeenCalledTimes(1);

      live.agentWork.next(work('conversation-7', 'run'));
      tick(300);
      fixture.detectChanges();
      expect(api.getSubjectWork).toHaveBeenCalledTimes(2);
      expect(cards(fixture).length).toBe(3);
    }));

    it('explains when the work on the subject is not available and offers a retry for other failures', () => {
      api.getSubjectWork.and.returnValues(
        throwError(() => new NhAssistantApiError(404, 'assistant-subject-not-found', 'nh-assistant.errors.assistant-subject-not-found')),
        throwError(() => new Error('offline')));
      const fixture = TestBed.createComponent(SuggestionsHostComponent);
      fixture.detectChanges();

      const element = fixture.nativeElement as HTMLElement;
      expect(element.textContent).toContain('subject.unavailable');
      expect(button(fixture, 'agent-runs.retry')).toBeUndefined();

      button(fixture, 'agent-runs.refresh')!.click();
      fixture.detectChanges();
      expect(element.textContent).toContain('agent-runs.load-failed');
      expect(button(fixture, 'agent-runs.retry')).toBeDefined();
    });
  });
});
