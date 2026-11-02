import { AdminAgent, AgentAlertModel, AlertModelMode, AlertModelSwitch, AlertModelVersion } from '@newheap/platform-ai-chat';
import { NhAssistantMockResult, mockFailure as failure } from './nh-assistant-mock-admin-shared';
import { NhAssistantMockAlertModelKind } from './nh-assistant-mock.models';

const modes: readonly AlertModelMode[] = ['off', 'shadow', 'live'];
const maxReason = 500;

/** What the switches of the mock read about agents. */
export interface NhAssistantMockAlertModelHost {
  find(agentId: string): AdminAgent | undefined;
}

/**
 * The switches of the mock API that let an agent's model raise alerts of a kind (FB87-9):
 * `GET admin/agents/{id}/alert-model`, `PUT admin/agents/{id}/alert-kinds/{kindId}/model` and its
 * versions. The kinds per agent come from the scenario (`admin.alertModel`); they can be switched for an
 * agent that runs on its own, live only from shadow, with the expected version, as the server does. The
 * mock has no templates to change, so a switch never reads as changed.
 */
export class NhAssistantMockAlertModel {
  private readonly kinds = new Map<string, AlertModelSwitch[]>();
  private readonly versions = new Map<string, AlertModelVersion[]>();

  constructor(
    scenario: Record<string, NhAssistantMockAlertModelKind[]> | undefined,
    private readonly host: NhAssistantMockAlertModelHost,
    private readonly now: () => string
  ) {
    for (const [agentId, kinds] of Object.entries(scenario ?? {})) {
      this.kinds.set(agentId, kinds.map(kind => ({
        displayNameKey: `nh-assistant.alert-kinds.${kind.kindId}.name`,
        mode: 'off',
        version: 0,
        contractChanged: false,
        kindEnabled: true,
        changedBy: null,
        changedAt: null,
        reason: null,
        ...structuredClone(kind)
      })));
    }
  }

  /** Routes `admin/agents/{id}/...`; `null` for a route that is not one of the switches. */
  handle(method: string, agentId: string, rest: string[], body: unknown): NhAssistantMockResult | null {
    if (rest.length === 1 && rest[0] === 'alert-model') {
      return method === 'GET' ? this.list(agentId) : failure('assistant-not-found');
    }
    if (rest.length === 3 && rest[0] === 'alert-kinds' && rest[2] === 'model') {
      return method === 'PUT' ? this.set(agentId, rest[1], body) : failure('assistant-not-found');
    }
    if (rest.length === 4 && rest[0] === 'alert-kinds' && rest[2] === 'model' && rest[3] === 'versions') {
      return method === 'GET' ? { status: 200, body: [...(this.versions.get(key(agentId, rest[1])) ?? [])].reverse() } : failure('assistant-not-found');
    }
    return null;
  }

  private list(agentId: string): NhAssistantMockResult {
    const agent = this.host.find(agentId);
    if (!agent) {
      return failure('assistant-agent-not-found');
    }

    const body: AgentAlertModel = {
      agentId,
      applies: doesOwnWork(agent),
      kinds: [...(this.kinds.get(agentId) ?? [])].sort((left, right) => left.kindId.localeCompare(right.kindId))
    };
    return { status: 200, body };
  }

  private set(agentId: string, kindId: string, body: unknown): NhAssistantMockResult {
    const request = (body ?? {}) as { mode?: unknown; expectedVersion?: unknown; reason?: unknown };
    if (typeof request.mode !== 'string' || !modes.includes(request.mode as AlertModelMode)) {
      return failure('assistant-validation', { mode: [request.mode === undefined || request.mode === null ? 'required' : 'invalid'] });
    }
    const reason = typeof request.reason === 'string' && request.reason.trim().length > 0 ? request.reason.trim() : null;
    if (reason !== null && reason.length > maxReason) {
      return failure('assistant-validation', { reason: ['too-long'] });
    }
    const agent = this.host.find(agentId);
    if (!agent) {
      return failure('assistant-agent-not-found');
    }
    const kinds = this.kinds.get(agentId) ?? [];
    const kind = kinds.find(item => item.kindId === kindId);
    if (!kind || !doesOwnWork(agent)) {
      return failure('assistant-alert-model-not-allowed');
    }

    const expected = typeof request.expectedVersion === 'number' ? request.expectedVersion : 0;
    if (expected !== kind.version) {
      return failure('assistant-version-conflict');
    }
    const mode = request.mode as AlertModelMode;
    if (kind.mode === mode && kind.version > 0) {
      return { status: 200, body: kind };
    }
    if (mode === 'live' && kind.mode !== 'shadow') {
      return failure('assistant-alert-model-shadow-first');
    }

    const changed: AlertModelSwitch = {
      ...kind,
      mode,
      version: kind.version + 1,
      contractChanged: false,
      changedBy: 'current-user',
      changedAt: this.now(),
      reason
    };
    this.kinds.set(agentId, kinds.map(item => item.kindId === kindId ? changed : item));
    const history = this.versions.get(key(agentId, kindId)) ?? [];
    history.push({ version: changed.version, mode, changedBy: 'current-user', changedAt: changed.changedAt!, reason, contractHash: 'mock-template' });
    this.versions.set(key(agentId, kindId), history);
    return { status: 200, body: changed };
  }
}

/** Whether the agent runs on its own and is no shadow variant, where a switch can apply. */
function doesOwnWork(agent: AdminAgent): boolean {
  const runs = agent.source === 'admin' || (agent.shadow !== null && agent.shadow !== undefined);
  return runs && agent.chatOnly !== true && !agent.shadowOf;
}

function key(agentId: string, kindId: string): string {
  return `${agentId}\u0000${kindId}`;
}
