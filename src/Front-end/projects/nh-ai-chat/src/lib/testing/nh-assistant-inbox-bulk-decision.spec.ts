import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { NEVER, firstValueFrom, of, throwError } from 'rxjs';
import { NhAssistantAgentInboxComponent } from '../../../agent-runs/src/public-api';
import { AgentBulkDecisionOverview, AgentBulkDecisionResult } from '../models/assistant-agent-run.models';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAgentRunApiService } from '../services/nh-assistant-agent-run-api.service';
import { NhAssistantApiError } from '../services/nh-assistant-transport';
import { NhAssistantLiveService } from '../services/nh-assistant-live.service';

/**
 * Deciding a whole filter of the inbox (FB80): "Decide all" with its overview of what happens, the
 * proposals decided one by one that stay, the tallies afterwards, and the mock API's overview, token,
 * repeat and versions as the server serves them.
 */
@Component({
  standalone: true,
  imports: [NhAssistantAgentInboxComponent],
  template: `<nh-assistant-agent-inbox [agentId]="agentId()" [toolId]="toolId()" [decideAll]="decideAll()" [recordVisit]="false" />`
})
class InboxHostComponent {
  readonly agentId = signal<string | undefined>(undefined);
  readonly toolId = signal<string | null>(null);
  readonly decideAll = signal(true);
}

/** A hub that never sends anything. */
class QuietLiveService {
  readonly changed$ = NEVER;
  readonly read$ = NEVER;
  readonly removed$ = NEVER;
  readonly events$ = NEVER;
  readonly agentWork$ = NEVER;
  readonly resync$ = NEVER;
  readonly state = signal('connected').asReadonly();

  connect(): Promise<void> {
    return Promise.resolve();
  }

  stop(): Promise<void> {
    return Promise.resolve();
  }
}

