import { AdminAgent, AgentMetadata, AgentMetadataEntry, AgentMetadataSource } from '@newheap/platform-ai-chat';
import { NhAssistantMockAgentMetadataEntry, NhAssistantMockScenario } from './nh-assistant-mock.models';
import { NhAssistantMockResult, mockFailure as failure, mockSnapshot } from './nh-assistant-mock-admin-shared';

/** What the metadata reads of the mock's agents and caller. */
export interface NhAssistantMockAgentMetadataContext {
  agent(agentId: string): AdminAgent | undefined;
  /** Whether the caller passes the administration policy. */
  administers(): boolean;
  /** Makes the next version of an agent created here, as a change of its metadata does. */
  nextVersion(agentId: string): AdminAgent;
}

/** A stored value: of an agent created here, or an override of an agent in code. */
interface StoredValue {
  value: Record<string, unknown>;
  version: number;
  changedAt: string;
  changedBy: string;
}

const defaultMaxBytes = 4_096;
const dashCase = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;

/**
 * Agent metadata of the mock API (MD1 to MD3), as the server serves it: `admin.agentMetadata` of the
 * scenario are the entries registered in code, and `metadata` of each scenario agent its values, in
 * code for an agent in code and stored at the first version for an agent created as data.
 * `GET admin/agents/{id}/metadata`, `PUT` and `DELETE admin/agents/{id}/metadata/{name}` answer with
 * the server's checks: an entry of an agent created here makes the agent's next version, an editable
 * entry of an agent in code is an override with a version of its own, and a read-only one is refused.
 * Values of `POST` and `PUT admin/agents` are not taken: the agent editor saves each entry through its
 * slot. Values live as long as the mock.
 */
export class NhAssistantMockAgentMetadata {
  private readonly entries: readonly NhAssistantMockAgentMetadataEntry[];
  /** The values in code, by agent and entry. */
  private readonly code = new Map<string, Map<string, Record<string, unknown>>>();
  /** The stored values, by agent and entry. */
  private readonly stored = new Map<string, Map<string, StoredValue>>();

  constructor(
    scenario: NhAssistantMockScenario,
    private readonly context: NhAssistantMockAgentMetadataContext,
    private readonly now: () => string
  ) {
    this.entries = [...(scenario.admin?.agentMetadata ?? [])].sort((left, right) => left.name.localeCompare(right.name));
    for (const entry of this.entries) {
      if (!dashCase.test(entry.name) || entry.name.length > 64 || entry.name.startsWith('nh-')) {
        throw new Error(`The mock's agent metadata entry '${entry.name}' is not a valid name.`);
      }
    }
    for (const agent of scenario.admin?.agents ?? []) {
      for (const [name, value] of Object.entries(agent.metadata ?? {})) {
        const entry = this.entries.find(item => item.name === name);
        // Startup refuses a value that a save would refuse.
        const problems = entry ? this.problemsOf(entry, value) : { '': ['not-found'] };
        if (!entry || Object.keys(problems).length > 0) {
          throw new Error(`The mock agent '${agent.id}' sets metadata '${name}' that the entries refuse.`);
        }
        if (agent.source === 'code') {
          this.valuesOf(this.code, agent.id).set(name, structuredClone(value as Record<string, unknown>));
        } else {
          this.valuesOf(this.stored, agent.id).set(name, {
            value: structuredClone(value as Record<string, unknown>),
            version: 1,
            changedAt: this.now(),
            changedBy: 'current-user'
          });
        }
      }
    }
  }

  /** The run catalog's `agentMetadata`. */
  catalog(): AgentMetadataEntry[] {
    return this.entries.map(entry => ({
      name: entry.name,
      editable: entry.editable === true,
      maxBytes: entry.maxBytes ?? defaultMaxBytes,
      indexed: entry.indexBy !== undefined || entry.indexByEach !== undefined,
      indexedByEach: entry.indexByEach !== undefined
    }));
  }

