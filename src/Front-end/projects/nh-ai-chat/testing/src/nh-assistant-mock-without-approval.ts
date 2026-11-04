import {
  AdminAgent,
  AgentWithoutApproval,
  ToolCatalogEntry,
  WithoutApprovalMode,
  WithoutApprovalSwitch,
  WithoutApprovalVersion
} from '@newheap/platform-ai-chat';
import { NhAssistantMockResult, mockFailure as failure } from './nh-assistant-mock-admin-shared';

const modes: readonly WithoutApprovalMode[] = ['off', 'shadow', 'live'];
const maxReason = 500;

/** What the switches of the mock read about agents and tools. */
export interface NhAssistantMockWithoutApprovalHost {
  find(agentId: string): AdminAgent | undefined;
  tools(): ToolCatalogEntry[];
  /** The refusal of changes of an agent's rights by a caller who may not make them; `null` when they may go ahead. */
  rightsRefusal(changes: readonly string[]): NhAssistantMockResult | null;
}

/**
 * The switches of the mock API that let an agent run a tool without approval (FB82):
 * `GET admin/agents/{id}/without-approval`, `PUT admin/agents/{id}/tools/{toolId}/without-approval` and
 * its versions. A tool of the catalog with `allowWithoutApproval` within the agent's selectors can be
 * switched for an agent that runs on its own; live only from shadow, with the expected version, as the
 * server does. The mock has no tool contracts, so a switch never reads as changed.
 */
export class NhAssistantMockWithoutApproval {
  private readonly versions = new Map<string, WithoutApprovalVersion[]>();

  constructor(private readonly host: NhAssistantMockWithoutApprovalHost, private readonly now: () => string) {}

  /** Routes `admin/agents/{id}/...`; `null` for a route that is not one of the switches. */
  handle(method: string, agentId: string, rest: string[], body: unknown): NhAssistantMockResult | null {
    if (rest.length === 1 && rest[0] === 'without-approval') {
      return method === 'GET' ? this.list(agentId) : failure('assistant-not-found');
    }
    if (rest.length === 3 && rest[0] === 'tools' && rest[2] === 'without-approval') {
      return method === 'PUT' ? this.set(agentId, rest[1], body) : failure('assistant-not-found');
    }
    if (rest.length === 4 && rest[0] === 'tools' && rest[2] === 'without-approval' && rest[3] === 'versions') {
      return method === 'GET' ? { status: 200, body: [...(this.versions.get(key(agentId, rest[1])) ?? [])].reverse() } : failure('assistant-not-found');
    }
    return null;
  }

  private list(agentId: string): NhAssistantMockResult {
    const agent = this.host.find(agentId);
    if (!agent) {
      return failure('assistant-agent-not-found');
    }

    const body: AgentWithoutApproval = {
      agentId,
      applies: doesOwnWork(agent),
      tools: this.allowedTools(agent).map(tool => this.switchOf(agentId, tool))
    };
    return { status: 200, body };
  }

  private set(agentId: string, toolId: string, body: unknown): NhAssistantMockResult {
    const request = (body ?? {}) as { mode?: unknown; expectedVersion?: unknown; reason?: unknown };
    if (typeof request.mode !== 'string' || !modes.includes(request.mode as WithoutApprovalMode)) {
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
    const tool = this.allowedTools(agent).find(item => item.id === toolId);
    if (!tool || !doesOwnWork(agent)) {
      return failure('assistant-without-approval-not-allowed');
    }

    const history = this.versions.get(key(agentId, toolId)) ?? [];
    const current = history.at(-1);
    const expected = typeof request.expectedVersion === 'number' ? request.expectedVersion : 0;
    if (expected !== (current?.version ?? 0)) {
      return failure('assistant-version-conflict');
    }
    const mode = request.mode as WithoutApprovalMode;
    if (current?.mode === mode) {
      return { status: 200, body: this.switchOf(agentId, tool) };
    }
    if (mode === 'live' && current?.mode !== 'shadow') {
      return failure('assistant-without-approval-shadow-first');
    }
    // Going live is what lets the tool run without a person; off and shadow stay open.
    const rightsRefused = mode === 'live' ? this.host.rightsRefusal(['mode']) : null;
    if (rightsRefused) {
      return rightsRefused;
    }

    history.push({
      version: (current?.version ?? 0) + 1,
      mode,
      changedBy: 'current-user',
      changedAt: this.now(),
      reason,
      contractHash: 'mock-contract'
    });
    this.versions.set(key(agentId, toolId), history);
    return { status: 200, body: this.switchOf(agentId, tool) };
  }

  private allowedTools(agent: AdminAgent): ToolCatalogEntry[] {
    return this.host.tools()
      .filter(tool => tool.allowWithoutApproval === true && agent.toolSelectors.some(selector => matches(selector, tool.id)))
      .sort((left, right) => left.id.localeCompare(right.id));
  }

  private switchOf(agentId: string, tool: ToolCatalogEntry): WithoutApprovalSwitch {
    const current = this.versions.get(key(agentId, tool.id))?.at(-1);
    return {
      toolId: tool.id,
      description: tool.description,
      effect: tool.effect === 'mutation' ? 'mutation' : 'idempotent-mutation',
      mode: current?.mode ?? 'off',
      version: current?.version ?? 0,
      contractChanged: false,
      changedBy: current?.changedBy ?? null,
      changedAt: current?.changedAt ?? null,
      reason: current?.reason ?? null
    };
  }
}

/** Whether the agent runs on its own and is no shadow variant, where a switch can apply. */
function doesOwnWork(agent: AdminAgent): boolean {
  const runs = agent.source === 'admin' || (agent.shadow !== null && agent.shadow !== undefined);
  return runs && agent.chatOnly !== true && !agent.shadowOf;
}

function matches(selector: string, toolId: string): boolean {
  return selector === toolId || (selector.endsWith('.*') && toolId.startsWith(selector.slice(0, -1)));
}

function key(agentId: string, toolId: string): string {
  return `${agentId}\u0000${toolId}`;
}
