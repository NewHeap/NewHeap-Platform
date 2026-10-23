import {
  AdminAgent,
  AgentSummary,
  ApplicationContext,
  ApplicationContextVersion,
  AssistantPreferences,
  McpConnectionStatus,
  NhAssistantMcpAuthorizationOutcome,
  ToolCatalogEntry
} from '@newheap/platform-ai-chat';
import { NhAssistantMockAdminAgent, NhAssistantMockRemoteTool, NhAssistantMockScenario } from './nh-assistant-mock.models';
import { NhAssistantMockAccounts } from './nh-assistant-mock-accounts';
import { NhAssistantMockAdminAgents } from './nh-assistant-mock-admin-agents';
import { NhAssistantMockAdminMcp } from './nh-assistant-mock-admin-mcp';
import {
  NhAssistantMockResult,
  mockFailure as failure,
  mockHashText as hashText,
  mockMaxInstructions as maxInstructions,
  mockValidationFailure as validationFailure
} from './nh-assistant-mock-admin-shared';
import { NhAssistantMockRunConfiguration } from './nh-assistant-mock-run-configuration';
import { mockSystemSubjectType } from './nh-assistant-mock-works-on';

export type { NhAssistantMockResult } from './nh-assistant-mock-admin-shared';

const styles = ['default', 'direct', 'personal', 'detailed'];
const addressForms = ['informal', 'formal'];
const responseLengths = ['short', 'normal', 'long'];
const maxCustomInstructions = 1_000;

const defaultPreferences: AssistantPreferences = {
  style: 'default',
  addressForm: 'informal',
  responseLength: 'normal',
  customInstructions: null
};

/**
 * In-memory administration and preference endpoints of the mock API. Routes `admin/*` to its
 * areas: the agents (`nh-assistant-mock-admin-agents`), their run configuration
 * (`nh-assistant-mock-run-configuration`), the MCP servers and the caller's connections
 * (`nh-assistant-mock-admin-mcp`) and the accounts (`nh-assistant-mock-accounts`), and keeps the
 * application context and the caller's preferences itself. Follows the contract rules: secrets are
 * write-only, new MCP tools start disabled as mutations, a changed remote schema disables a tool,
 * and stale versions answer `409`.
 */
export class NhAssistantMockAdmin {
  private readonly agents: NhAssistantMockAdminAgents;
  private readonly mcp: NhAssistantMockAdminMcp;
  private readonly accounts: NhAssistantMockAccounts;
  private readonly runConfiguration: NhAssistantMockRunConfiguration;
  private readonly contextVersions: (ApplicationContextVersion & { text: string })[] = [];
  private preferences: AssistantPreferences;
  private canAdminister: boolean;

  constructor(private readonly scenario: NhAssistantMockScenario, private readonly now: () => string) {
    const admin = scenario.admin ?? {};
    this.canAdminister = admin.canAdminister ?? true;
    this.preferences = { ...defaultPreferences, ...scenario.preferences };

    this.contextVersions.push({ version: 1, hash: hashText(admin.context ?? ''), updatedAt: now(), updatedBy: null, text: admin.context ?? '' });

    this.agents = new NhAssistantMockAdminAgents(scenario, now, {
      hasServer: serverId => this.mcp.hasServer(serverId),
      agentPolicyNames: () => this.runConfiguration.agentPolicyNames,
      divisionsListed: () => this.runConfiguration.divisionsListed,
      profileNames: () => this.runConfiguration.profileNames,
      dataConsultations: agentId => this.runConfiguration.consultationsInEffect(agentId)
    });
    this.accounts = new NhAssistantMockAccounts(admin);
    this.runConfiguration = new NhAssistantMockRunConfiguration(
      admin,
      {
        find: agentId => this.agents.find(agentId),
        all: () => this.agents.all(),
        knownSubjectTypes: () => [...(scenario.subjectTypes ?? []).map(type => type.id), mockSystemSubjectType],
        setDataSkill: (agentId, skillId, description, startsOff) => this.agents.setDataSkill(agentId, skillId, description, startsOff)
      },
      now,
      this.accounts);
    this.mcp = new NhAssistantMockAdminMcp(scenario, now, {
      agents: () => this.agents.all(),
      chatAgentIds: () => this.agents.chatAgents().map(agent => agent.id),
      removeServer: serverId => this.agents.removeServer(serverId),
      canAdminister: () => this.canAdminister
    });
    this.presetRunConfigurations(admin.agents ?? []);
  }

  get administers(): boolean {
    return this.canAdminister;
  }

  setCanAdminister(value: boolean): void {
    this.canAdminister = value;
  }

