import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { AgentProposal } from '../models/assistant-agent-run.models';
import { NH_ASSISTANT_FETCH, NhAssistantFetch } from '../nh-assistant.config';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAgentRunApiService } from './nh-assistant-agent-run-api.service';
import { NhAssistantApiError } from './nh-assistant-api.service';

function jsonResponse(value: unknown, status = 200): Response {
  return new Response(JSON.stringify(value), { status, headers: { 'content-type': 'application/json' } });
}

const proposal: AgentProposal = {
  approvalId: 'approval 1',
  proposalId: 'proposal-1',
  proposalHash: 'hash-1',
  conversationId: 'thread-1',
  runId: 'run-1',
  agentId: 'intake',
  subjectType: 'project',
  subjectId: 'p-1',
  subjectDisplayName: 'PRJ-1',
  toolId: 'projects.change-status',
  summary: 'Activate the project.',
  presentation: null,
  argumentsPreview: '{}',
  targets: [],
  status: 'pending',
  mode: 'deferred',
  policyId: 'project-intake',
  grantSource: 'agent',
  createdAt: '2026-10-13T08:00:00Z',
  expiresAt: '2026-10-16T08:00:00Z',
  initiatorActorId: null,
  decidedByActorId: null,
  decidedAt: null,
  executionResultCode: null,
  executedAt: null
};

