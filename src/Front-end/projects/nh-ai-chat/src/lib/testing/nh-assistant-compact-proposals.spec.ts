import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideTranslateService } from '@ngx-translate/core';
import { NEVER, Subject, of } from 'rxjs';
import {
  NhAssistantAgentInboxComponent,
  NhAssistantAgentProposalsComponent
} from '../../../agent-runs/src/public-api';
import { AgentInbox, AgentInboxFilter, AgentProposal } from '../models/assistant-agent-run.models';
import { LiveAgentWorkChanged } from '../models/assistant-live.models';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAgentRunApiService } from '../services/nh-assistant-agent-run-api.service';
import { NhAssistantLiveService, NhAssistantLiveState } from '../services/nh-assistant-live.service';

/**
 * The proposals list for many proposals: a selection that follows the list (FB84), so "Approve
 * selected" only sends what is shown.
 */
function proposal(id: string, overrides: Partial<AgentProposal> = {}): AgentProposal {
  return {
    approvalId: id,
    proposalId: `proposal-${id}`,
    proposalHash: `hash-${id}`,
    conversationId: `thread-${id}`,
    runId: 'run-1',
    agentId: 'planner',
    subjectType: 'project',
    subjectId: `project-${id}`,
    subjectDisplayName: `Project ${id}`,
    subjectTypeName: 'Project',
    toolId: 'projects.complete',
    summary: 'Approval is required before this tool can run.',
    presentation: null,
    argumentsPreview: '{}',
    targets: [],
    status: 'pending',
    mode: 'deferred',
    policyId: null,
    grantSource: 'agent',
    createdAt: '2026-10-20T10:00:00Z',
    expiresAt: '2026-10-23T10:00:00Z',
    initiatorActorId: null,
    decidedByActorId: null,
    decidedAt: null,
    executionResultCode: null,
    executedAt: null,
    ...overrides
  };
}

/** A connected hub whose agent-work updates the test sends. */
class FakeLiveService {
  readonly agentWork = new Subject<LiveAgentWorkChanged>();
  readonly changed$ = NEVER;
  readonly read$ = NEVER;
  readonly removed$ = NEVER;
  readonly events$ = NEVER;
  readonly agentWork$ = this.agentWork.asObservable();
  readonly resync$ = NEVER;
  readonly state = signal<NhAssistantLiveState>('connected').asReadonly();

  connect(): Promise<void> {
    return Promise.resolve();
  }

  stop(): Promise<void> {
    return Promise.resolve();
  }
}

@Component({
  standalone: true,
  imports: [NhAssistantAgentInboxComponent],
  template: `<nh-assistant-agent-inbox [markNew]="false" [recordVisit]="false" [itemsPerPage]="2" />`
})
class InboxHostComponent {}

@Component({
  standalone: true,
  imports: [NhAssistantAgentProposalsComponent],
  template: `<nh-assistant-agent-proposals [proposals]="proposals()" [canDecide]="true" [selectable]="true" />`
})
class ProposalsHostComponent {
  readonly proposals = signal<AgentProposal[]>([]);
}

function button(fixture: ComponentFixture<unknown>, key: string): HTMLButtonElement {
  const match = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('button'))
    .find(item => item.textContent?.includes(key));
  if (!match) {
    throw new Error(`No button "${key}".`);
  }
  return match;
}

function checkbox(element: HTMLElement, approvalId: string): HTMLInputElement {
  return element.querySelector<HTMLInputElement>(`[data-approval-id="${approvalId}"] input[type="checkbox"]`)!;
}

function cardIds(element: HTMLElement): string[] {
  return Array.from(element.querySelectorAll<HTMLElement>('li[data-approval-id]')).map(card => card.dataset['approvalId'] ?? '');
}

describe('the proposals list for many proposals', () => {
  let api: jasmine.SpyObj<NhAssistantAgentRunApiService>;

  beforeEach(() => {
    api = jasmine.createSpyObj<NhAssistantAgentRunApiService>('NhAssistantAgentRunApiService', [
      'getInbox',
      'visitInbox',
      'decide',
      'decideMany',
      'lookupProposals'
    ]);
    TestBed.configureTestingModule({
      providers: [
        provideTranslateService(),
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token', translations: 'host' }),
        { provide: NhAssistantAgentRunApiService, useValue: api },
        { provide: NhAssistantLiveService, useValue: new FakeLiveService() }
      ]
    });
  });

  describe('a selection that follows the list', () => {
    it('keeps a selection through a refresh of the same page and drops it on another page, so only shown proposals are approved', () => {
      const pages: Record<number, AgentInbox> = {
        1: { items: [proposal('a'), proposal('b')], total: 4 },
        2: { items: [proposal('c'), proposal('d')], total: 4 }
      };
      api.getInbox.and.callFake((filter: AgentInboxFilter = {}) => of(pages[filter.page ?? 1]));
      api.decideMany.and.returnValue(of({ results: [] }));
      const fixture = TestBed.createComponent(InboxHostComponent);
      fixture.detectChanges();
      const element = fixture.nativeElement as HTMLElement;
      const list = fixture.debugElement.query(By.directive(NhAssistantAgentProposalsComponent))
        .componentInstance as NhAssistantAgentProposalsComponent;

      checkbox(element, 'a').click();
      fixture.detectChanges();

      // A refresh of the same page keeps what the person is working on.
      button(fixture, 'agent-runs.refresh').click();
      fixture.detectChanges();
      expect(api.getInbox).toHaveBeenCalledTimes(2);
      expect(checkbox(element, 'a').checked).toBeTrue();
      expect([...list.selected()]).toEqual(['a']);

      // Another page shows other proposals: nothing of page 1 stays selected.
      button(fixture, 'inbox.next').click();
      fixture.detectChanges();
      expect(cardIds(element)).toEqual(['c', 'd']);
      expect(list.selected().size).toBe(0);
      expect(button(fixture, 'inbox.approve-selected').disabled).toBeTrue();

      checkbox(element, 'c').click();
      fixture.detectChanges();
      button(fixture, 'inbox.approve-selected').click();
      fixture.detectChanges();

      expect(api.decideMany).toHaveBeenCalledOnceWith(['c'], 'approve');
    });

    it('drops a selected proposal the host no longer shows, also when it comes back later', () => {
      api.decideMany.and.returnValue(of({ results: [] }));
      const fixture = TestBed.createComponent(ProposalsHostComponent);
      fixture.componentInstance.proposals.set([proposal('a'), proposal('b')]);
      fixture.detectChanges();
      const element = fixture.nativeElement as HTMLElement;
      const list = fixture.debugElement.query(By.directive(NhAssistantAgentProposalsComponent))
        .componentInstance as NhAssistantAgentProposalsComponent;

      checkbox(element, 'a').click();
      checkbox(element, 'b').click();
      fixture.detectChanges();

      // A colleague decided "a": the list no longer shows it.
      fixture.componentInstance.proposals.set([proposal('b'), proposal('c')]);
      fixture.detectChanges();
      expect([...list.selected()]).toEqual(['b']);

      fixture.componentInstance.proposals.set([proposal('a'), proposal('b'), proposal('c')]);
      fixture.detectChanges();
      expect(checkbox(element, 'a').checked).toBeFalse();

      button(fixture, 'inbox.approve-selected').click();
      expect(api.decideMany).toHaveBeenCalledOnceWith(['b'], 'approve');
    });
  });
});