  /** What the mock authorization window answers next. */
  setMcpAuthorizationOutcome(outcome: 'approve' | 'deny' | 'cancel'): void {
    this.mcp.setAuthorizationOutcome(outcome);
  }

  /**
   * Simulates what happens to a server's OAuth connections outside the application: the
   * authorization server refused a refresh (`reauthorization-required`), or the server named another
   * authorization server (`issuer-changed`).
   */
  setMcpConnectionStatus(serverId: string, status: Exclude<McpConnectionStatus, 'not-connected' | 'active'>): void {
    this.mcp.setConnectionStatus(serverId, status);
  }

  /**
   * Why the caller has to connect a server each person connects to before a turn can use it, or null
   * when the caller's connection is active.
   */
  connectionNeeded(serverId: string): { displayName: string; reason: 'not-connected' | 'reauthorization-required' | 'insufficient-scope' } | null {
    return this.mcp.connectionNeeded(serverId);
  }

  /**
   * Plays the callback of a started authorization as the authorization server and the callback page
   * would: the person approves, denies or closes the window.
   */
  authorizeFlow(flowId: string): NhAssistantMcpAuthorizationOutcome {
    return this.mcp.authorizeFlow(flowId);
  }

  /** Replaces the tool list of a simulated remote server; the next sync picks it up. */
  setRemoteTools(serverId: string, tools: NhAssistantMockRemoteTool[]): void {
    this.mcp.setRemoteTools(serverId, tools);
  }

  /** Chat agents: code agents keep their keys, admin agents carry literal text; stopped agents are left out. */
  chatAgents(): AgentSummary[] {
    return this.agents.chatAgents();
  }

  /** Every agent of the administration, also those kept out of the chat, for the agent runs. */
  allAgents(): AdminAgent[] {
    return this.agents.all();
  }

  handlePreferences(method: string, body: unknown): NhAssistantMockResult {
    if (method === 'GET') {
      return { status: 200, body: this.preferences };
    }
    if (method !== 'PUT') {
      return { status: 405 };
    }

    const input = body as Partial<AssistantPreferences> | undefined;
    const instructions = input?.customInstructions ?? null;
    if (!input || !styles.includes(input.style ?? '') || !addressForms.includes(input.addressForm ?? '') ||
      !responseLengths.includes(input.responseLength ?? '') ||
      (instructions !== null && (typeof instructions !== 'string' || instructions.length > maxCustomInstructions))) {
      return validationFailure();
    }

    this.preferences = {
      style: input.style!,
      addressForm: input.addressForm!,
      responseLength: input.responseLength!,
      customInstructions: instructions && instructions.trim().length > 0 ? instructions : null
    };
    return { status: 200, body: this.preferences };
  }

  /**
   * `GET mcp-connections` and `PUT mcp-connections/{serverId}`: the caller's consent to background
   * use of servers that run with the caller's own account. A consent holds for the server's address
   * and authentication as they were; a change shows it as not allowed again.
   */
  handleMcpConnections(method: string, segments: string[], body: unknown): NhAssistantMockResult {
    return this.mcp.handleConnections(method, segments, body);
  }

  handleAdmin(method: string, segments: string[], body: unknown, query = new URLSearchParams()): NhAssistantMockResult {
    if (!this.canAdminister) {
      return failure('assistant-forbidden');
    }

    const [, area, id, action, child] = segments;
    switch (area) {
      case 'context':
        return this.context(method, id, body);
      case 'tools':
        return method === 'GET' && segments.length === 2 ? { status: 200, body: this.toolCatalog() } : failure('assistant-not-found');
      case 'run-catalog':
        return segments.length === 2 ? this.runConfiguration.runCatalog(method) : failure('assistant-not-found');
      case 'accounts':
        return segments.length === 2 ? this.accounts.search(method, query) : failure('assistant-not-found');
      case 'agents':
        if (id !== undefined && action === 'feedback') {
          return this.agents.agentFeedback(method, id, child, segments.length);
        }
        if (id !== undefined && action === 'evaluation') {
          return this.agents.agentEvaluation(method, id, child, segments.length, query);
        }
        if (id !== undefined && (action === 'replay' || action === 'replays') && segments.length === 4) {
          return this.agents.agentReplay(method, id, action, body);
        }
        if (id !== undefined && action !== undefined) {
          const configured = this.runConfiguration.handle(method, id, segments.slice(3), body, query);
          if (configured) {
            return configured;
          }
        }
        if (id !== undefined && action === 'skills' && child !== undefined && segments[5] === 'state' && segments.length === 6) {
          return this.agents.skillStateEndpoint(method, id, child, body);
        }
        if (id !== undefined && action === 'skills' && child !== undefined && segments[5] === 'shadow' && segments.length === 6) {
          return this.agents.skillShadowEndpoint(method, id, child, body);
        }
        return this.agents.agentsEndpoint(method, id, action, body, segments.length);
      case 'mcp-servers':
        return this.mcp.serversEndpoint(method, id, action, child, body, segments.length);
      case 'mcp-auth-modes':
        return this.mcp.authModesEndpoint(method, segments.length);
      default:
        return failure('assistant-not-found');
    }
  }