  /**
   * A copy of the stored values; the returned function puts it back, for an import of an agent
   * package that fails as a whole and for its preview.
   */
  snapshot(): () => void {
    return mockSnapshot(this.stored);
  }

  /** Forgets the values of an agent that was deleted, as the server removes them with it. */
  forget(agentId: string): void {
    this.stored.delete(agentId);
  }

  /** `GET admin/agents/{id}/metadata`, `PUT` and `DELETE admin/agents/{id}/metadata/{name}`. */
  handle(method: string, agentId: string, name: string | undefined, query: URLSearchParams, body: unknown): NhAssistantMockResult {
    if (!this.context.administers()) {
      return failure('assistant-forbidden');
    }
    if (name === undefined) {
      if (method !== 'GET') {
        return { status: 405 };
      }
      const agent = this.context.agent(agentId);
      return agent
        ? { status: 200, body: this.entries.map(entry => this.view(agent, entry)) }
        : failure('assistant-agent-not-found');
    }
    if (method !== 'PUT' && method !== 'DELETE') {
      return { status: 405 };
    }

    const entry = this.entries.find(item => item.name === name);
    if (!entry) {
      return failure('assistant-metadata-entry-not-found');
    }
    const agent = this.context.agent(agentId);
    if (!agent) {
      return failure('assistant-agent-not-found');
    }
    if (agent.source === 'code' && entry.editable !== true) {
      return failure('assistant-metadata-read-only');
    }

    return method === 'PUT'
      ? this.save(agent, entry, body as { value?: unknown; expectedVersion?: number | null } | undefined)
      : this.remove(agent, entry, query.get('expectedVersion'));
  }

  private save(
    agent: AdminAgent,
    entry: NhAssistantMockAgentMetadataEntry,
    request: { value?: unknown; expectedVersion?: number | null } | undefined
  ): NhAssistantMockResult {
    if (!request || typeof request !== 'object') {
      return failure('assistant-validation');
    }

    const errors: Record<string, string[]> = {};
    for (const [field, codes] of Object.entries(this.problemsOf(entry, request.value))) {
      errors[field === '' ? 'value' : `value.${field}`] = codes;
    }
    const expectedVersion = typeof request.expectedVersion === 'number' ? request.expectedVersion : null;
    if (agent.source !== 'code' && expectedVersion === null) {
      errors['expectedVersion'] = ['required'];
    }
    if (Object.keys(errors).length > 0) {
      return failure('assistant-validation', errors);
    }

    const values = this.valuesOf(this.stored, agent.id);
    const current = values.get(entry.name);
    let version: number;
    if (agent.source === 'code') {
      if (expectedVersion !== (current?.version ?? null)) {
        return failure('assistant-version-conflict');
      }
      version = (current?.version ?? 0) + 1;
    } else {
      if (expectedVersion !== agent.version) {
        return failure('assistant-version-conflict');
      }
      version = this.context.nextVersion(agent.id).version;
    }

    values.set(entry.name, { value: structuredClone(request.value as Record<string, unknown>), version, changedAt: this.now(), changedBy: 'current-user' });
    return { status: 200, body: this.view(this.context.agent(agent.id)!, entry) };
  }

  private remove(agent: AdminAgent, entry: NhAssistantMockAgentMetadataEntry, expected: string | null): NhAssistantMockResult {
    const values = this.valuesOf(this.stored, agent.id);
    const current = values.get(entry.name);
    if (!current) {
      return { status: 200, body: this.view(agent, entry) };
    }
    if (expected === null || !/^\d+$/.test(expected)) {
      return failure('assistant-validation', { expectedVersion: ['required'] });
    }
    const expectedVersion = Number(expected);
    if (expectedVersion !== (agent.source === 'code' ? current.version : agent.version)) {
      return failure('assistant-version-conflict');
    }

    if (agent.source !== 'code') {
      this.context.nextVersion(agent.id);
    }
    values.delete(entry.name);
    return { status: 200, body: this.view(this.context.agent(agent.id)!, entry) };
  }

