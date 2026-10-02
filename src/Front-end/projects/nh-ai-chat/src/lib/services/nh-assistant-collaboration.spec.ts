import { TestBed } from '@angular/core/testing';
import { NhAssistantStore, provideNhAssistant } from '@newheap/platform-ai-chat';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';

const colleague = { actorId: 'colleague-sam', displayName: 'Sam' };

const scenario: NhAssistantMockScenario = {
  agents: [{ id: 'projects', version: 1, displayNameKey: 'agents.projects', descriptionKey: 'agents.projects-description', canMutate: true }],
  timing: { firstEventDelayMs: 0, eventDelayMs: 5, chunkSize: 64 },
  collaboration: { currentUser: { actorId: 'me', displayName: 'Robin' }, directory: [colleague] },
  turns: [
    { match: 'slow', steps: [{ text: Array.from({ length: 40 }, (_, index) => `word${index}`).join(' ') }] },
    {
      match: 'approve',
      steps: [{
        approval: {
          toolId: 'sample-api.project.update-status',
          displayName: 'Update status',
          summary: 'Set Alpha on hold',
          argumentsPreview: '{}',
          targets: ['Project Alpha'],
          approved: [{ text: 'Done.' }],
          rejected: [{ text: 'Nothing changed.' }]
        }
      }]
    },
    { steps: [{ text: 'Quick answer.' }] }
  ]
};

async function until(condition: () => boolean, timeoutMs = 3_000): Promise<void> {
  const started = Date.now();
  while (!condition()) {
    if (Date.now() - started > timeoutMs) {
      throw new Error('The condition was not met in time.');
    }
    TestBed.tick();
    await new Promise(resolve => setTimeout(resolve, 5));
  }
}

describe('NhAssistantStore collaboration', () => {
  let store: NhAssistantStore;
  let backend: NhAssistantMockBackend;

  beforeEach(async () => {
    // The store counts the open conversation as seen only in a focused window.
    spyOn(document, 'hasFocus').and.returnValue(true);
    TestBed.configureTestingModule({
      providers: [
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
        provideNhAssistantMockApi(scenario)
      ]
    });
    store = TestBed.inject(NhAssistantStore);
    backend = TestBed.inject(NhAssistantMockBackend);
    await store.initialize();
    await store.refreshConversations();
  });

  it('runs several conversations at the same time and reports them as activity', async () => {
    expect(await store.send('slow report')).toBeTrue();
    const slowId = store.activeConversation()!.id;

    store.startNewConversation();
    expect(store.streaming()).toBeFalse();
    expect(store.canSend()).toBeTrue();
    expect(await store.send('quick question')).toBeTrue();
    const quickId = store.activeConversation()!.id;

    expect(quickId).not.toBe(slowId);
    expect(store.activity().find(item => item.id === slowId)?.status).toBe('running');
    await until(() => store.runningCount() === 0);

    // Nobody looked at the slow conversation while it answered.
    const slow = store.activity().find(item => item.id === slowId);
    expect(slow?.unread).toBeTrue();
    expect(store.conversations().find(item => item.id === slowId)?.status).toBe('idle');
  });

  it('marks the open conversation as read only while the user looks at it', async () => {
    await store.send('quick question');
    const conversationId = store.activeConversation()!.id;
    await until(() => store.activeConversation()?.status === 'idle');
    expect(store.unreadCount()).toBe(1);
    expect(backend.requests.some(request => request.path.endsWith('/read'))).toBeFalse();

    store.setViewing(true);
    await until(() => store.unreadCount() === 0);

    expect(backend.requests.filter(request => request.path === `conversations/${conversationId}/read`).length).toBe(1);
  });

  it('shows the turn of a colleague live, with their name, and blocks sending meanwhile', async () => {
    await store.send('quick question');
    const conversationId = store.activeConversation()!.id;
    await until(() => store.activeConversation()?.status === 'idle');

    const colleagueTurn = backend.simulateParticipantTurn(conversationId, colleague, 'slow team update');
    await until(() => store.activeMember()?.displayName === 'Sam');
    expect(store.canSend()).toBeFalse();
    await colleagueTurn;
    await until(() => store.activeConversation()?.status === 'idle' && store.activeMember() === null);

    const messages = store.activeConversation()!.messages;
    expect(messages.some(message => message.authorActorId === 'colleague-sam')).toBeTrue();
    expect(store.members().map(member => member.displayName)).toEqual(['Robin', 'Sam']);
    expect(store.canSend()).toBeTrue();
  });

  it('lets only the participant who started a turn decide its approval', async () => {
    await store.send('quick question');
    const conversationId = store.activeConversation()!.id;
    await until(() => store.activeConversation()?.status === 'idle');

    await backend.simulateParticipantTurn(conversationId, colleague, 'approve the change');
    await until(() => store.activeConversation()?.status === 'waiting-for-approval' && store.pendingApproval() !== null);

    expect(store.canDecide()).toBeFalse();
    store.decide('approve');
    expect(backend.requests.some(request => request.path.includes('/decide'))).toBeFalse();
  });

  it('creates, revokes and joins an invitation link', async () => {
    await store.send('quick question');
    const conversationId = store.activeConversation()!.id;
    await until(() => store.activeConversation()?.status === 'idle');

    const token = await store.createShareLink();
    expect(token).toBeTruthy();
    expect(store.activeConversation()!.shareToken).toBe(token);

    store.startNewConversation();
    expect(await store.joinConversation(conversationId, token!)).toBeTrue();
    expect(store.activeConversation()!.id).toBe(conversationId);

    expect(await store.revokeShareLink()).toBeTrue();
    expect(store.activeConversation()!.shareToken).toBeNull();
    store.startNewConversation();
    expect(await store.joinConversation(conversationId, token!)).toBeFalse();
    expect(store.error()?.code).toBe('assistant-share-link-invalid');
  });

  it('invites a colleague from the directory', async () => {
    await store.send('quick question');
    await until(() => store.activeConversation()?.status === 'idle');

    const candidates = await store.searchParticipantCandidates('sa');
    expect(candidates.map(candidate => candidate.actorId)).toEqual(['colleague-sam']);
    expect(await store.inviteParticipant('colleague-sam')).toBeTrue();

    expect(store.members().map(member => member.actorId)).toEqual(['me', 'colleague-sam']);
    expect(await store.searchParticipantCandidates('sa')).toEqual([]);
  });

  it('forgets a conversation that was deleted elsewhere', async () => {
    await store.send('quick question');
    const conversationId = store.activeConversation()!.id;
    await until(() => store.activeConversation()?.status === 'idle');

    await backend.fetch(`/api/assistant/conversations/${conversationId}`, { method: 'DELETE' });
    await until(() => store.activeConversation() === null);

    expect(store.conversations().some(item => item.id === conversationId)).toBeFalse();
    expect(store.error()?.code).toBe('assistant-conversation-removed');
  });
});