  private context(method: string, sub: string | undefined, body: unknown): NhAssistantMockResult {
    const current = this.contextVersions[this.contextVersions.length - 1];
    if (sub === 'versions' && method === 'GET') {
      return { status: 200, body: [...this.contextVersions].reverse().map(({ text, ...version }) => version) };
    }
    if (sub !== undefined) {
      return failure('assistant-not-found');
    }
    if (method === 'GET') {
      return { status: 200, body: toContext(current) };
    }
    if (method !== 'PUT') {
      return { status: 405 };
    }

    const input = body as { text?: unknown; expectedVersion?: unknown } | undefined;
    if (typeof input?.text !== 'string' || typeof input.expectedVersion !== 'number') {
      return validationFailure();
    }
    if (input.text.length > maxInstructions) {
      return failure('assistant-instructions-too-long');
    }
    if (input.expectedVersion !== current.version) {
      return failure('assistant-version-conflict');
    }

    const next = { version: current.version + 1, hash: hashText(input.text), updatedAt: this.now(), updatedBy: 'current-user', text: input.text };
    this.contextVersions.push(next);
    return { status: 200, body: toContext(next) };
  }

  /**
   * Saves the run configuration the scenario gives agents created in the administration through the
   * mock's own endpoints, as an administrator would: the run settings, then each skill and trigger,
   * put live when the scenario says so, and then the consultations defined as data of every agent. A
   * part the mock refuses fails the scenario at once.
   */
  private presetRunConfigurations(agents: readonly NhAssistantMockAdminAgent[]): void {
    const query = new URLSearchParams();
    for (const agent of agents) {
      const preset = agent.runConfiguration;
      if (!preset) {
        continue;
      }
      if (agent.source !== 'admin') {
        throw new Error(`Mock agent '${agent.id}' runs as its code says; only an agent created in the administration has a run configuration as data.`);
      }

      presetStep(agent.id, 'run settings', this.runConfiguration.handle('PUT', agent.id, ['run-settings'], preset.runSettings, query));
      for (const { skillId, shadow, ...skill } of preset.skills ?? []) {
        presetStep(agent.id, `skill '${skillId}'`, this.runConfiguration.handle('PUT', agent.id, ['skills', skillId], skill, query));
        if (shadow === false) {
          presetStep(agent.id, `live skill '${skillId}'`, this.agents.skillShadowEndpoint('POST', agent.id, skillId, { mode: 'live' }));
        }
      }
      for (const { triggerId, shadow, ...trigger } of preset.triggers ?? []) {
        presetStep(agent.id, `trigger '${triggerId}'`, this.runConfiguration.handle('PUT', agent.id, ['triggers', triggerId], trigger, query));
        if (shadow === false) {
          presetStep(agent.id, `live trigger '${triggerId}'`, this.runConfiguration.handle('POST', agent.id, ['triggers', triggerId, 'shadow'], { mode: 'live' }, query));
        }
      }
    }
    for (const agent of agents) {
      for (const { subjectType, ...consultation } of agent.dataConsultations ?? []) {
        presetStep(agent.id, `consultation about '${subjectType}'`, this.runConfiguration.handle('PUT', agent.id, ['consultations', subjectType], consultation, query));
      }
    }
  }

  /**
   * The scenario's tools, with the object type they name as the server reports it, the generated tools
   * of the consultations defined as data in effect, read live as the server reads its catalogs, and
   * the enabled MCP tools.
   */
  private toolCatalog(): ToolCatalogEntry[] {
    return [
      ...(this.scenario.admin?.tools ?? []).map(tool => ({ ...tool, subjectType: tool.subjectType ?? null })),
      ...this.runConfiguration.consultationTools(),
      ...this.mcp.catalogEntries()
    ];
  }
}

/** Fails the scenario when the mock refused a part of a preset run configuration, with the mock's answer. */
function presetStep(agentId: string, part: string, result: NhAssistantMockResult | null): void {
  if (!result || result.status >= 300) {
    throw new Error(`The mock refused the ${part} of agent '${agentId}' in the scenario's run configuration: ${JSON.stringify(result?.body ?? null)}.`);
  }
}

function toContext(version: ApplicationContextVersion & { text: string }): ApplicationContext {
  return { text: version.text, version: version.version, hash: version.hash, updatedAt: version.updatedAt, updatedBy: version.updatedBy };
}