describe('NhAssistantAgentRunApiService', () => {
  let fetchSpy: jasmine.Spy<NhAssistantFetch>;
  let api: NhAssistantAgentRunApiService;

  beforeEach(() => {
    fetchSpy = jasmine.createSpy<NhAssistantFetch>('fetch');
    TestBed.configureTestingModule({
      providers: [
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'run-token' }),
        { provide: NH_ASSISTANT_FETCH, useFactory: () => fetchSpy }
      ]
    });
    api = TestBed.inject(NhAssistantAgentRunApiService);
  });

  function lastCall(): { url: string; method: string; headers: Record<string, string>; body: unknown } {
    const [url, init] = fetchSpy.calls.mostRecent().args;
    return {
      url,
      method: init.method ?? 'GET',
      headers: init.headers as Record<string, string>,
      body: typeof init.body === 'string' ? JSON.parse(init.body) : undefined
    };
  }

  it('calls every agent-run endpoint with the documented method and path', async () => {
    fetchSpy.and.callFake(() => Promise.resolve(jsonResponse({})));
    const calls: [() => Promise<unknown>, string, string][] = [
      [() => firstValueFrom(api.getThread('intake', 'project', 'p 1')), 'GET', 'threads?agentId=intake&subjectType=project&subjectId=p+1'],
      [() => firstValueFrom(api.startAgent({ agentId: 'intake', subjectType: 'project', subjectId: 'p-1' })), 'POST', 'threads/start'],
      [() => firstValueFrom(api.openThread('intake', 'project', 'p-1')), 'POST', 'threads/open'],
      [() => firstValueFrom(api.getSubjectWork('project', 'p 1')), 'GET', 'subjects/project/p%201/work'],
      [() => firstValueFrom(api.getSubjectWork('project', 'p-1', 'intake')), 'GET', 'subjects/project/p-1/work?agentId=intake'],
      [() => firstValueFrom(api.summarizeSubjects('project', ['p-1', 'p-2'])), 'POST', 'subjects/project/summary'],
      [() => firstValueFrom(api.searchSubjects('project', 'alpha one')), 'GET', 'subjects/project/search?query=alpha+one'],
      [() => firstValueFrom(api.searchSubjects('project', 'a&b', 5)), 'GET', 'subjects/project/search?query=a%26b&limit=5'],
      [() => firstValueFrom(api.getRuns('thread-1')), 'GET', 'threads/thread-1/runs'],
      [() => firstValueFrom(api.getProposals('thread-1')), 'GET', 'threads/thread-1/proposals'],
      [() => firstValueFrom(api.getProposal('approval 1')), 'GET', 'approvals/approval%201'],
      [() => firstValueFrom(api.lookupProposals(['a', 'b'])), 'POST', 'approvals/lookup'],
      [() => firstValueFrom(api.getConsultations('thread-1')), 'GET', 'threads/thread-1/consultations?page=1&itemsPerPage=20'],
      [() => firstValueFrom(api.getMemory('thread-1')), 'GET', 'threads/thread-1/memory'],
      [() => firstValueFrom(api.getArtifacts('thread-1')), 'GET', 'threads/thread-1/artifacts'],
      [() => firstValueFrom(api.cancelRun('run-1'), { defaultValue: undefined }), 'POST', 'runs/run-1/cancel'],
      [() => firstValueFrom(api.retryRun('run-1', 'resume')), 'POST', 'runs/run-1/retry'],
      [() => firstValueFrom(api.getInbox()), 'GET', 'approvals/inbox'],
      [() => firstValueFrom(api.getInbox({ subjectType: 'project', page: 2, itemsPerPage: 10 })), 'GET', 'approvals/inbox?subjectType=project&page=2&itemsPerPage=10'],
      [() => firstValueFrom(api.decide(proposal, 'approve')), 'POST', 'approvals/approval%201/decisions'],
      [() => firstValueFrom(api.decideMany(['a', 'b'], 'reject')), 'POST', 'approvals/decisions'],
      [() => firstValueFrom(api.resolve('approval-1', 'mark-resolved')), 'POST', 'approvals/approval-1/resolve'],
      [() => firstValueFrom(api.giveFeedback('approval 1', 'not-now')), 'PUT', 'approvals/approval%201/feedback'],
      [() => firstValueFrom(api.giveFeedbackMany(['a', 'b'], 'incorrect')), 'POST', 'approvals/feedback']
    ];

    for (const [call, method, path] of calls) {
      await call();
      const request = lastCall();
      expect(request.method).withContext(path).toBe(method);
      expect(request.url).withContext(path).toBe(`/api/assistant/${path}`);
      expect(request.headers['Authorization']).withContext(path).toBe('Bearer run-token');
    }
  });

  it('sends the subjects to summarize and the subject whose thread opens', async () => {
    fetchSpy.and.callFake(() => Promise.resolve(jsonResponse({})));

    await firstValueFrom(api.summarizeSubjects('project', ['p-1', 'p-2']));
    expect(lastCall().body).toEqual({ subjectIds: ['p-1', 'p-2'] });

    await firstValueFrom(api.openThread('intake', 'project', 'p-1'));
    expect(lastCall().body).toEqual({ agentId: 'intake', subjectType: 'project', subjectId: 'p-1' });

    await firstValueFrom(api.lookupProposals(['approval-1', 'approval-2']));
    expect(lastCall().body).toEqual({ approvalIds: ['approval-1', 'approval-2'] });
  });

  it('reports a subject without a role for the caller with its stable code', async () => {
    fetchSpy.and.callFake(() => Promise.resolve(jsonResponse(
      { code: 'assistant-subject-not-found', messageKey: 'nh-assistant.errors.assistant-subject-not-found' },
      404)));

    const failure = await firstValueFrom(api.getSubjectWork('project', 'p-1')).catch((error: unknown) => error);

    expect(failure).toEqual(jasmine.any(NhAssistantApiError));
    expect((failure as NhAssistantApiError).status).toBe(404);
    expect((failure as NhAssistantApiError).code).toBe('assistant-subject-not-found');
  });

  it('binds a decision to the proposal hash and sends a trimmed reason', async () => {
    fetchSpy.and.callFake(() => Promise.resolve(jsonResponse(proposal)));

    await firstValueFrom(api.decide(proposal, 'reject', '  not now  '));

    expect(lastCall().body).toEqual({ decision: 'reject', expectedProposalHash: 'hash-1', reason: 'not now' });
  });

  it('sends feedback with a category and a trimmed text, and null for an empty text', async () => {
    fetchSpy.and.callFake(() => Promise.resolve(jsonResponse({})));

    await firstValueFrom(api.giveFeedback('approval-1', 'wrong-value', '  The deadline is wrong.  '));
    expect(lastCall().body).toEqual({ category: 'wrong-value', text: 'The deadline is wrong.' });

    await firstValueFrom(api.giveFeedback('approval-1', 'not-now', '   '));
    expect(lastCall().body).toEqual({ category: 'not-now', text: null });

    await firstValueFrom(api.giveFeedbackMany(['approval-1', 'approval-2'], 'already-done'));
    expect(lastCall().body).toEqual({ approvalIds: ['approval-1', 'approval-2'], category: 'already-done', text: null });
  });

  it('reports refused feedback with its stable code', async () => {
    fetchSpy.and.callFake(() => Promise.resolve(jsonResponse(
      { code: 'assistant-feedback-not-accepted', messageKey: 'nh-assistant.errors.assistant-feedback-not-accepted' },
      409)));

    const failure = await firstValueFrom(api.giveFeedback('approval-1', 'other', 'x')).catch((error: unknown) => error);

    expect((failure as NhAssistantApiError).code).toBe('assistant-feedback-not-accepted');
    expect((failure as NhAssistantApiError).status).toBe(409);
  });

  it('reports a four-eyes refusal with its stable code', async () => {
    fetchSpy.and.callFake(() => Promise.resolve(jsonResponse(
      { code: 'assistant-four-eyes-required', messageKey: 'nh-assistant.errors.assistant-four-eyes-required' },
      403)));

    const failure = await firstValueFrom(api.decide(proposal, 'approve')).catch((error: unknown) => error);

    expect(failure).toEqual(jasmine.any(NhAssistantApiError));
    expect((failure as NhAssistantApiError).code).toBe('assistant-four-eyes-required');
  });
});