  /** The entry of the agent in effect, as `GET admin/agents/{id}/metadata` lists it. */
  private view(agent: AdminAgent, entry: NhAssistantMockAgentMetadataEntry): AgentMetadata {
    const stored = this.stored.get(agent.id)?.get(entry.name);
    const code = this.code.get(agent.id)?.get(entry.name);
    const inCode = agent.source === 'code';
    const value = stored?.value ?? (inCode ? code : undefined);
    const source: AgentMetadataSource | null = stored
      ? (inCode ? 'override' : 'data')
      : (inCode && code !== undefined ? 'code' : null);
    return {
      name: entry.name,
      editable: !inCode || entry.editable === true,
      maxBytes: entry.maxBytes ?? defaultMaxBytes,
      source,
      value: value === undefined ? null : structuredClone(value),
      version: inCode ? stored?.version ?? null : agent.version,
      agentVersion: agent.version,
      codeValue: inCode && code !== undefined ? structuredClone(code) : null,
      indexed: entry.indexBy !== undefined || entry.indexByEach !== undefined,
      indexValue: value !== undefined ? indexOf(entry, value) : null,
      indexedByEach: entry.indexByEach !== undefined,
      indexValues: value !== undefined ? indexValuesOf(entry, value) : [],
      changedAt: stored?.changedAt ?? null,
      changedBy: stored?.changedBy ?? null
    };
  }

  /** The problems of a value, by field path, `''` for the whole value, as the server checks it. */
  private problemsOf(entry: NhAssistantMockAgentMetadataEntry, value: unknown): Record<string, string[]> {
    if (value === null || value === undefined) {
      return { '': ['required'] };
    }
    if (typeof value !== 'object' || Array.isArray(value)
      || (entry.fields && Object.keys(value).some(field => !entry.fields!.includes(field)))) {
      return { '': ['invalid'] };
    }
    if (new TextEncoder().encode(JSON.stringify(value)).length > (entry.maxBytes ?? defaultMaxBytes)) {
      return { '': ['too-long'] };
    }
    const problems = { ...(entry.validate?.(value as Record<string, unknown>) ?? {}) };
    const index = indexOf(entry, value as Record<string, unknown>);
    if (index !== null && index.length > 256) {
      problems[''] = [...(problems[''] ?? []), 'too-long'];
    }
    return Object.fromEntries(Object.entries(problems).filter(([, codes]) => codes.length > 0));
  }

  private valuesOf<T>(map: Map<string, Map<string, T>>, agentId: string): Map<string, T> {
    let values = map.get(agentId);
    if (!values) {
      values = new Map<string, T>();
      map.set(agentId, values);
    }
    return values;
  }
}

/** The searchable value of a value, trimmed and in lowercase, as the server stores it. */
function indexOf(entry: NhAssistantMockAgentMetadataEntry, value: Record<string, unknown>): string | null {
  if (entry.indexBy === undefined) {
    return null;
  }
  const text = value[entry.indexBy];
  const trimmed = typeof text === 'string' ? text.trim() : '';
  return trimmed.length > 0 ? trimmed.toLowerCase() : null;
}

/**
 * The searchable values of an `IndexByEach` entry's value as the server stores them: trimmed, in
 * lowercase, without empty values or repeats, in ordinal order.
 */
function indexValuesOf(entry: NhAssistantMockAgentMetadataEntry, value: Record<string, unknown>): string[] {
  const texts = entry.indexByEach === undefined ? undefined : value[entry.indexByEach];
  if (!Array.isArray(texts)) {
    return [];
  }
  const values = texts
    .map(text => (typeof text === 'string' ? text.trim().toLowerCase() : ''))
    .filter(text => text.length > 0);
  return [...new Set(values)].sort((left, right) => (left < right ? -1 : left > right ? 1 : 0));
}
