import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { NEVER, firstValueFrom, of } from 'rxjs';
import { NhAssistantAgentInboxComponent } from '../../../agent-runs/src/public-api';
import { AgentInboxSort, AgentInboxSummary, AgentProposal } from '../models/assistant-agent-run.models';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAgentRunApiService } from '../services/nh-assistant-agent-run-api.service';
import { NhAssistantLiveService } from '../services/nh-assistant-live.service';

/**
 * The inbox by kind of action (FB78): the inbox's `toolId`, `expiresBefore` and `sort` inputs, and the
 * mock API's filters, sorts and summary, which count what the inbox lists as the server does.
 */
@Component({
  standalone: true,
  imports: [NhAssistantAgentInboxComponent],
  template: `
    <nh-assistant-agent-inbox
      [toolId]="toolId()"
      [expiresBefore]="expiresBefore()"
      [sort]="sort()"
      [recordVisit]="false"
      [itemsPerPage]="2" />
  `
})
class InboxHostComponent {
  readonly toolId = signal<string | readonly string[] | null>(null);
  readonly expiresBefore = signal<string | null>(null);
  readonly sort = signal<AgentInboxSort | null>(null);
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

describe('the inbox by kind of action', () => {
  it('asks for the kinds of action, the expiry and the order it is given, and reloads from the first page when one changes', () => {
    const api = jasmine.createSpyObj<NhAssistantAgentRunApiService>('NhAssistantAgentRunApiService', ['getInbox', 'visitInbox']);
    api.getInbox.and.returnValue(of({ items: [], total: 6 }));
    TestBed.configureTestingModule({
      providers: [
        provideTranslateService(),
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token', translations: 'host' }),
        { provide: NhAssistantAgentRunApiService, useValue: api },
        { provide: NhAssistantLiveService, useValue: new QuietLiveService() }
      ]
    });
    const fixture = TestBed.createComponent(InboxHostComponent);
    fixture.detectChanges();
    expect(api.getInbox.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({ toolId: undefined, expiresBefore: undefined, sort: undefined }));

    const inbox = fixture.debugElement.children[0].componentInstance as NhAssistantAgentInboxComponent;
    inbox.goTo(3);
    fixture.componentInstance.toolId.set(['projects.add-task', 'projects.close']);
    fixture.componentInstance.expiresBefore.set('2026-11-03T10:00:00Z');
    fixture.componentInstance.sort.set('createdAt');
    fixture.detectChanges();
    expect(api.getInbox.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({
      toolId: ['projects.add-task', 'projects.close'],
      expiresBefore: '2026-11-03T10:00:00Z',
      sort: 'createdAt',
      page: 1
    }));
    expect(inbox.page()).toBe(1);
    expect(api.visitInbox).not.toHaveBeenCalled();
  });

  describe('with the mock API', () => {
    // The mock follows the real clock; the proposals are made around now.
    const base = Date.now();
    const at = (minutes: number) => new Date(base + minutes * 60_000).toISOString();

    function scenario(): NhAssistantMockScenario {
      const named = (toolDisplayName: string) => ({ toolDisplayName, summary: 'Do it.', fields: [], notice: null });
      return {
        agents: [{ id: 'planner', version: 1, displayNameKey: 'Planner', descriptionKey: 'Plans.', canMutate: true }],
        turns: [],
        proposals: [
          { approvalId: 'a', agentId: 'planner', subjectType: 'project', subjectId: 'apollo', subjectTypeName: 'Project', toolId: 'projects.close', presentation: named('Close project'), createdAt: at(-10), expiresAt: at(60) },
          { approvalId: 'b', agentId: 'planner', subjectType: 'project-task', subjectId: 't1', subjectTypeName: 'Task', toolId: 'projects.add-task', presentation: named('Add task'), createdAt: at(-30), expiresAt: at(30 * 60) },
          { approvalId: 'c', agentId: 'planner', subjectType: 'project-task', subjectId: 't1', subjectTypeName: 'Task', toolId: 'projects.add-task', presentation: named('Add task'), createdAt: at(-5), expiresAt: at(120) },
          { approvalId: 'd', agentId: 'reviewer', subjectType: 'project', subjectId: 'gemini', subjectTypeName: 'Project', toolId: 'projects.add-task', createdAt: at(-20), expiresAt: at(180) },
          { approvalId: 'hidden', agentId: 'planner', subjectType: 'project', subjectId: 'vega', toolId: 'projects.archive', canDecide: false, reasonCode: 'assistant-approval-policy-denied' }
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

    async function call(path: string): Promise<{ status: number; body: any }> {
      const response = await TestBed.inject(NhAssistantMockBackend).fetch(`/api/assistant/${path}`, { method: 'GET' });
      const body = await response.text();
      return { status: response.status, body: body ? JSON.parse(body) : null };
    }

    function ids(body: { items: AgentProposal[] }): string[] {
      return body.items.map(item => item.approvalId);
    }

    it('filters on the kind of action and the expiry and sorts as the server does', async () => {
      await configure();

      expect(ids((await call('approvals/inbox')).body)).toEqual(['a', 'c', 'd', 'b']);
      expect(ids((await call('approvals/inbox?toolId=projects.add-task')).body)).toEqual(['c', 'd', 'b']);
      expect(ids((await call('approvals/inbox?toolId=projects.add-task&toolId=projects.close')).body)).toEqual(['a', 'c', 'd', 'b']);
      expect(ids((await call(`approvals/inbox?expiresBefore=${encodeURIComponent(at(24 * 60))}`)).body)).toEqual(['a', 'c', 'd']);
      expect(ids((await call('approvals/inbox?sort=createdAt')).body)).toEqual(['b', 'd', 'a', 'c']);
      expect((await call('approvals/inbox?sort=title')).body.code).toBe('assistant-message-invalid');
      expect((await call('approvals/inbox?toolId=%20')).status).toBe(400);
    });

    it('counts what the inbox lists per agent, kind of action and subject type, and what expires within a day', async () => {
      await configure();

      const summary = (await call('approvals/inbox/summary')).body as AgentInboxSummary;
      expect([summary.total, summary.expiringSoon, summary.status]).toEqual([4, 3, 'pending']);
      expect(Date.parse(summary.expiringBefore)).toBeGreaterThanOrEqual(base + 24 * 60 * 60_000);
      expect(summary.agents).toEqual([{ agentId: 'planner', count: 3 }, { agentId: 'reviewer', count: 1 }]);
      // A kind is named by its cards' presentation; the proposal the person may not decide is not listed.
      expect(summary.tools).toEqual([
        { toolId: 'projects.add-task', displayName: 'Add task', count: 3 },
        { toolId: 'projects.close', displayName: 'Close project', count: 1 }
      ]);
      expect(summary.subjectTypes).toEqual([
        { subjectType: 'project', subjectTypeName: 'Project', count: 2, subjects: 2 },
        { subjectType: 'project-task', subjectTypeName: 'Task', count: 2, subjects: 1 }
      ]);
      const reviewer = (await call('approvals/inbox/summary?agentId=reviewer')).body as AgentInboxSummary;
      expect(reviewer.tools).toEqual([{ toolId: 'projects.add-task', displayName: 'projects.add-task', count: 1 }]);
      expect((await call('approvals/inbox/summary?status=awaiting-application')).body.total).toBe(0);
    });
  });
});