function text(element: Element | null | undefined): string {
  return element?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

describe('deciding the whole inbox at once', () => {
  describe('with the mock API', () => {
    const base = Date.now();
    const at = (minutes: number) => new Date(base + minutes * 60_000).toISOString();

    function scenario(): NhAssistantMockScenario {
      const named = (toolDisplayName: string) => ({ toolDisplayName, summary: 'Do it.', fields: [], notice: null });
      return {
        agents: [{ id: 'planner', version: 1, displayNameKey: 'Planner', descriptionKey: 'Plans.', canMutate: true }],
        turns: [],
        proposals: [
          { approvalId: 'a', agentId: 'planner', subjectType: 'project-task', subjectId: 't1', subjectTypeName: 'Task', toolId: 'projects.add-task', presentation: named('Add task'), expiresAt: at(60) },
          { approvalId: 'b', agentId: 'planner', subjectType: 'project-task', subjectId: 't1', subjectTypeName: 'Task', toolId: 'projects.add-task', presentation: named('Add task'), expiresAt: at(90) },
          { approvalId: 'c', agentId: 'planner', subjectType: 'project-task', subjectId: 't2', subjectTypeName: 'Task', toolId: 'projects.add-task', presentation: named('Add task'), expiresAt: at(120) },
          { approvalId: 'own', agentId: 'planner', subjectType: 'project-task', subjectId: 't3', subjectTypeName: 'Task', toolId: 'projects.add-task', presentation: named('Add task'), expiresAt: at(150), canDecide: true, initiatorActorId: 'someone' },
          { approvalId: 'mail', agentId: 'planner', subjectType: 'project', subjectId: 'apollo', subjectTypeName: 'Project', toolId: 'projects.notify-supplier', presentation: named('Notify supplier'), expiresAt: at(30), requireIndividualDecision: true }
        ]
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

    async function call(method: string, path: string, body?: unknown): Promise<{ status: number; body: any }> {
      const response = await TestBed.inject(NhAssistantMockBackend).fetch(`/api/assistant/${path}`, {
        method,
        headers: { 'Content-Type': 'application/json' },
        body: body === undefined ? undefined : JSON.stringify(body)
      });
      const content = await response.text();
      return { status: response.status, body: content ? JSON.parse(content) : null };
    }

    async function settle(fixture: ComponentFixture<unknown>): Promise<HTMLElement> {
      for (let round = 0; round < 6; round++) {
        fixture.detectChanges();
        await new Promise(resolve => setTimeout(resolve));
      }
      fixture.detectChanges();
      return fixture.nativeElement as HTMLElement;
    }

    it('serves the overview of a set, decides it once, reports a repeat and leaves what is decided one by one', async () => {
      await configure();

      const overview = (await call('POST', 'approvals/bulk-decisions/overview', {})).body as AgentBulkDecisionOverview;
      expect([overview.count, overview.subjects, overview.listed, overview.remaining, overview.limit]).toEqual([4, 3, 5, 0, 200]);
      expect(overview.kinds).toEqual([
        { toolId: 'projects.add-task', toolDisplayName: 'Add task', subjectType: 'project-task', subjectTypeName: 'Task', count: 4, subjects: 3 }
      ]);
      expect(overview.individual).toEqual({ count: 1, tools: [{ toolId: 'projects.notify-supplier', displayName: 'Notify supplier', count: 1 }] });
      expect(overview.token).toBeTruthy();

      // Deciding one of the set by hand first makes it stale for the set.
      expect((await call('POST', 'approvals/a/decisions', { decision: 'reject' })).status).toBe(200);
      const result = (await call('POST', 'approvals/bulk-decisions', { token: overview.token, decision: 'approve' })).body as AgentBulkDecisionResult;
      expect([result.decision, result.succeeded, result.refused, result.stale, result.repeated]).toEqual(['approve', 3, 0, 1, false]);
      expect(result.items.map(item => [item.approvalId, item.outcome])).toEqual([['a', 'stale'], ['b', 'succeeded'], ['c', 'succeeded'], ['own', 'succeeded']]);
      const repeated = (await call('POST', 'approvals/bulk-decisions', { token: overview.token, decision: 'reject' })).body as AgentBulkDecisionResult;
      expect([repeated.decision, repeated.succeeded, repeated.repeated]).toEqual(['approve', 3, true]);
      expect((await call('POST', 'approvals/bulk-decisions', { token: 'unknown', decision: 'approve' })).body.code).toBe('assistant-bulk-decision-not-found');
      expect((await call('POST', 'approvals/bulk-decisions', { decision: 'approve' })).body.code).toBe('assistant-approval-decision-invalid');

      // Deciding several by id checks the versions sent and refuses what is decided one by one.
      const many = (await call('POST', 'approvals/decisions', {
        approvalIds: ['mail'],
        decision: 'approve',
        expectedProposalHashes: { mail: 'sha256:mail' }
      })).body;
      expect(many.results[0].code).toBe('assistant-individual-decision-required');
      expect((await call('GET', 'approvals/mail')).body.proposal.status).toBe('pending');
      expect((await call('POST', 'approvals/decisions', { approvalIds: ['mail'], decision: 'reject', expectedProposalHashes: { mail: 'sha256:other' } })).body.results[0].code)
        .toBe('assistant-proposal-hash-mismatch');
    });

    it('offers "Decide all", names what happens before deciding, and shows the tallies afterwards', async () => {
      await configure();
      const fixture = TestBed.createComponent(InboxHostComponent);
      let element = await settle(fixture);

      const decideAll = element.querySelector<HTMLButtonElement>('[data-decide-all]')!;
      expect(text(decideAll)).toBe('Decide all 5');
      decideAll.click();
      element = await settle(fixture);

      const overview = element.querySelector('[data-bulk-overview]')!;
      expect(text(overview.querySelector('h4'))).toBe('Decide 4 proposals at once');
      expect(Array.from(overview.querySelectorAll('[data-bulk-kinds] li')).map(text)).toEqual(['4 times Add task, on 3 objects (Task)']);
      expect(text(overview.querySelector('[data-bulk-individual]')))
        .toBe('1 proposal stays in the list, because it is decided on its own: Notify supplier.');
      expect(element.querySelector('[data-decide-all]')).toBeNull();

      overview.querySelector<HTMLButtonElement>('[data-bulk-approve]')!.click();
      element = await settle(fixture);
      expect(element.querySelector('[data-bulk-overview]')).toBeNull();
      expect(text(element.querySelector('[data-bulk-result]'))).toBe('4 approved, 0 refused, 0 no longer as shown.');
      // The inbox reloads: only the proposal that is decided one by one is left.
      expect(Array.from(element.querySelectorAll<HTMLElement>('li.card')).map(card => card.dataset['approvalId'])).toEqual(['mail']);
      expect(element.querySelector('[data-decide-all]')).toBeNull();
    });
  });

  describe('with the API', () => {
    let api: jasmine.SpyObj<NhAssistantAgentRunApiService>;

    beforeEach(() => {
      api = jasmine.createSpyObj<NhAssistantAgentRunApiService>('NhAssistantAgentRunApiService', [
        'getInbox',
        'visitInbox',
        'getBulkDecisionOverview',
        'decideBulk'
      ]);
      api.getInbox.and.returnValue(of({ items: [], total: 12 }));
      TestBed.configureTestingModule({
        providers: [
          provideTranslateService(),
          provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token', translations: 'host' }),
          { provide: NhAssistantAgentRunApiService, useValue: api },
          { provide: NhAssistantLiveService, useValue: new QuietLiveService() }
        ]
      });
    });

    function overview(overrides: Partial<AgentBulkDecisionOverview> = {}): AgentBulkDecisionOverview {
      return {
        token: 'token-1',
        expiresAt: '2026-11-02T10:15:00Z',
        count: 10,
        subjects: 4,
        listed: 12,
        remaining: 0,
        limit: 200,
        kinds: [{ toolId: 'projects.add-task', toolDisplayName: 'Add task', subjectType: 'project', subjectTypeName: 'Project', count: 10, subjects: 4 }],
        tools: [],
        subjectTypes: [],
        individual: { count: 2, tools: [] },
        ...overrides
      };
    }

    it('asks for the overview of the inbox\'s own filter and decides only with its token and reason', () => {
      api.getBulkDecisionOverview.and.returnValue(of(overview()));
      api.decideBulk.and.returnValue(of({ token: 'token-1', decision: 'reject', succeeded: 9, refused: 1, stale: 0, repeated: false, items: [] }));
      const fixture = TestBed.createComponent(InboxHostComponent);
      fixture.componentInstance.agentId.set('planner');
      fixture.componentInstance.toolId.set('projects.add-task');
      fixture.detectChanges();
      const inbox = fixture.debugElement.children[0].componentInstance as NhAssistantAgentInboxComponent;

      inbox.openOverview();
      expect(api.getBulkDecisionOverview).toHaveBeenCalledOnceWith(jasmine.objectContaining({ agentId: 'planner', toolId: 'projects.add-task' }));
      inbox.onBulkReason({ target: { value: 'Not this quarter.' } } as unknown as Event);
      inbox.decideOverview('reject');
      expect(api.decideBulk).toHaveBeenCalledOnceWith('token-1', 'reject', 'Not this quarter.');
      expect(inbox.bulkResult()?.succeeded).toBe(9);
      expect(inbox.overview()).toBeNull();
    });

    it('keeps the overview with the reason of a refused decision, and closes it when the filter changes', () => {
      api.getBulkDecisionOverview.and.returnValue(of(overview({ remaining: 3 })));
      api.decideBulk.and.returnValue(throwError(() => new NhAssistantApiError(409, 'assistant-bulk-decision-expired', 'nh-assistant.errors.assistant-bulk-decision-expired')));
      const fixture = TestBed.createComponent(InboxHostComponent);
      fixture.detectChanges();
      const inbox = fixture.debugElement.children[0].componentInstance as NhAssistantAgentInboxComponent;
      const element = fixture.nativeElement as HTMLElement;

      inbox.openOverview();
      fixture.detectChanges();
      expect(element.querySelector('[data-bulk-remaining]')).not.toBeNull();
      inbox.decideOverview('approve');
      fixture.detectChanges();
      expect(inbox.bulkError()?.code).toBe('assistant-bulk-decision-expired');
      expect(element.querySelector('[data-bulk-overview] [role="alert"]')).not.toBeNull();

      fixture.componentInstance.toolId.set('projects.close');
      fixture.detectChanges();
      expect(inbox.overview()).toBeNull();
    });

    it('offers no "Decide all" when the host turns it off', () => {
      const fixture = TestBed.createComponent(InboxHostComponent);
      fixture.componentInstance.decideAll.set(false);
      fixture.detectChanges();
      expect((fixture.nativeElement as HTMLElement).querySelector('[data-decide-all]')).toBeNull();
    });
  });
});
