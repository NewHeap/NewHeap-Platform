import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { NEVER, Subject, firstValueFrom, of } from 'rxjs';
import {
  NhAssistantAgentInboxComponent,
  NhAssistantAgentProposalsComponent,
  NhAssistantRunResultComponent
} from '../../../agent-runs/src/public-api';
import {
  AgentInbox,
  AgentInboxGroup,
  AgentProposal,
  AgentProposalDecisionRight
} from '../models/assistant-agent-run.models';
import { LiveAgentWorkChanged } from '../models/assistant-live.models';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAgentRunApiService } from '../services/nh-assistant-agent-run-api.service';
import { NhAssistantLiveService, NhAssistantLiveState } from '../services/nh-assistant-live.service';

/**
 * The overview of the approval inbox: its division, run and grouping inputs, groups per parent and
 * subject with "Select group" for the proposals shown, what is new since the person's last visit, and
 * deciding the proposals of one run together in its result; with the mock API that serves them as the
 * server does.
 */
function proposal(id: string, overrides: Partial<AgentProposal> = {}): AgentProposal {
  return {
    approvalId: id,
    proposalId: `proposal-${id}`,
    proposalHash: `hash-${id}`,
    conversationId: `thread-${id}`,
    runId: 'run-1',
    agentId: 'planner',
    subjectType: 'project-task',
    subjectId: `task-${id}`,
    subjectDisplayName: `Task ${id}`,
    subjectTypeName: 'Task',
    toolId: 'tasks.complete',
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

const apollo = { subjectType: 'project', subjectId: 'apollo', displayName: 'Apollo', subjectTypeName: 'Project', subjectLink: '/projects/apollo' };

function group(subjectId: string, label: string, count: number, total: number, subjects: AgentInboxGroup['subjects'], flags: Partial<AgentInboxGroup> = {}): AgentInboxGroup {
  return {
    subjectType: 'project',
    subjectId,
    displayName: label,
    subjectTypeName: 'Project',
    subjectLink: `/projects/${subjectId}`,
    count,
    total,
    continuesFromPreviousPage: false,
    continuesOnNextPage: false,
    subjects,
    ...flags
  };
}

function subject(subjectType: string, subjectId: string, count: number, total = count): AgentInboxGroup['subjects'][number] {
  return { subjectType, subjectId, count, total, continuesFromPreviousPage: false, continuesOnNextPage: false };
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
  template: `
    <nh-assistant-agent-inbox
      [divisionId]="divisionId()"
      [runId]="runId()"
      [grouped]="grouped()"
      [groupByRun]="groupByRun()"
      [markNew]="markNew()"
      [recordVisit]="recordVisit()"
      [itemsPerPage]="2" />
  `
})
class InboxHostComponent {
  readonly divisionId = signal<string | null>(null);
  readonly runId = signal<string | null>(null);
  readonly grouped = signal(false);
  readonly groupByRun = signal(false);
  readonly markNew = signal(true);
  readonly recordVisit = signal(true);
}

@Component({
  standalone: true,
  imports: [NhAssistantAgentProposalsComponent],
  template: `
    <nh-assistant-agent-proposals
      [proposals]="proposals()"
      [groups]="groups()"
      [decisionRights]="rights()"
      [selectable]="true" />
  `
})
class GroupedProposalsHostComponent {
  readonly proposals = signal<AgentProposal[]>([]);
  readonly groups = signal<AgentInboxGroup[] | null>(null);
  readonly rights = signal<ReadonlyMap<string, AgentProposalDecisionRight> | null>(null);
}

function text(element: Element | null | undefined): string {
  return element?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

function cardIds(element: HTMLElement): string[] {
  return Array.from(element.querySelectorAll<HTMLElement>('li.card')).map(card => card.dataset['approvalId'] ?? '');
}

describe('the inbox overview', () => {
  describe('with the API', () => {
    let api: jasmine.SpyObj<NhAssistantAgentRunApiService>;
    let live: FakeLiveService;

    beforeEach(() => {
      live = new FakeLiveService();
      api = jasmine.createSpyObj<NhAssistantAgentRunApiService>('NhAssistantAgentRunApiService', [
        'getInbox',
        'visitInbox',
        'decide',
        'decideMany',
        'lookupProposals'
      ]);
      api.visitInbox.and.returnValue(of({ scopeKey: 'global', visitedAt: '2026-10-21T12:00:00Z' }));
      TestBed.configureTestingModule({
        providers: [
          provideTranslateService(),
          provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token', translations: 'host' }),
          { provide: NhAssistantAgentRunApiService, useValue: api },
          { provide: NhAssistantLiveService, useValue: live }
        ]
      });
    });

    it('filters on the division and the run and groups on request, and reloads from the first page when one changes', () => {
      api.getInbox.and.returnValue(of({ items: [proposal('a'), proposal('b')], total: 6 }));
      const fixture = TestBed.createComponent(InboxHostComponent);
      fixture.detectChanges();
      expect(api.getInbox).toHaveBeenCalledOnceWith(jasmine.objectContaining({ page: 1, itemsPerPage: 2 }));
      expect(api.getInbox.calls.mostRecent().args[0]).not.toEqual(jasmine.objectContaining({ grouped: jasmine.anything() }));

      const inbox = fixture.debugElement.children[0].componentInstance as NhAssistantAgentInboxComponent;
      inbox.goTo(2);
      expect(api.getInbox.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({ page: 2 }));

      fixture.componentInstance.divisionId.set('8d5c6f7e-1a2b-4c3d-9e8f-0a1b2c3d4e5f');
      fixture.detectChanges();
      expect(api.getInbox.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({
        divisionId: '8d5c6f7e-1a2b-4c3d-9e8f-0a1b2c3d4e5f',
        page: 1
      }));
      expect(inbox.page()).toBe(1);

      inbox.goTo(3);
      fixture.componentInstance.runId.set('run-7');
      fixture.componentInstance.grouped.set(true);
      fixture.detectChanges();
      expect(api.getInbox.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({ runId: 'run-7', grouped: true, page: 1 }));
    });

    it('shows a header per group and per subject, how many it shows of the group, and that a group goes on', () => {
      const own = proposal('own', { subjectType: 'project', subjectId: 'apollo', subjectDisplayName: 'Apollo', subjectTypeName: 'Project' });
      const first = proposal('first', { parent: apollo, runId: 'run-1' });
      // The second proposal is on the same task as the first, from another run.
      const second = proposal('second', { parent: apollo, subjectId: 'task-first', subjectDisplayName: 'Task first', runId: 'run-2' });
      const inbox: AgentInbox = {
        items: [own, first, second],
        total: 7,
        groups: [group('apollo', 'Apollo', 3, 5, [subject('project', 'apollo', 1), subject('project-task', 'task-first', 2, 3)], { continuesOnNextPage: true })]
      };
      api.getInbox.and.returnValue(of(inbox));
      const fixture = TestBed.createComponent(InboxHostComponent);
      fixture.componentInstance.grouped.set(true);
      fixture.componentInstance.groupByRun.set(true);
      fixture.detectChanges();

      const element = fixture.nativeElement as HTMLElement;
      const groups = Array.from(element.querySelectorAll('[data-proposal-group]'));
      expect(groups.length).toBe(1);
      expect(text(groups[0].querySelector('h4'))).toContain('Apollo');
      expect(text(groups[0].querySelector('[data-group-loaded]'))).toContain('inbox.group-loaded');
      expect(groups[0].querySelector('[data-group-continues]')).not.toBeNull();
      expect(groups[0].querySelector('[data-group-continued]')).toBeNull();
      // The group's own subject stands directly under the group; another subject has its own header.
      const subjects = Array.from(groups[0].querySelectorAll('[data-proposal-subject]'));
      expect(subjects.map(item => item.getAttribute('data-proposal-subject'))).toEqual(['project-task|task-first']);
      expect(text(subjects[0])).toContain('Task first');
      // One header per run within the subject.
      expect(Array.from(groups[0].querySelectorAll('[data-proposal-run]')).map(item => item.getAttribute('data-proposal-run')))
        .toEqual(['run-1', 'run-1', 'run-2']);
      expect(cardIds(element)).toEqual(['own', 'first', 'second']);
    });

    it('selects with "Select group" only the shown pending proposals of the group that the person may decide', () => {
      api.decideMany.and.returnValue(of({ results: [] }));
      const fixture = TestBed.createComponent(GroupedProposalsHostComponent);
      const items = [
        proposal('one', { parent: apollo }),
        proposal('two', { parent: apollo }),
        proposal('other', { parent: apollo }),
        proposal('elsewhere', { subjectType: 'project', subjectId: 'gemini', subjectDisplayName: 'Gemini' })
      ];
      fixture.componentInstance.proposals.set(items);
      fixture.componentInstance.groups.set([
        group('apollo', 'Apollo', 3, 7, [subject('project-task', 'task-one', 1), subject('project-task', 'task-two', 1), subject('project-task', 'task-other', 1)]),
        group('gemini', 'Gemini', 1, 1, [subject('project', 'gemini', 1)])
      ]);
      fixture.componentInstance.rights.set(new Map([
        ['one', { canDecide: true, reasonCode: null }],
        ['two', { canDecide: true, reasonCode: null }],
        ['other', { canDecide: false, reasonCode: 'assistant-four-eyes-required' }],
        ['elsewhere', { canDecide: true, reasonCode: null }]
      ]));
      fixture.detectChanges();

      const element = fixture.nativeElement as HTMLElement;
      const selectGroup = element.querySelector<HTMLInputElement>('[data-proposal-group="project|apollo"] [data-select-group]')!;
      selectGroup.click();
      fixture.detectChanges();

      const list = fixture.debugElement.children[0].componentInstance as NhAssistantAgentProposalsComponent;
      expect([...list.selected()].sort()).toEqual(['one', 'two']);
      expect(selectGroup.checked).toBeTrue();

      list.decideSelected('approve');
      expect(api.decideMany).toHaveBeenCalledOnceWith(['one', 'two'], 'approve');

      // Selecting again after deselecting the group leaves the other groups alone.
      list.toggle(items[3]);
      selectGroup.click();
      selectGroup.click();
      fixture.detectChanges();
      expect([...list.selected()].sort()).toEqual(['elsewhere']);
    });

    it('marks what came since the last visit as new, records the visit once and marks what arrives while shown', fakeAsync(() => {
      const seen = proposal('seen', { createdAt: '2026-10-20T08:00:00Z' });
      const fresh = proposal('fresh', { createdAt: '2026-10-21T09:00:00Z' });
      const arrived = proposal('arrived', { createdAt: '2026-10-21T12:00:01Z' });
      api.getInbox.and.returnValues(
        of({ items: [seen, fresh], total: 2, scopeKey: 'global', lastVisitedAt: '2026-10-21T08:00:00Z', newApprovalIds: ['fresh'] }),
        of({ items: [seen, fresh, arrived], total: 3, scopeKey: 'global', lastVisitedAt: '2026-10-21T12:00:00Z', newApprovalIds: ['arrived'] }));
      const fixture = TestBed.createComponent(InboxHostComponent);
      fixture.detectChanges();

      const element = fixture.nativeElement as HTMLElement;
      expect(Array.from(element.querySelectorAll('li.card.new')).map(card => card.getAttribute('data-approval-id'))).toEqual(['fresh']);
      expect(element.querySelector('[data-inbox-last-visit]')).not.toBeNull();
      expect(api.visitInbox).toHaveBeenCalledOnceWith(null);

      // A proposal that arrives while the inbox is shown is new; what was new when it opened stays new.
      live.agentWork.next({ conversationId: 'thread-arrived', reason: 'proposal', approvalId: 'arrived', runId: 'run-1', subjectType: 'project-task', subjectId: 'task-arrived' });
      tick(300);
      fixture.detectChanges();
      expect(Array.from(element.querySelectorAll('li.card.new')).map(card => card.getAttribute('data-approval-id'))).toEqual(['fresh', 'arrived']);
      expect(element.querySelectorAll('[data-proposal-new]').length).toBe(2);
      expect(api.visitInbox).toHaveBeenCalledTimes(1);
    }));

    it('neither marks nor records a visit when the host turns it off, and asks the server for nothing new', () => {
      api.getInbox.and.returnValue(of({ items: [proposal('fresh')], total: 1, scopeKey: 'global', lastVisitedAt: null, newApprovalIds: ['fresh'] }));
      const fixture = TestBed.createComponent(InboxHostComponent);
      fixture.componentInstance.markNew.set(false);
      fixture.componentInstance.recordVisit.set(false);
      fixture.detectChanges();

      const element = fixture.nativeElement as HTMLElement;
      expect(element.querySelector('li.card.new')).toBeNull();
      expect(element.querySelector('[data-inbox-first-visit]')).toBeNull();
      expect(api.visitInbox).not.toHaveBeenCalled();
    });
  });

  describe('with the mock API', () => {
    const division = '8d5c6f7e-1a2b-4c3d-9e8f-0a1b2c3d4e5f';

    function scenario(): NhAssistantMockScenario {
      return {
        agents: [{ id: 'projects', version: 1, displayNameKey: 'Project assistant', descriptionKey: 'Helps.', canMutate: true }],
        turns: [],
        divisions: [{ id: division, name: 'North' }, { id: 'b2000000-0000-0000-0000-0000000000b2', name: 'South', readable: false }],
        subjectTypes: [{
          id: 'project',
          names: { en: 'Project' },
          subjects: [{ id: 'apollo', displayName: 'Apollo', role: 'approver' }]
        }],
        runs: [{ id: 'run-1', agentId: 'projects', subjectType: 'project', subjectId: 'apollo', result: 'Proposed three changes.', createdAt: '2026-10-20T10:00:00Z' }],
        proposals: [
          { approvalId: 'own', agentId: 'projects', subjectType: 'project', subjectId: 'apollo', subjectDisplayName: 'Apollo', toolId: 'projects.change', runId: 'run-1', divisionId: division, expiresAt: '2026-10-23T09:00:00Z' },
          { approvalId: 'task-a', agentId: 'projects', subjectType: 'project-task', subjectId: 'a', subjectDisplayName: 'Task A', toolId: 'tasks.complete', runId: 'run-1', parent: apollo, divisionId: division, expiresAt: '2026-10-23T08:00:00Z' },
          { approvalId: 'task-b', agentId: 'projects', subjectType: 'project-task', subjectId: 'b', subjectDisplayName: 'Task B', toolId: 'tasks.complete', runId: 'run-1', parent: apollo, canDecide: false, reasonCode: 'assistant-four-eyes-required' },
          { approvalId: 'gemini', agentId: 'projects', subjectType: 'project', subjectId: 'gemini', subjectDisplayName: 'Gemini', toolId: 'projects.change', runId: 'run-2', expiresAt: '2026-10-22T09:00:00Z' }
        ],
        admin: {
          agents: [{
            source: 'code', id: 'projects', displayName: 'Project assistant', description: 'Helps.', instructions: 'Code.',
            toolSelectors: [], mcpServerIds: [], requiredPolicy: null, autonomy: 'propose', isEnabled: true,
            worksOnSubjectTypes: ['project'], worksOnAllSubjects: false, worksOnApplication: false
          }]
        }
      };
    }

    async function configure(): Promise<void> {
      TestBed.configureTestingModule({
        providers: [
          provideTranslateService({ fallbackLang: 'en' }),
          provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
          provideNhAssistantMockApi(scenario())
        ]
      });
      await firstValueFrom(TestBed.inject(TranslateService).use('en'));
    }

    async function call(method: string, path: string): Promise<{ status: number; body: any }> {
      const response = await TestBed.inject(NhAssistantMockBackend).fetch(`/api/assistant/${path}`, { method });
      const body = await response.text();
      return { status: response.status, body: body ? JSON.parse(body) : null };
    }

    async function settle(fixture: ComponentFixture<unknown>): Promise<HTMLElement> {
      for (let round = 0; round < 6; round++) {
        fixture.detectChanges();
        await new Promise(resolve => setTimeout(resolve));
      }
      fixture.detectChanges();
      return fixture.nativeElement as HTMLElement;
    }

    it('serves the grouped inbox, its division filter and its visits as the server does', async () => {
      await configure();

      const grouped = (await call('GET', 'approvals/inbox?grouped=true&itemsPerPage=2')).body;
      expect(grouped.items.map((item: AgentProposal) => item.approvalId)).toEqual(['gemini', 'own']);
      expect(grouped.groups.map((item: AgentInboxGroup) => [item.subjectId, item.count, item.total, item.continuesOnNextPage]))
        .toEqual([['gemini', 1, 1, false], ['apollo', 1, 2, true]]);
      const next = (await call('GET', 'approvals/inbox?grouped=true&itemsPerPage=2&page=2')).body;
      expect(next.items.map((item: AgentProposal) => item.approvalId)).toEqual(['task-a']);
      expect(next.groups[0].continuesFromPreviousPage).toBeTrue();
      expect(next.items[0].parent.displayName).toBe('Apollo');
      const north = (await call('GET', `approvals/inbox?divisionId=${division}`)).body;
      expect(north.items.map((item: AgentProposal) => item.approvalId)).toEqual(['task-a', 'own']);
      expect(north.scopeKey).toBe(`division:${division.replaceAll('-', '')}`);

      expect(north.newApprovalIds.length).toBe(2);
      expect((await call('POST', `approvals/inbox/visit?divisionId=${division}`)).status).toBe(200);
      expect((await call('GET', `approvals/inbox?divisionId=${division}`)).body.newApprovalIds).toEqual([]);
      expect((await call('POST', 'approvals/inbox/visit?divisionId=b2000000-0000-0000-0000-0000000000b2')).body.code).toBe('assistant-subject-not-found');
    });

    it('lets the reader of a run result select and decide the run\'s proposals they may decide', async () => {
      await configure();
      const fixture = TestBed.createComponent(NhAssistantRunResultComponent);
      fixture.componentRef.setInput('runId', 'run-1');
      const element = await settle(fixture);

      // A checkbox for each proposal the reader may decide; the other one says who decides.
      const boxes = Array.from(element.querySelectorAll<HTMLInputElement>('li.card input[type="checkbox"]'));
      expect(boxes.length).toBe(2);
      expect(element.querySelector('[data-approval-id="task-b"] .waiting-for')).not.toBeNull();
      const toolbar = element.querySelector('nh-assistant-agent-proposals .toolbar')!;
      toolbar.querySelector<HTMLInputElement>('label.select input')!.click();
      fixture.detectChanges();
      const approve = toolbar.querySelector<HTMLButtonElement>('button.primary')!;
      approve.click();
      await settle(fixture);

      const decided = (await call('GET', 'runs/run-1')).body.proposals as AgentProposal[];
      expect(decided.map(item => [item.approvalId, item.status])).toEqual([['own', 'executed'], ['task-a', 'executed'], ['task-b', 'pending']]);
      expect(element.querySelectorAll('li.card input[type="checkbox"]').length).toBe(0);
    });
  });
});
