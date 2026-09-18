import { ApprovalPart, Conversation, ToolCallPart } from '../models/assistant-api.models';
import { NhAssistantSseEvent } from '../models/assistant-sse.models';
import { applyNhAssistantApprovalDecision, applyNhAssistantEvent } from './nh-assistant-reducer';

const usage = { inputTokens: 120, outputTokens: 40, toolCalls: 2 };

const approval: ApprovalPart = {
  type: 'approval',
  approvalId: 'approval-1',
  proposalId: 'proposal-1',
  proposalHash: 'hash-1',
  toolId: 'sample-api.project.update-status',
  summary: 'Set project Alpha to On hold',
  argumentsPreview: '{"id":"p1","status":"on-hold"}',
  targets: ['Project Alpha'],
  expiresAt: '2026-09-18T12:00:00Z',
  status: 'pending'
};

/** One mutating turn exactly as contract section 6 streams it. */
const mutatingTurn: NhAssistantSseEvent[] = [
  { type: 'turn.started', data: { turnId: 't1', userMessageId: 'u1', assistantMessageId: 'a1' } },
  { type: 'message.delta', data: { messageId: 'a1', text: 'Looking up ' } },
  { type: 'message.delta', data: { messageId: 'a1', text: 'the project.' } },
  {
    type: 'tool.started',
    data: { invocationId: 'i1', toolId: 'sample-api.project.get-by-id', toolVersion: 1, displayName: 'Get project', argumentsPreview: '{"id":"p1"}' }
  },
  { type: 'tool.completed', data: { invocationId: 'i1', status: 'succeeded', resultCode: null, resultPreview: '{"name":"Alpha"}' } },
  {
    type: 'tool.started',
    data: { invocationId: 'i2', toolId: approval.toolId, toolVersion: 1, displayName: 'Update project status', argumentsPreview: approval.argumentsPreview }
  },
  { type: 'approval.required', data: approval },
  { type: 'turn.completed', data: { turnId: 't1', status: 'waiting-for-approval', usage, errorCode: null } }
];

function emptyConversation(): Conversation {
  return {
    id: 'c1',
    agentId: 'agent',
    agentVersion: 1,
    title: null,
    status: 'running',
    createdAt: '2026-09-18T10:00:00Z',
    updatedAt: '2026-09-18T10:00:00Z',
    pendingApproval: null,
    messages: [{ id: 'client-1', role: 'user', createdAt: '2026-09-18T10:00:00Z', parts: [{ type: 'text', text: 'Pause Alpha' }] }]
  };
}

function replay(conversation: Conversation, events: NhAssistantSseEvent[], clientMessageId?: string): Conversation {
  return events.reduce((current, event) => applyNhAssistantEvent(current, event, clientMessageId), conversation);
}

describe('applyNhAssistantEvent', () => {
  it('turns the fixture event sequence into the expected conversation state', () => {
    const result = replay(emptyConversation(), mutatingTurn, 'client-1');

    expect(result.status).toBe('waiting-for-approval');
    expect(result.pendingApproval).toEqual(approval);
    expect(result.messages.map(message => [message.id, message.role])).toEqual([['u1', 'user'], ['a1', 'assistant']]);

    const assistant = result.messages[1];
    expect(assistant.parts[0]).toEqual({ type: 'text', text: 'Looking up the project.' });
    expect(assistant.parts[1]).toEqual(jasmine.objectContaining<ToolCallPart>({
      invocationId: 'i1', status: 'succeeded', resultPreview: '{"name":"Alpha"}', displayName: 'Get project'
    }));
    expect(assistant.parts[2]).toEqual(jasmine.objectContaining<ToolCallPart>({ invocationId: 'i2', status: 'awaiting-approval' }));
    expect(assistant.parts[3]).toEqual(approval);
  });

  it('does not mutate the input conversation', () => {
    const conversation = emptyConversation();
    const snapshot = JSON.parse(JSON.stringify(conversation));

    replay(conversation, mutatingTurn, 'client-1');

    expect(conversation).toEqual(snapshot);
  });

  it('starts a new text part after a tool card', () => {
    const result = replay(emptyConversation(), [
      mutatingTurn[0],
      mutatingTurn[3],
      { type: 'message.delta', data: { messageId: 'a1', text: 'Done.' } }
    ], 'client-1');

    expect(result.messages[1].parts.map(part => part.type)).toEqual(['tool-call', 'text']);
  });

  it('records a failed tool call with its result code', () => {
    const result = replay(emptyConversation(), [
      mutatingTurn[0],
      mutatingTurn[3],
      { type: 'tool.completed', data: { invocationId: 'i1', status: 'failed', resultCode: 'api-bridge-forbidden', resultPreview: null } }
    ], 'client-1');

    expect(result.messages[1].parts[0]).toEqual(jasmine.objectContaining({ status: 'failed', resultCode: 'api-bridge-forbidden' }));
  });

  it('maps turn completion statuses to conversation statuses', () => {
    const base = replay(emptyConversation(), [mutatingTurn[0]], 'client-1');
    const complete = (status: 'completed' | 'cancelled' | 'failed') =>
      applyNhAssistantEvent(base, { type: 'turn.completed', data: { turnId: 't1', status, usage, errorCode: null } }).status;

    expect(complete('completed')).toBe('idle');
    expect(complete('cancelled')).toBe('idle');
    expect(complete('failed')).toBe('failed');
  });

  it('marks a running conversation failed on an error event', () => {
    const result = applyNhAssistantEvent(emptyConversation(), { type: 'error', data: { code: 'x', messageKey: 'y' } });

    expect(result.status).toBe('failed');
  });
});

describe('applyNhAssistantApprovalDecision', () => {
  it('approves: approval approved, tool running again and the resumed turn completes it', () => {
    const waiting = replay(emptyConversation(), mutatingTurn, 'client-1');

    const decided = applyNhAssistantApprovalDecision(waiting, 'approval-1', 'approve');
    const resumed = replay(decided, [
      { type: 'turn.started', data: { turnId: 't2', userMessageId: 'u1', assistantMessageId: 'a1' } },
      { type: 'tool.completed', data: { invocationId: 'i2', status: 'succeeded', resultCode: null, resultPreview: '{"status":"on-hold"}' } },
      { type: 'message.delta', data: { messageId: 'a1', text: 'Alpha is on hold.' } },
      { type: 'turn.completed', data: { turnId: 't2', status: 'completed', usage, errorCode: null } }
    ]);

    const parts = resumed.messages[1].parts;
    expect(decided.pendingApproval).toBeNull();
    expect((decided.messages[1].parts[2] as ToolCallPart).status).toBe('running');
    expect((parts[2] as ToolCallPart).status).toBe('succeeded');
    expect((parts[3] as ApprovalPart).status).toBe('approved');
    expect(parts[4]).toEqual({ type: 'text', text: 'Alpha is on hold.' });
    expect(resumed.messages.length).toBe(2);
    expect(resumed.status).toBe('idle');
  });

  it('rejects: approval rejected and the waiting tool call rejected', () => {
    const waiting = replay(emptyConversation(), mutatingTurn, 'client-1');

    const decided = applyNhAssistantApprovalDecision(waiting, 'approval-1', 'reject');

    const parts = decided.messages[1].parts;
    expect((parts[2] as ToolCallPart).status).toBe('rejected');
    expect((parts[3] as ApprovalPart).status).toBe('rejected');
    expect(decided.pendingApproval).toBeNull();
    expect(decided.status).toBe('running');
  });
});
