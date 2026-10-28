import { AgentCompositionActivity } from '@newheap/platform-ai-chat';
import { NhAssistantMockResult, mockFailure as failure } from './nh-assistant-mock-admin-shared';

/**
 * `GET admin/agents/{id}/compositions` of the mock API: one composition that chained three reads and
 * one that stopped at a change that waits for a decision, so the activity shows its tool calls in order,
 * the rounds they saved and the size of the result the model received.
 */
export function mockCompositionActivity(method: string, agentId: string, query: URLSearchParams): NhAssistantMockResult {
  if (method !== 'GET') {
    return failure('assistant-not-found');
  }

  const to = query.get('to') ?? new Date().toISOString();
  const from = query.get('from') ?? new Date(Date.parse(to) - 30 * 24 * 60 * 60 * 1000).toISOString();
  const startedAt = new Date(Date.parse(to) - 60 * 60 * 1000).toISOString();
  const body: AgentCompositionActivity = {
    agentId,
    from,
    to,
    total: 2,
    truncated: false,
    tools: [
      {
        toolId: 'projects.flag-open-risks',
        kind: 'composition',
        calls: 1,
        toolCalls: 2,
        roundsSaved: 1,
        averageResultCharacters: 512,
        maxResultCharacters: 512
      },
      {
        toolId: 'projects.overview',
        kind: 'composition',
        calls: 1,
        toolCalls: 3,
        roundsSaved: 2,
        averageResultCharacters: 1840,
        maxResultCharacters: 1840
      }
    ],
    calls: [
      {
        invocationId: '0b9c3f51-8ad3-4a3e-9d43-8e1b7c1f0a02',
        conversationId: '6f1e2a3b-4c5d-4e6f-8a9b-0c1d2e3f4a5b',
        toolId: 'projects.flag-open-risks',
        kind: 'composition',
        status: 'succeeded',
        resultCode: null,
        startedAt,
        steps: [
          { index: 0, toolId: 'projects.list-risks', status: 'succeeded', resultCode: null },
          { index: 1, toolId: 'projects.flag-risk', status: 'awaiting-approval', resultCode: null }
        ],
        roundsSaved: 1,
        resultCharacters: 512
      },
      {
        invocationId: '0b9c3f51-8ad3-4a3e-9d43-8e1b7c1f0a01',
        conversationId: '6f1e2a3b-4c5d-4e6f-8a9b-0c1d2e3f4a5b',
        toolId: 'projects.overview',
        kind: 'composition',
        status: 'succeeded',
        resultCode: null,
        startedAt,
        steps: [
          { index: 0, toolId: 'projects.get', status: 'succeeded', resultCode: null },
          { index: 1, toolId: 'projects.list-risks', status: 'succeeded', resultCode: null },
          { index: 2, toolId: 'projects.get-people', status: 'succeeded', resultCode: null }
        ],
        roundsSaved: 2,
        resultCharacters: 1840
      }
    ]
  };
  return { status: 200, body };
}
