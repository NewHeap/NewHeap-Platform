import {
  AdminAgent,
  AdminAgentInput,
  AgentHandOffs,
  AgentMetadata,
  AgentPackage,
  AgentPackageExportOptions,
  AgentPackageImportOptions,
  AgentPackageLink,
  AgentPackagePart,
  AgentPackagePartKind,
  AgentPackagePartOutcome,
  AgentPackageWarning,
  AgentRunConfiguration,
  NH_ASSISTANT_AGENT_PACKAGE_FORMAT,
  NH_ASSISTANT_AGENT_PACKAGE_FORMAT_VERSION,
  NH_ASSISTANT_AGENT_PACKAGE_MAX_AGENTS,
  NH_ASSISTANT_AGENT_PACKAGE_MAX_BYTES,
  NH_ASSISTANT_PACKAGE_LOCK_ALL,
  NH_ASSISTANT_PACKAGE_LOCK_FIELDS,
  PackageAgent,
  PackagePartLink
} from '@newheap/platform-ai-chat';
import { NhAssistantMockAgentPackage } from './nh-assistant-mock.models';
import { NhAssistantMockResult, mockDashCase, mockFailure as failure, mockMaxStateReason as maxReason, mockSnapshot } from './nh-assistant-mock-admin-shared';

/** What the agent packages read and change of the rest of the mock. */
export interface NhAssistantMockPackageContext {
  /**
   * Answers an administration request (`admin/...`) as its endpoint does, without the package locks:
   * the import's own path through the save validators.
   */
  call(method: string, path: string, body?: unknown): NhAssistantMockResult;
  /** The definition in code of an agent in code, for its overlay; `null` for an agent created here. */
  codeDefinition(agentId: string): AdminAgentInput | null;
  /** A copy of every store of the administration; the returned function puts it back. */
  snapshot(): () => void;
  now(): string;
}

/** The link of one part of an agent to the package it was imported from. */
interface PartLink {
  lockedFields: string[];
  /** The SHA-256 of the part's canonical JSON once the package's version is in effect. */
  hash: string;
  /** The fields of the package's version, which a locked field keeps. */
  view: Record<string, unknown> | null;
  packageVersion: number;
  linkedAt: string;
  linkedBy: string;
}

/** An agent's link to its package: the linked parts, and those an administrator unlinked (`*` for all). */
interface AgentLink {
  packageId: string;
  packageVersion: number;
  parts: Map<string, PartLink>;
  local: Set<string>;
}

/** A package the host imported. */
interface InstalledPackage {
  version: number;
  agents: string[];
}

/** What the mock reads of one agent on the host. */
interface HostRead {
  agent: AdminAgent;
  configuration: AgentRunConfiguration | null;
  handOffs: AgentHandOffs | null;
  metadata: AgentMetadata[];
}

/** The outcome of one part during an import, with what it needs for the link afterwards. */
interface PlannedPart extends AgentPackagePart {
  /** The hash of the host's part once the package's version is in effect. */
  hashAfter: string | null;
  /** The fields of the host's part once the package's version is in effect. */
  viewAfter: Record<string, unknown> | null;
}

/** The longest package id. */
const maxPackageId = 128;
/** The longest package description. */
const maxPackageDescription = 500;
/** The header of the export that lists the references to agents outside the selection. */
const warningsHeader = 'X-NewHeap-Package-Warnings';

/** The order in which an import writes the parts of its agents. */
const phaseOrder: readonly AgentPackagePartKind[] = ['agent', 'runSettings', 'skill', 'trigger', 'consultation', 'handOff', 'metadata'];

/** The properties of the package format each lockable field stands for. */
const lockProperties: Readonly<Record<AgentPackagePartKind, Readonly<Record<string, readonly string[]>>>> = {
  agent: {
    instructions: ['instructions', 'instructionsAsset'],
    worksOn: ['worksOnSubjectTypes', 'worksOnAllSubjects', 'worksOnApplication', 'perDivision', 'runPolicy', 'viewPolicy']
  },
  runSettings: {
    dailyBudget: ['dailyToolCalls', 'dailyTokens'],
    files: ['acceptsFiles', 'maxFileBytes', 'maxFilePages', 'maxFilesPerTurn', 'maxFileTokensPerTurn']
  },
  skill: {
    instructions: ['instructions', 'instructionsAsset'],
    dailyBudget: ['dailyToolCalls', 'dailyTokens'],
    files: ['acceptsFiles', 'maxFileBytes', 'maxFilePages', 'maxFilesPerTurn', 'maxFileTokensPerTurn']
  },
  trigger: {},
  consultation: {
    limits: ['maxToolCalls', 'timeoutSeconds', 'maxAnswerChars', 'maxHistoryMessages']
  },
  handOff: {},
  metadata: {}
};

/** The fields of the definition an administrator overrides on an agent in code, and its overlay carries. */
const codeAgentFields = ['displayName', 'description', 'instructions', 'toolSelectors', 'requiredPolicy', 'autonomy'] as const;

/** The properties the strict format reads per object, required ones first. */
const formatShapes: Readonly<Record<string, { required: readonly string[]; optional: readonly string[] }>> = {
  root: { required: ['format', 'formatVersion', 'package', 'agents'], optional: [] },
  package: { required: ['id', 'version'], optional: ['description'] },
  agent: {
    required: ['id', 'source'],
    optional: ['definition', 'runSettings', 'skills', 'triggers', 'consultations', 'handOffs', 'metadata', 'groups', 'locks', 'hashes']
  },
  definition: {
    required: [],
    optional: [
      'displayName', 'description', 'instructions', 'instructionsAsset', 'toolSelectors', 'requiredPolicy', 'autonomy', 'offeredInChat',
      'worksOnSubjectTypes', 'worksOnAllSubjects', 'worksOnApplication', 'perDivision', 'runPolicy', 'viewPolicy', 'profile',
      'dataClassification', 'startsOff', 'startsInShadow', 'shadowOf'
    ]
  },
  runSettings: {
    required: ['accountability'],
    optional: [
      'delegateFromInitiator', 'allowGatewayReads', 'elevatedReads', 'approvalPolicy', 'retryCount', 'dailyToolCalls', 'dailyTokens',
      'maxConcurrentRuns', 'maxRunsPerSubjectPerDay', 'maxTokensPerRun', 'triageProfile', 'ruleHandler', 'acceptsFiles', 'maxFileBytes',
      'maxFilePages', 'maxFilesPerTurn', 'maxFileTokensPerTurn'
    ]
  },
  skill: {
    required: ['id', 'description'],
    optional: [
      'instructions', 'instructionsAsset', 'tools', 'profile', 'ruleHandler', 'maxTokensPerRun', 'dailyToolCalls', 'dailyTokens',
      'approvalPolicy', 'dataClassification', 'acceptsFiles', 'maxFileBytes', 'maxFilePages', 'maxFilesPerTurn', 'maxFileTokensPerTurn',
      'startsOff'
    ]
  },
  trigger: {
    required: ['id', 'source'],
    optional: ['skill', 'event', 'filters', 'cron', 'timeZone', 'subjectSet', 'message', 'conflict', 'debounceSeconds']
  },
  consultation: {
    required: ['subjectType', 'description'],
    optional: [
      'instructions', 'minimumRole', 'askerPolicy', 'routes', 'proposalRole', 'proposalPolicy', 'toolSelectors', 'maxToolCalls',
      'timeoutSeconds', 'maxAnswerChars', 'maxHistoryMessages', 'allowRequests', 'exposeToMcp', 'dataClassification', 'startsOff'
    ]
  },
  handOff: { required: ['subjectType', 'sources'], optional: ['description', 'notifySource', 'maxPerSubjectPerDay'] },
  instructionsAsset: { required: ['assetId', 'version', 'hash'], optional: [] }
};

/**
 * Agent packages of the mock API (S7-2), with the rules of the server: an export of exactly the chosen
 * agents with the references to agents outside them, a preview per part (`created`, `changed`,
 * `unchanged`, `kept-local`, `left-behind`, `refused`) after one check of the whole package against
 * the host as it would be after the import, and an import that applies every part through the mock's
 * own endpoints in dependency order, or nothing. Linked parts carry their link in every read; every
 * save path refuses a change of a locked field with `{field}: locked`, and removing a locked part or
 * deleting or resetting a linked agent answers `409 assistant-package-locked`. Unlinking makes a part
 * local: the next upgrade keeps it as `kept-local` unless the import relinks it. Hashes are the SHA-256
 * of each part's canonical JSON; a hash a package gives is not checked.
 */
export class NhAssistantMockAgentPackages {
  private readonly links = new Map<string, AgentLink>();
  private readonly installed = new Map<string, InstalledPackage>();

  constructor(private readonly context: NhAssistantMockPackageContext) {
  }

  /**
   * Imports the scenario's packages in order, as an administrator would; a package the import refuses
   * fails the scenario at once.
   */
  importPresets(presets: readonly NhAssistantMockAgentPackage[]): void {
    for (const preset of presets) {
      const result = this.importPackage({ package: preset.package, options: { serviceAccounts: preset.serviceAccounts ?? null } }, true);
      if (result.status !== 200) {
        throw new Error(`The mock refused the scenario's agent package '${preset.package?.package?.id}': ${JSON.stringify(result.body ?? null)}.`);
      }
    }
  }

  /**
   * `POST admin/agent-packages/export`, `.../preview` and `.../import`, and `POST
   * admin/agents/{id}/package/unlink`; `null` for another route.
   */
  handle(method: string, segments: readonly string[], body: unknown): NhAssistantMockResult | null {
    if (segments[0] !== 'admin') {
      return null;
    }
    if (segments[1] === 'agent-packages' && segments.length === 3) {
      if (method !== 'POST') {
        return { status: 405 };
      }
      switch (segments[2]) {
        case 'export':
          return this.exportPackage(body);
        case 'preview':
          return this.importPackage(body, false);
        case 'import':
          return this.importPackage(body, true);
        default:
          return failure('assistant-not-found');
      }
    }
    if (segments[1] === 'agents' && segments[3] === 'package' && segments[4] === 'unlink' && segments.length === 5) {
      return method === 'POST' ? this.unlink(segments[2], body) : { status: 405 };
    }
    return null;
  }

  /**
   * Runs a save of the administration under the package locks: a change of a locked field is undone
   * and refused with `{field}: locked`, removing a locked part or deleting or resetting a linked agent
   * answers `409 assistant-package-locked`, and removing a linked part without locks drops its link.
   * Other requests run as they are.
   */
  guard(method: string, segments: readonly string[], run: () => NhAssistantMockResult): NhAssistantMockResult {
    const target = guardedPart(method, segments);
    if (!target) {
      return run();
    }
    if (target.part === null) {
      return this.isLinked(target.agentId) ? failure('assistant-package-locked') : run();
    }

    const link = this.links.get(target.agentId)?.parts.get(target.part);
    if (!link) {
      return run();
    }
    if (target.removal) {
      if (link.lockedFields.length > 0) {
        return failure('assistant-package-locked');
      }
      const removed = run();
      if (removed.status < 300) {
        this.links.get(target.agentId)?.parts.delete(target.part);
      }
      return removed;
    }
    if (link.lockedFields.length === 0) {
      return run();
    }

    const kind = kindOf(target.part);
    const before = this.lockView(target.agentId, target.part);
    const restore = this.context.snapshot();
    const result = run();
    if (result.status >= 300) {
      return result;
    }

    const changed = lockedChanges(kind, link.lockedFields, before, this.lockView(target.agentId, target.part), link.view);
    if (changed.length > 0) {
      restore();
      return failure('assistant-validation', Object.fromEntries(changed.map(field => [field, ['locked']])));
    }
    return result;
  }

  /**
   * Adds the package links to an administration answer: the agent's package on agents, the part's link
   * on run settings, skills, triggers, consultations, hand-offs and metadata entries.
   */
  decorate(method: string, segments: readonly string[], result: NhAssistantMockResult): NhAssistantMockResult {
    if (segments[0] !== 'admin' || segments[1] !== 'agents' || result.status >= 300 || result.body === undefined || this.links.size === 0) {
      return result;
    }

    const [, , agentId, area, id, action] = segments;
    const body = result.body;
    if (agentId === undefined) {
      if (Array.isArray(body)) {
        return { ...result, body: body.map(agent => this.withAgentPackage(agent as AdminAgent)) };
      }
      return { ...result, body: this.withAgentPackage(body as AdminAgent) };
    }
    if (area === undefined || (area === 'reset' && segments.length === 4)) {
      return { ...result, body: this.withAgentPackage(body as AdminAgent) };
    }
    if (method === 'DELETE' && area !== 'metadata') {
      return result;
    }

    switch (area) {
      case 'configuration': {
        const configuration = body as AgentRunConfiguration;
        return {
          ...result,
          body: {
            ...configuration,
            runSettings: configuration.runSettings ? this.withLink(agentId, 'runSettings', configuration.runSettings) : configuration.runSettings,
            dataSkills: configuration.dataSkills.map(skill => this.withLink(agentId, `skill:${skill.skillId}`, skill)),
            dataTriggers: configuration.dataTriggers.map(trigger => this.withLink(agentId, `trigger:${trigger.triggerId}`, trigger)),
            dataConsultations: configuration.dataConsultations?.map(item => this.withLink(agentId, `consultation:${item.subjectType}`, item))
          }
        };
      }
      case 'run-settings':
        return { ...result, body: this.withLink(agentId, 'runSettings', body as object) };
      case 'skills':
        return id !== undefined && (segments.length === 5 || (action === 'candidate' && segments[6] === 'promote'))
          ? { ...result, body: this.withLink(agentId, `skill:${id}`, body as object) }
          : result;
      case 'triggers':
        return id !== undefined && (segments.length === 5 || (action === 'candidate' && segments[6] === 'promote') || action === 'shadow')
          ? { ...result, body: this.withLink(agentId, `trigger:${id}`, body as object) }
          : result;
      case 'consultations':
        return id !== undefined ? { ...result, body: this.withLink(agentId, `consultation:${id}`, body as object) } : result;
      case 'hand-offs': {
        if (id !== undefined) {
          return { ...result, body: this.withLink(agentId, `handOff:${id}`, body as object) };
        }
        const handOffs = body as AgentHandOffs;
        return { ...result, body: { ...handOffs, dataHandOffs: handOffs.dataHandOffs.map(item => this.withLink(agentId, `handOff:${item.subjectType}`, item)) } };
      }
      case 'metadata':
        if (id !== undefined) {
          return { ...result, body: this.withLink(agentId, `metadata:${id}`, body as object) };
        }
        return { ...result, body: (body as AgentMetadata[]).map(entry => this.withLink(agentId, `metadata:${entry.name}`, entry)) };
      default:
        return result;
    }
  }

  /** A copy of the links and the imported packages; the returned function puts them back. */
  snapshot(): () => void {
    return mockSnapshot(this.links, this.installed);
  }

  /** Forgets the links of an agent that was deleted, as the server removes them with it. */
  forget(agentId: string): void {
    this.links.delete(agentId);
  }

  private exportPackage(body: unknown): NhAssistantMockResult {
    const request = (body ?? {}) as { agentIds?: unknown; options?: AgentPackageExportOptions | null };
    const agentIds = request.agentIds;
    if (!Array.isArray(agentIds) || agentIds.length === 0 || !agentIds.every(id => typeof id === 'string')) {
      return failure('assistant-validation', { agentIds: ['required'] });
    }
    if (agentIds.length > NH_ASSISTANT_AGENT_PACKAGE_MAX_AGENTS || new Set(agentIds).size !== agentIds.length) {
      return failure('assistant-validation', { agentIds: ['invalid'] });
    }

    const reads: HostRead[] = [];
    for (const agentId of agentIds as string[]) {
      const read = this.read(agentId);
      if (!read) {
        return failure('assistant-agent-not-found');
      }
      reads.push(read);
    }

    const options = request.options ?? {};
    const errors: Record<string, string[]> = {};
    const linked = this.links.get(reads[0].agent.id);
    const packageId = text(options.packageId) ?? linked?.packageId ?? reads[0].agent.id;
    if (!mockDashCase.test(packageId) || packageId.length > maxPackageId) {
      errors['packageId'] = ['invalid'];
    }
    const installed = this.installed.get(packageId);
    const version = options.version ?? (installed ? installed.version + 1 : 1);
    if (!Number.isInteger(version) || version < 1) {
      errors['version'] = ['invalid'];
    }
    const description = text(options.description);
    if (description !== null && description.length > maxPackageDescription) {
      errors['description'] = ['too-long'];
    }

    const agents = reads.map(read => this.packageAgentOf(read));
    for (const [agentId, locks] of Object.entries(options.locks ?? {})) {
      const agent = agents.find(item => item.id === agentId);
      for (const [part, fields] of Object.entries(locks ?? {})) {
        const problem = agent ? lockProblem(agent, part, fields) : 'not-found';
        if (problem) {
          errors['locks'] = [problem];
        } else if (agent && fields.length > 0) {
          agent.locks = { ...(agent.locks ?? {}), [part]: [...fields] };
        }
      }
    }
    if (Object.keys(errors).length > 0) {
      return failure('assistant-validation', errors);
    }

    for (const agent of agents) {
      agent.hashes = Object.fromEntries([...partsOf(agent)].map(([part, value]) => [part, mockSha256(canonicalJson(value))]));
    }
    const agentPackage: AgentPackage = {
      format: NH_ASSISTANT_AGENT_PACKAGE_FORMAT,
      formatVersion: NH_ASSISTANT_AGENT_PACKAGE_FORMAT_VERSION,
      package: { id: packageId, version, ...(description !== null ? { description } : {}) },
      agents
    };
    return {
      status: 200,
      text: JSON.stringify(withoutNulls(agentPackage), null, 2),
      headers: {
        'content-type': 'application/json',
        'content-disposition': `attachment; filename="agent-package-${packageId}.json"`,
        [warningsHeader]: JSON.stringify(this.warningsOf(reads))
      }
    };
  }

  /** Every reference of the exported agents to agents outside the selection. */
  private warningsOf(reads: readonly HostRead[]): AgentPackageWarning[] {
    const selected = new Set(reads.map(read => read.agent.id));
    const everyone = this.agents();
    const warnings: AgentPackageWarning[] = [];
    const outside = (agentId: string, part: string, code: string, referencedAgentId: string): void => {
      if (!selected.has(referencedAgentId)) {
        warnings.push({ agentId, part, code, referencedAgentId });
      }
    };

    for (const { agent, configuration, handOffs } of reads) {
      if (agent.shadowOf) {
        outside(agent.id, 'agent', 'shadow-of', agent.shadowOf);
      }
      for (const variant of everyone.filter(item => item.shadowOf === agent.id)) {
        outside(agent.id, 'agent', 'shadow-variant', variant.id);
      }
      for (const handOff of handOffs?.dataHandOffs ?? []) {
        for (const source of handOff.sources) {
          outside(agent.id, `handOff:${handOff.subjectType}`, 'hand-off-source', source);
        }
      }
      for (const consultation of configuration?.dataConsultations ?? []) {
        for (const asker of consultation.askingAgents) {
          outside(agent.id, `consultation:${consultation.subjectType}`, 'consultation-asker', asker);
        }
      }
    }
    return warnings;
  }

  /**
   * `POST admin/agent-packages/preview` (`keep` false) and `.../import`: the package read strictly, its
   * parts applied in dependency order on a copy of the host, and, for an import without a refused part,
   * applied for real with the links of its parts.
   */
  private importPackage(body: unknown, keep: boolean): NhAssistantMockResult {
    const request = (body ?? {}) as { package?: unknown; options?: AgentPackageImportOptions | null };
    const read = readPackage(request.package);
    if ('status' in read) {
      return read;
    }

    const agentPackage = read.package;
    const options = request.options ?? {};
    const apply = options.apply ?? 'live';
    if (apply !== 'live' && apply !== 'shadow') {
      return failure('assistant-validation', { apply: ['invalid'] });
    }

    // The check tries every part on a copy of the host, in live mode, and puts the host back.
    const restoreHost = this.context.snapshot();
    const restoreOwn = this.snapshot();
    const planned = this.apply(agentPackage, options, 'live', null);
    restoreHost();
    restoreOwn();

    const preview = {
      packageId: agentPackage.package.id,
      packageVersion: agentPackage.package.version,
      installedVersion: this.installed.get(agentPackage.package.id)?.version ?? null,
      importable: planned.every(part => part.outcome !== 'refused'),
      parts: planned.map(publicPart)
    };
    if (!keep) {
      return { status: 200, body: preview };
    }
    if (!preview.importable) {
      return failure('assistant-package-refused', refusedErrors(planned));
    }

    // The import applies the package for real, with the chosen mode, and links its parts.
    const restoreAll = [this.context.snapshot(), this.snapshot()];
    const applied = this.apply(agentPackage, options, apply, planned);
    if (applied.some(part => part.outcome === 'refused')) {
      for (const restore of restoreAll) {
        restore();
      }
      return failure('assistant-package-refused', refusedErrors(applied));
    }
    this.link(agentPackage, options, planned);
    return {
      status: 200,
      body: {
        packageId: agentPackage.package.id,
        packageVersion: agentPackage.package.version,
        hash: mockSha256(canonicalJson(agentPackage)),
        parts: applied.map(publicPart)
      }
    };
  }

  /**
   * Applies every agent and part of the package to the host in dependency order: agents that are no
   * shadow variant of another agent of the package first, each phase for all of them, then the
   * variants; reports per part what happened, `refused` with the endpoint's code or field errors.
   * Parts an administrator unlinked stay as they are (`kept-local`) unless `relink` names them; agents
   * and parts an earlier version of the package linked that it no longer holds are `left-behind`.
   */
  private apply(
    agentPackage: AgentPackage,
    options: AgentPackageImportOptions,
    mode: 'live' | 'shadow',
    plan: readonly PlannedPart[] | null
  ): PlannedPart[] {
    const packageId = agentPackage.package.id;
    const ids = new Set(agentPackage.agents.map(agent => agent.id));
    const live = agentPackage.agents.filter(agent => !agent.definition?.shadowOf || !ids.has(agent.definition.shadowOf));
    const variants = agentPackage.agents.filter(agent => !live.includes(agent));
    const results = new Map<string, PlannedPart[]>();
    const refusedAgents = new Set<string>();

    for (const group of [live, variants]) {
      for (const kind of phaseOrder) {
        for (const agent of group) {
          if (refusedAgents.has(agent.id)) {
            continue;
          }
          for (const [part, value] of partsOf(agent)) {
            if (kindOf(part) !== kind) {
              continue;
            }
            const planned = plan?.find(item => item.agentId === agent.id && item.part === part);
            // The import writes only what the check found new or changed.
            const outcome = planned && planned.outcome !== 'created' && planned.outcome !== 'changed'
              ? planned
              : this.applyPart(agentPackage, agent, part, value, options, mode);
            results.set(agent.id, [...(results.get(agent.id) ?? []), outcome]);
            if (kind === 'agent' && outcome.outcome === 'refused') {
              refusedAgents.add(agent.id);
            }
          }
          // Groups go with the agent: they are no part, and stay free once imported.
          if (kind === 'agent' && !refusedAgents.has(agent.id) && agent.groups && !agent.definition?.shadowOf) {
            const groups = this.context.call('PUT', `admin/agents/${encodeURIComponent(agent.id)}/groups`, { groups: agent.groups });
            if (groups.status >= 300) {
              const refusal = refusalOf(agent.id, 'agent', groups, agent.locks?.['agent'] ?? []);
              results.set(agent.id, [refusal, ...(results.get(agent.id) ?? []).filter(item => item.part !== 'agent')]);
              refusedAgents.add(agent.id);
            }
          }
        }
      }
    }

    // What an earlier version of the package linked and this one no longer holds stays, unlinked.
    const ordered = agentPackage.agents.flatMap(agent => results.get(agent.id) ?? []);
    for (const [agentId, link] of this.links) {
      if (link.packageId !== packageId) {
        continue;
      }
      if (!ids.has(agentId)) {
        ordered.push(leftBehind(agentId, 'agent'));
        continue;
      }
      const held = new Set(partsOf(agentPackage.agents.find(agent => agent.id === agentId)!).keys());
      for (const part of link.parts.keys()) {
        if (!held.has(part)) {
          ordered.push(leftBehind(agentId, part));
        }
      }
    }
    return ordered;
  }

  /** Applies one part through its endpoint and reports what happened to it. */
  private applyPart(
    agentPackage: AgentPackage,
    agent: PackageAgent,
    part: string,
    value: unknown,
    options: AgentPackageImportOptions,
    mode: 'live' | 'shadow'
  ): PlannedPart {
    const lockedFields = agent.locks?.[part] ?? [];
    const link = this.links.get(agent.id);
    const linkedHere = link?.packageId === agentPackage.package.id;
    const relinked = (options.relink ?? []).some(item => item.agentId === agent.id && (item.part === null || item.part === undefined || item.part === part));
    const lockProblemCode = lockProblem(agent, part, lockedFields);
    if (lockProblemCode) {
      return { ...base(agent.id, part, lockedFields), outcome: 'refused', errors: { locks: [lockProblemCode] } };
    }

    const host = this.read(agent.id);
    if (part === 'agent') {
      const refusal = agentRefusal(agent, host?.agent ?? null, linkedHere);
      if (refusal) {
        return { ...base(agent.id, part, lockedFields), outcome: 'refused', code: refusal };
      }
    }
    if (linkedHere && !relinked && (link!.local.has(part) || link!.local.has(NH_ASSISTANT_PACKAGE_LOCK_ALL))) {
      return { ...base(agent.id, part, lockedFields), outcome: 'kept-local' };
    }

    const before = host ? this.hostHash(host, part) : null;
    const partLink = linkedHere ? link!.parts.get(part) : undefined;
    const result = this.write(agent, part, value, host, options, mode);
    if (result.status >= 300) {
      return refusalOf(agent.id, part, result, lockedFields);
    }

    const after = this.read(agent.id);
    const hashAfter = after ? this.hostHash(after, part) : null;
    // In shadow a changed skill or trigger is saved as the candidate next to its live version.
    const kind = kindOf(part);
    const candidateVersion = mode === 'shadow' && before !== null && (kind === 'skill' || kind === 'trigger')
      ? (result.body as { version?: number } | undefined)?.version ?? null
      : null;
    let outcome: AgentPackagePartOutcome = 'changed';
    if (before === null) {
      outcome = 'created';
    } else if (candidateVersion === null && before === hashAfter) {
      outcome = 'unchanged';
    }
    return {
      ...base(agent.id, part, lockedFields),
      outcome,
      changedLocally: !!partLink && before !== partLink.hash,
      candidateVersion,
      hashAfter,
      viewAfter: this.lockView(agent.id, part)
    };
  }

  /** Writes one part through the endpoint a save of it uses. */
  private write(
    agent: PackageAgent,
    part: string,
    value: unknown,
    host: HostRead | null,
    options: AgentPackageImportOptions,
    mode: 'live' | 'shadow'
  ): NhAssistantMockResult {
    const kind = kindOf(part);
    const key = keyOf(part);
    const agentPath = `admin/agents/${encodeURIComponent(agent.id)}`;
    const item = value as Record<string, unknown>;
    switch (kind) {
      case 'agent':
        return this.writeDefinition(agent, host);
      case 'runSettings': {
        const settings = item as Record<string, unknown> & { accountability?: string };
        const account = settings.accountability === 'service-account' ? options.serviceAccounts?.[agent.id] ?? null : null;
        return this.context.call('PUT', `${agentPath}/run-settings`, {
          ...settings,
          serviceAccountActorId: account,
          expectedVersion: host?.configuration?.runSettings?.version ?? null
        });
      }
      case 'skill': {
        const { id: _, startsOff, ...skill } = item;
        const current = host?.configuration?.dataSkills.find(existing => existing.skillId === key);
        return this.context.call('PUT', `${agentPath}/skills/${encodeURIComponent(key)}`, {
          ...skill,
          tools: skill['tools'] ?? [],
          expectedVersion: current?.version ?? null,
          ...(current ? { apply: mode } : startsOff === true ? { startsOff: true } : {})
        });
      }
      case 'trigger': {
        const { id: _, ...trigger } = item;
        const current = host?.configuration?.dataTriggers.find(existing => existing.triggerId === key);
        return this.context.call('PUT', `${agentPath}/triggers/${encodeURIComponent(key)}`, {
          ...trigger,
          expectedVersion: current?.version ?? null,
          ...(current ? { apply: mode } : {})
        });
      }
      case 'consultation': {
        const { subjectType: _, startsOff, ...consultation } = item;
        const current = host?.configuration?.dataConsultations?.find(existing => existing.subjectType === key);
        return this.context.call('PUT', `${agentPath}/consultations/${encodeURIComponent(key)}`, {
          ...consultation,
          routes: consultation['routes'] ?? [],
          expectedVersion: current?.version ?? null,
          ...(!current && startsOff === true ? { startsOff: true } : {})
        });
      }
      case 'handOff': {
        const { subjectType: _, ...handOff } = item;
        const current = host?.handOffs?.dataHandOffs.find(existing => existing.subjectType === key);
        return this.context.call('PUT', `${agentPath}/hand-offs/${encodeURIComponent(key)}`, {
          ...handOff,
          expectedVersion: current?.version ?? null
        });
      }
      case 'metadata': {
        const entry = this.read(agent.id)?.metadata.find(existing => existing.name === key);
        return entry
          ? this.context.call('PUT', `${agentPath}/metadata/${encodeURIComponent(key)}`, { value, expectedVersion: entry.version })
          : failure('assistant-metadata-entry-not-found');
      }
    }
  }

  /**
   * Writes the definition: creates an agent created here when its id is free, or makes its next
   * version; overrides an agent in code with the overlay, or restores its code without one.
   */
  private writeDefinition(agent: PackageAgent, host: HostRead | null): NhAssistantMockResult {
    const definition = agent.definition ?? {};
    if (agent.source === 'code') {
      const code = this.context.codeDefinition(agent.id)!;
      const overlay = codeAgentFields.filter(field => definition[field] !== undefined && definition[field] !== null);
      if (overlay.length === 0) {
        return host?.agent.isOverridden
          ? this.context.call('POST', `admin/agents/${encodeURIComponent(agent.id)}/reset`)
          : { status: 200, body: host?.agent };
      }
      return this.context.call('PUT', `admin/agents/${encodeURIComponent(agent.id)}`, {
        ...code,
        ...Object.fromEntries(overlay.map(field => [field, definition[field]])),
        id: agent.id,
        mcpServerIds: host?.agent.mcpServerIds ?? [],
        expectedVersion: host?.agent.version ?? 1
      });
    }

    const worksOn = ['worksOnSubjectTypes', 'worksOnAllSubjects', 'worksOnApplication', 'perDivision', 'runPolicy', 'viewPolicy'] as const;
    const input: Record<string, unknown> = {
      id: agent.id,
      displayName: definition.displayName ?? '',
      description: definition.description ?? '',
      instructions: definition.instructions ?? '',
      ...(definition.instructionsAsset ? { instructionsAsset: definition.instructionsAsset } : {}),
      toolSelectors: definition.toolSelectors ?? [],
      mcpServerIds: host?.agent.mcpServerIds ?? [],
      requiredPolicy: definition.requiredPolicy ?? null,
      autonomy: definition.autonomy ?? 'explain',
      ...(definition.offeredInChat !== undefined && definition.offeredInChat !== null ? { offeredInChat: definition.offeredInChat } : {}),
      ...(worksOn.some(field => definition[field] !== undefined && definition[field] !== null)
        ? Object.fromEntries(worksOn.map(field => [field, definition[field] ?? null]))
        : {}),
      ...(definition.profile ? { profile: definition.profile } : {}),
      ...(definition.dataClassification ? { dataClassification: definition.dataClassification } : {})
    };
    if (!host) {
      return this.context.call('POST', 'admin/agents', {
        ...input,
        ...(definition.startsOff ? { startsOff: true } : {}),
        ...(definition.startsInShadow ? { startsInShadow: true } : {}),
        ...(definition.shadowOf ? { shadowOf: definition.shadowOf } : {})
      });
    }
    return this.context.call('PUT', `admin/agents/${encodeURIComponent(agent.id)}`, { ...input, expectedVersion: host.agent.version });
  }

  /**
   * Links the parts of an import: each part the import applied or found unchanged, with its locks and
   * the hash of the host's part once the package's version is in effect; parts that stayed local stay
   * so, relinked ones are linked again, and what the package left behind is unlinked.
   */
  private link(agentPackage: AgentPackage, options: AgentPackageImportOptions, planned: readonly PlannedPart[]): void {
    const packageId = agentPackage.package.id;
    const version = agentPackage.package.version;
    const now = this.context.now();
    for (const agent of agentPackage.agents) {
      const previous = this.links.get(agent.id);
      const kept = previous?.packageId === packageId ? previous : undefined;
      const relinked = (options.relink ?? []).filter(item => item.agentId === agent.id);
      const local = new Set([...(kept?.local ?? [])].filter(part =>
        !relinked.some(item => item.part === null || item.part === undefined || item.part === part)));
      if (relinked.some(item => item.part === null || item.part === undefined)) {
        local.clear();
      }

      const parts = new Map<string, PartLink>();
      for (const item of planned.filter(entry => entry.agentId === agent.id)) {
        if (item.outcome === 'kept-local' || item.outcome === 'left-behind' || item.outcome === 'refused') {
          continue;
        }
        const previousLink = kept?.parts.get(item.part);
        parts.set(item.part, {
          lockedFields: [...item.lockedFields],
          hash: item.hashAfter ?? previousLink?.hash ?? '',
          view: item.viewAfter ?? previousLink?.view ?? null,
          packageVersion: version,
          linkedAt: item.outcome === 'unchanged' && previousLink ? previousLink.linkedAt : now,
          linkedBy: item.outcome === 'unchanged' && previousLink ? previousLink.linkedBy : 'current-user'
        });
      }
      this.links.set(agent.id, { packageId, packageVersion: version, parts, local });
    }
    for (const item of planned.filter(entry => entry.outcome === 'left-behind' && entry.part === 'agent')) {
      this.links.delete(item.agentId);
    }
    this.installed.set(packageId, { version, agents: agentPackage.agents.map(agent => agent.id) });
  }

  /** `POST admin/agents/{id}/package/unlink`: one part, or without one the whole agent, becomes local. */
  private unlink(agentId: string, body: unknown): NhAssistantMockResult {
    if (!this.read(agentId)) {
      return failure('assistant-agent-not-found');
    }
    const request = (body ?? {}) as { part?: unknown; reason?: unknown };
    const part = request.part === undefined || request.part === null ? null : request.part;
    if (part !== null && (typeof part !== 'string' || !validPartKey(part))) {
      return failure('assistant-validation', { part: ['invalid'] });
    }
    if (request.reason !== undefined && request.reason !== null && (typeof request.reason !== 'string' || request.reason.length > maxReason)) {
      return failure('assistant-validation', { reason: ['invalid'] });
    }

    const link = this.links.get(agentId);
    if (!link || link.parts.size === 0 || (part !== null && !link.parts.has(part))) {
      return failure('assistant-package-not-linked');
    }
    const unlinked = part === null ? [...link.parts.keys()] : [part];
    for (const key of unlinked) {
      link.parts.delete(key);
      link.local.add(key);
    }
    if (part === null) {
      link.local.add(NH_ASSISTANT_PACKAGE_LOCK_ALL);
    }
    return { status: 200, body: { agentId, packageId: link.packageId, parts: unlinked.sort() } };
  }

  /** Whether the agent still has a linked part, so it cannot be deleted or reset. */
  private isLinked(agentId: string): boolean {
    return (this.links.get(agentId)?.parts.size ?? 0) > 0;
  }

  /** The agent with the package it belongs to, while a part of it is linked. */
  private withAgentPackage(agent: AdminAgent): AdminAgent {
    const link = this.links.get(agent?.id);
    if (!link || link.parts.size === 0) {
      return agent;
    }
    const read = this.read(agent.id);
    const value: AgentPackageLink = {
      packageId: link.packageId,
      packageVersion: link.packageVersion,
      parts: [...link.parts.keys()].sort().map(part => this.partLinkOf(agent.id, part, link, read)!)
    };
    return { ...agent, package: value };
  }

  /** The item with the link of its part, or as it is when the part is not linked. */
  private withLink<T extends object>(agentId: string, part: string, item: T): T {
    const link = this.links.get(agentId);
    const value = link ? this.partLinkOf(agentId, part, link, null) : null;
    return value ? { ...item, package: value } : item;
  }

  private partLinkOf(agentId: string, part: string, link: AgentLink, read: HostRead | null): PackagePartLink | null {
    const partLink = link.parts.get(part);
    if (!partLink) {
      return null;
    }
    const host = read ?? this.read(agentId);
    return {
      part,
      packageId: link.packageId,
      packageVersion: partLink.packageVersion,
      lockedFields: [...partLink.lockedFields],
      hash: partLink.hash,
      changedLocally: host ? this.hostHash(host, part) !== partLink.hash : false,
      linkedAt: partLink.linkedAt,
      linkedBy: partLink.linkedBy
    };
  }

  /** What the mock has of one agent, read through its own endpoints; `null` for an unknown agent. */
  private read(agentId: string): HostRead | null {
    const agent = this.agents().find(item => item.id === agentId);
    if (!agent) {
      return null;
    }
    const path = `admin/agents/${encodeURIComponent(agentId)}`;
    const configuration = this.context.call('GET', `${path}/configuration`);
    const handOffs = this.context.call('GET', `${path}/hand-offs`);
    const metadata = this.context.call('GET', `${path}/metadata`);
    return {
      agent,
      configuration: configuration.status === 200 ? configuration.body as AgentRunConfiguration : null,
      handOffs: handOffs.status === 200 ? handOffs.body as AgentHandOffs : null,
      metadata: metadata.status === 200 ? metadata.body as AgentMetadata[] : []
    };
  }

  private agents(): AdminAgent[] {
    const result = this.context.call('GET', 'admin/agents');
    return result.status === 200 ? result.body as AdminAgent[] : [];
  }

  /** The SHA-256 of the canonical JSON of the host's part as a package holds it; `null` without the part. */
  private hostHash(read: HostRead, part: string): string | null {
    const value = partsOf(this.packageAgentOf(read)).get(part);
    return value === undefined ? null : mockSha256(canonicalJson(value));
  }

  /**
   * What a save may not change of a part: the agent's definition as it is in effect (for an agent in
   * code with its code's values), or the part as a package holds it.
   */
  private lockView(agentId: string, part: string): Record<string, unknown> | null {
    const read = this.read(agentId);
    if (!read) {
      return null;
    }
    if (part === 'agent') {
      return agentDefinitionOf(read.agent);
    }
    const value = partsOf(this.packageAgentOf(read)).get(part);
    return value === undefined ? null : part.startsWith('metadata:') ? { value } : value as Record<string, unknown>;
  }

  /** The agent as a package holds it, without locks and hashes. */
  private packageAgentOf(read: HostRead): PackageAgent {
    const { agent, configuration, handOffs, metadata } = read;
    const inCode = agent.source === 'code';
    const definition = inCode ? this.overlayOf(agent) : agentDefinitionOf(agent);
    const values = metadata.filter(entry => entry.source === (inCode ? 'override' : 'data'));
    // The projections are the format's JSON; the format's types name the same properties.
    const packageAgent: Record<string, unknown> = {
      id: agent.id,
      source: agent.source,
      ...(definition && Object.keys(definition).length > 0 ? { definition } : {}),
      ...(!inCode && configuration?.runSettings ? { runSettings: runSettingsOf(configuration.runSettings) } : {}),
      ...nonEmpty('skills', (configuration?.dataSkills ?? []).map(skillOf)),
      ...nonEmpty('triggers', (configuration?.dataTriggers ?? []).map(triggerOf)),
      ...nonEmpty('consultations', (configuration?.dataConsultations ?? []).map(consultationOf)),
      ...nonEmpty('handOffs', (handOffs?.dataHandOffs ?? []).map(handOff => ({
        subjectType: handOff.subjectType,
        sources: [...handOff.sources],
        description: handOff.description,
        notifySource: handOff.notifySource,
        maxPerSubjectPerDay: handOff.maxPerSubjectPerDay
      })))
    };
    if (values.length > 0) {
      packageAgent['metadata'] = Object.fromEntries(values.map(entry => [entry.name, entry.value]));
    }
    if (!agent.shadowOf && (agent.groups?.length ?? 0) > 0) {
      packageAgent['groups'] = [...agent.groups!];
    }
    return packageAgent as unknown as PackageAgent;
  }

  /** The overrides of an agent in code: the fields of its definition that differ from its code. */
  private overlayOf(agent: AdminAgent): Record<string, unknown> {
    const code = this.context.codeDefinition(agent.id);
    if (!code || !agent.isOverridden) {
      return {};
    }
    const definition = agent as unknown as Record<string, unknown>;
    const inCode = code as unknown as Record<string, unknown>;
    return Object.fromEntries(codeAgentFields
      .filter(field => !sameValue(definition[field], inCode[field]))
      .map(field => [field, definition[field]]));
  }
}

/** The part of the administration a request saves or removes, or the whole agent it deletes or resets. */
function guardedPart(method: string, segments: readonly string[]): { agentId: string; part: string | null; removal: boolean } | null {
  if (segments[0] !== 'admin' || segments[1] !== 'agents' || segments[2] === undefined) {
    return null;
  }
  const [, , agentId, area, id, action, child] = segments;
  if (segments.length === 3) {
    return method === 'PUT' ? { agentId, part: 'agent', removal: false } : method === 'DELETE' ? { agentId, part: null, removal: true } : null;
  }
  if (area === 'reset' && segments.length === 4) {
    return method === 'POST' ? { agentId, part: null, removal: false } : null;
  }
  if (area === 'run-settings' && segments.length === 4) {
    return method === 'PUT' || method === 'DELETE' ? { agentId, part: 'runSettings', removal: method === 'DELETE' } : null;
  }

  const kinds: Record<string, AgentPackagePartKind> = {
    skills: 'skill',
    triggers: 'trigger',
    consultations: 'consultation',
    'hand-offs': 'handOff',
    metadata: 'metadata'
  };
  const kind = area === undefined ? undefined : kinds[area];
  if (kind === undefined || id === undefined) {
    return null;
  }
  const part = `${kind}:${id}`;
  if (segments.length === 5) {
    return method === 'PUT' || method === 'DELETE' ? { agentId, part, removal: method === 'DELETE' } : null;
  }
  // Promoting a candidate makes it the live version, which the locks keep as the package has it.
  if ((kind === 'skill' || kind === 'trigger') && action === 'candidate' && child === 'promote' && segments.length === 7 && method === 'POST') {
    return { agentId, part, removal: false };
  }
  return null;
}

/**
 * The locked fields a save changed to something else than the package's value: promoting the
 * candidate an import made of the package's version is no change of a locked field.
 */
function lockedChanges(
  kind: AgentPackagePartKind,
  locked: readonly string[],
  before: Record<string, unknown> | null,
  after: Record<string, unknown> | null,
  packaged: Record<string, unknown> | null
): string[] {
  const fields = locked.includes(NH_ASSISTANT_PACKAGE_LOCK_ALL) ? NH_ASSISTANT_PACKAGE_LOCK_FIELDS[kind] : locked;
  return fields.filter(field => {
    const properties = lockProperties[kind][field] ?? [field];
    const changed = properties.some(property => !sameValue(before?.[property], after?.[property]));
    const asPackaged = packaged !== null && properties.every(property => sameValue(after?.[property], packaged[property]));
    return changed && !asPackaged;
  });
}

/** Why a lock of a package agent is invalid: `not-found` for a part it does not hold, `invalid` fields. */
function lockProblem(agent: PackageAgent, part: string, fields: readonly string[]): 'not-found' | 'invalid' | null {
  if (fields.length === 0) {
    return null;
  }
  if (!partsOf(agent).has(part)) {
    return 'not-found';
  }
  const kind = kindOf(part);
  const allowed = kind === 'agent' && agent.source === 'code'
    ? NH_ASSISTANT_PACKAGE_LOCK_FIELDS.agent.slice(0, codeAgentFields.length)
    : NH_ASSISTANT_PACKAGE_LOCK_FIELDS[kind];
  const whole = fields.length === 1 && fields[0] === NH_ASSISTANT_PACKAGE_LOCK_ALL;
  return whole || fields.every(field => allowed.includes(field)) ? null : 'invalid';
}

/** Why the agent of a package may not be imported onto the host's agent with its id, or `null`. */
function agentRefusal(agent: PackageAgent, host: AdminAgent | null, linkedToPackage: boolean): string | null {
  if (agent.source === 'code') {
    if (!host) {
      return 'assistant-agent-not-found';
    }
    return host.source === 'code' ? null : 'assistant-agent-not-code';
  }
  if (host && (host.source === 'code' || !linkedToPackage)) {
    return 'assistant-agent-exists';
  }
  return null;
}

/** The parts of a package agent by part key, in the format's order. */
function partsOf(agent: PackageAgent): Map<string, unknown> {
  const parts = new Map<string, unknown>();
  if (agent.definition) {
    parts.set('agent', agent.definition);
  }
  if (agent.runSettings) {
    parts.set('runSettings', agent.runSettings);
  }
  for (const skill of agent.skills ?? []) {
    parts.set(`skill:${skill.id}`, skill);
  }
  for (const trigger of agent.triggers ?? []) {
    parts.set(`trigger:${trigger.id}`, trigger);
  }
  for (const consultation of agent.consultations ?? []) {
    parts.set(`consultation:${consultation.subjectType}`, consultation);
  }
  for (const handOff of agent.handOffs ?? []) {
    parts.set(`handOff:${handOff.subjectType}`, handOff);
  }
  for (const [name, value] of Object.entries(agent.metadata ?? {})) {
    parts.set(`metadata:${name}`, value);
  }
  return parts;
}

function kindOf(part: string): AgentPackagePartKind {
  const colon = part.indexOf(':');
  return (colon < 0 ? part : part.slice(0, colon)) as AgentPackagePartKind;
}

function keyOf(part: string): string {
  const colon = part.indexOf(':');
  return colon < 0 ? '' : part.slice(colon + 1);
}

/** Whether a part key names a known kind, with a dash-case key for the kinds that take one. */
function validPartKey(part: string): boolean {
  if (part === 'agent' || part === 'runSettings') {
    return true;
  }
  const kind = kindOf(part);
  return ['skill', 'trigger', 'consultation', 'handOff', 'metadata'].includes(kind) && mockDashCase.test(keyOf(part));
}

/** The definition of an agent as a package holds an agent created here. */
function agentDefinitionOf(agent: AdminAgent): Record<string, unknown> {
  return {
    displayName: agent.displayName,
    description: agent.description,
    instructions: agent.instructionsAsset ? null : agent.instructions,
    instructionsAsset: agent.instructionsAsset ?? null,
    toolSelectors: [...agent.toolSelectors],
    requiredPolicy: agent.requiredPolicy,
    autonomy: agent.autonomy,
    offeredInChat: agent.offeredInChat ?? true,
    worksOnSubjectTypes: agent.worksOnSubjectTypes ?? null,
    worksOnAllSubjects: agent.worksOnAllSubjects ?? null,
    worksOnApplication: agent.worksOnApplication ?? null,
    perDivision: agent.perDivision ?? null,
    runPolicy: agent.runPolicy ?? null,
    viewPolicy: agent.viewPolicy ?? null,
    profile: agent.profile ?? null,
    dataClassification: agent.dataClassification ?? null,
    shadowOf: agent.shadowOf ?? null
  };
}

function runSettingsOf(settings: NonNullable<AgentRunConfiguration['runSettings']>): Record<string, unknown> {
  return {
    accountability: settings.accountability,
    delegateFromInitiator: settings.delegateFromInitiator,
    allowGatewayReads: settings.allowGatewayReads,
    elevatedReads: settings.elevatedReads,
    approvalPolicy: settings.approvalPolicy,
    retryCount: settings.retryCount,
    dailyToolCalls: settings.dailyToolCalls,
    dailyTokens: settings.dailyTokens,
    maxConcurrentRuns: settings.maxConcurrentRuns,
    maxRunsPerSubjectPerDay: settings.maxRunsPerSubjectPerDay,
    maxTokensPerRun: settings.maxTokensPerRun,
    triageProfile: settings.triageProfile,
    ruleHandler: settings.ruleHandler,
    acceptsFiles: settings.acceptsFiles ?? false,
    maxFileBytes: settings.maxFileBytes ?? null,
    maxFilePages: settings.maxFilePages ?? null,
    maxFilesPerTurn: settings.maxFilesPerTurn ?? null,
    maxFileTokensPerTurn: settings.maxFileTokensPerTurn ?? null
  };
}

function skillOf(skill: AgentRunConfiguration['dataSkills'][number]): Record<string, unknown> {
  return {
    id: skill.skillId,
    description: skill.description,
    instructions: skill.instructionsAsset ? null : skill.instructions,
    instructionsAsset: skill.instructionsAsset ?? null,
    tools: [...skill.tools],
    profile: skill.profile,
    ruleHandler: skill.ruleHandler,
    maxTokensPerRun: skill.maxTokensPerRun,
    dailyToolCalls: skill.dailyToolCalls,
    dailyTokens: skill.dailyTokens,
    approvalPolicy: skill.approvalPolicy,
    dataClassification: skill.dataClassification ?? null,
    acceptsFiles: skill.acceptsFiles ?? false,
    maxFileBytes: skill.maxFileBytes ?? null,
    maxFilePages: skill.maxFilePages ?? null,
    maxFilesPerTurn: skill.maxFilesPerTurn ?? null,
    maxFileTokensPerTurn: skill.maxFileTokensPerTurn ?? null
  };
}

function triggerOf(trigger: AgentRunConfiguration['dataTriggers'][number]): Record<string, unknown> {
  return {
    id: trigger.triggerId,
    source: trigger.source,
    skill: trigger.skillId,
    event: trigger.event,
    filters: [...(trigger.filters ?? [])],
    cron: trigger.cron,
    timeZone: trigger.timeZone,
    subjectSet: trigger.subjectSet,
    message: trigger.message,
    conflict: trigger.conflict,
    debounceSeconds: trigger.debounceSeconds
  };
}

function consultationOf(consultation: NonNullable<AgentRunConfiguration['dataConsultations']>[number]): Record<string, unknown> {
  return {
    subjectType: consultation.subjectType,
    description: consultation.description,
    instructions: consultation.instructions,
    minimumRole: consultation.minimumRole,
    askerPolicy: consultation.askerPolicy,
    routes: [...consultation.routes],
    proposalRole: consultation.proposalRole,
    proposalPolicy: consultation.proposalPolicy,
    toolSelectors: consultation.toolSelectors ?? null,
    maxToolCalls: consultation.maxToolCalls,
    timeoutSeconds: consultation.timeoutSeconds,
    maxAnswerChars: consultation.maxAnswerChars,
    maxHistoryMessages: consultation.maxHistoryMessages,
    allowRequests: consultation.allowRequests,
    exposeToMcp: consultation.exposeToMcp,
    dataClassification: consultation.dataClassification
  };
}

/** `{ [name]: items }` for a list with items, otherwise nothing, as the format leaves empty lists out. */
function nonEmpty<K extends string, T>(name: K, items: T[]): Partial<Record<K, T[]>> {
  return items.length > 0 ? { [name]: items } as Record<K, T[]> : {};
}

/**
 * Reads a package strictly, as the server does: `assistant-package-too-large` above 1 MB or 20
 * agents; `assistant-package-format-unsupported` for another format, a newer format version, an
 * unknown property, a missing required one or an agent id given twice.
 */
function readPackage(value: unknown): { package: AgentPackage } | NhAssistantMockResult {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    return failure('assistant-package-format-unsupported');
  }
  if (new TextEncoder().encode(JSON.stringify(value)).length > NH_ASSISTANT_AGENT_PACKAGE_MAX_BYTES) {
    return failure('assistant-package-too-large');
  }

  const root = value as Record<string, unknown>;
  if (root['format'] !== NH_ASSISTANT_AGENT_PACKAGE_FORMAT
    || typeof root['formatVersion'] !== 'number'
    || !Number.isInteger(root['formatVersion'])
    || root['formatVersion'] < 1
    || root['formatVersion'] > NH_ASSISTANT_AGENT_PACKAGE_FORMAT_VERSION) {
    return failure('assistant-package-format-unsupported');
  }
  const agents = root['agents'];
  if (!fits(root, 'root') || !fits(root['package'], 'package') || !Array.isArray(agents)) {
    return failure('assistant-package-format-unsupported');
  }
  if (agents.length > NH_ASSISTANT_AGENT_PACKAGE_MAX_AGENTS) {
    return failure('assistant-package-too-large');
  }

  const info = root['package'] as Record<string, unknown>;
  const ids = new Set<string>();
  for (const item of agents) {
    if (!fitsAgent(item)) {
      return failure('assistant-package-format-unsupported');
    }
    const id = (item as Record<string, unknown>)['id'] as string;
    if (ids.has(id)) {
      return failure('assistant-package-format-unsupported');
    }
    ids.add(id);
  }
  if (agents.length === 0) {
    return failure('assistant-package-format-unsupported');
  }

  const errors: Record<string, string[]> = {};
  if (typeof info['id'] !== 'string' || !mockDashCase.test(info['id']) || info['id'].length > maxPackageId) {
    errors['package.id'] = ['invalid'];
  }
  if (typeof info['version'] !== 'number' || !Number.isInteger(info['version']) || info['version'] < 1) {
    errors['package.version'] = ['invalid'];
  }
  if (info['description'] !== undefined && info['description'] !== null
    && (typeof info['description'] !== 'string' || info['description'].length > maxPackageDescription)) {
    errors['package.description'] = ['too-long'];
  }
  return Object.keys(errors).length > 0 ? failure('assistant-validation', errors) : { package: structuredClone(value) as AgentPackage };
}

function fitsAgent(value: unknown): boolean {
  if (!fits(value, 'agent')) {
    return false;
  }
  const agent = value as Record<string, unknown>;
  if (typeof agent['id'] !== 'string' || (agent['source'] !== 'admin' && agent['source'] !== 'code')) {
    return false;
  }
  if (agent['source'] === 'admin' && (agent['definition'] === undefined || agent['definition'] === null)) {
    return false;
  }
  const definition = agent['definition'] as Record<string, unknown> | undefined | null;
  if (definition !== undefined && definition !== null
    && (!fits(definition, 'definition') || (definition['instructionsAsset'] && !fits(definition['instructionsAsset'], 'instructionsAsset')))) {
    return false;
  }
  if (agent['runSettings'] !== undefined && agent['runSettings'] !== null && !fits(agent['runSettings'], 'runSettings')) {
    return false;
  }
  const lists: [string, string][] = [['skills', 'skill'], ['triggers', 'trigger'], ['consultations', 'consultation'], ['handOffs', 'handOff']];
  for (const [list, shape] of lists) {
    const items = agent[list];
    if (items !== undefined && items !== null && (!Array.isArray(items) || !items.every(item => fits(item, shape)))) {
      return false;
    }
  }
  for (const map of ['metadata', 'locks', 'hashes']) {
    const item = agent[map];
    if (item !== undefined && item !== null && (typeof item !== 'object' || Array.isArray(item))) {
      return false;
    }
  }
  const groups = agent['groups'];
  return groups === undefined || groups === null || (Array.isArray(groups) && groups.every(group => typeof group === 'string'));
}

/** Whether an object has every required property of its shape and none it does not know. */
function fits(value: unknown, shape: keyof typeof formatShapes): boolean {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    return false;
  }
  const { required, optional } = formatShapes[shape];
  const record = value as Record<string, unknown>;
  return required.every(name => record[name] !== undefined && record[name] !== null)
    && Object.keys(record).every(name => required.includes(name) || optional.includes(name));
}

function base(agentId: string, part: string, lockedFields: readonly string[]): PlannedPart {
  return {
    agentId,
    part,
    outcome: 'unchanged',
    changedLocally: false,
    code: null,
    errors: null,
    lockedFields: [...lockedFields],
    candidateVersion: null,
    hashAfter: null,
    viewAfter: null
  };
}

function leftBehind(agentId: string, part: string): PlannedPart {
  return { ...base(agentId, part, []), outcome: 'left-behind' };
}

/** A refused part with the endpoint's field errors, or its code without them. */
function refusalOf(agentId: string, part: string, result: NhAssistantMockResult, lockedFields: readonly string[]): PlannedPart {
  const body = (result.body ?? {}) as { code?: string; errors?: Record<string, string[]> };
  const errors = body.errors && Object.keys(body.errors).length > 0 ? body.errors : null;
  return {
    ...base(agentId, part, lockedFields),
    outcome: 'refused',
    code: errors ? null : body.code ?? 'assistant-validation',
    errors
  };
}

/** The field errors of `assistant-package-refused`: `{agentId}/{part}/{field}`, or `{agentId}/{part}` with the code. */
function refusedErrors(parts: readonly PlannedPart[]): Record<string, string[]> {
  const errors: Record<string, string[]> = {};
  for (const part of parts.filter(item => item.outcome === 'refused')) {
    if (part.errors) {
      for (const [field, codes] of Object.entries(part.errors)) {
        errors[`${part.agentId}/${part.part}/${field}`] = [...codes];
      }
    } else {
      errors[`${part.agentId}/${part.part}`] = [part.code ?? 'assistant-validation'];
    }
  }
  return errors;
}

function publicPart({ hashAfter: _, viewAfter: __, ...part }: PlannedPart): AgentPackagePart {
  return part;
}

/** A trimmed text, or `null` for none. */
function text(value: unknown): string | null {
  return typeof value === 'string' && value.trim().length > 0 ? value.trim() : null;
}

/** Whether two values are the same as a package holds them: `null` and missing alike. */
function sameValue(left: unknown, right: unknown): boolean {
  return canonicalJson(left ?? null) === canonicalJson(right ?? null);
}

/** A copy of a value without `null` and `undefined` properties, as the format writes it. */
function withoutNulls<T>(value: T): T {
  if (Array.isArray(value)) {
    return value.map(item => withoutNulls(item)) as T;
  }
  if (value && typeof value === 'object') {
    return Object.fromEntries(Object.entries(value)
      .filter(([, item]) => item !== null && item !== undefined)
      .map(([key, item]) => [key, withoutNulls(item)])) as T;
  }
  return value;
}

/**
 * The canonical JSON of a value, as the format hashes a part: object properties ordered by name
 * (ordinal), properties with a `null` value left out, no white space.
 */
export function canonicalJson(value: unknown): string {
  if (value === null || value === undefined) {
    return 'null';
  }
  if (Array.isArray(value)) {
    return `[${value.map(item => canonicalJson(item)).join(',')}]`;
  }
  if (typeof value === 'object') {
    const entries = Object.entries(value as Record<string, unknown>)
      .filter(([, item]) => item !== null && item !== undefined)
      .sort(([left], [right]) => (left < right ? -1 : left > right ? 1 : 0));
    return `{${entries.map(([key, item]) => `${JSON.stringify(key)}:${canonicalJson(item)}`).join(',')}}`;
  }
  return JSON.stringify(value);
}

const sha256Constants = new Uint32Array([
  0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
  0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
  0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
  0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
  0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
  0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
  0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
  0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2
]);

/**
 * The SHA-256 of the UTF-8 bytes of a text, lowercase hex, computed synchronously so an import can
 * hash its parts between two of its writes.
 */
export function mockSha256(input: string): string {
  const bytes = new TextEncoder().encode(input);
  const length = ((bytes.length + 9 + 63) >> 6) << 6;
  const data = new Uint8Array(length);
  data.set(bytes);
  data[bytes.length] = 0x80;
  const view = new DataView(data.buffer);
  view.setUint32(length - 8, Math.floor(bytes.length / 0x20000000));
  view.setUint32(length - 4, (bytes.length << 3) >>> 0);

  const hash = new Uint32Array([0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19]);
  const words = new Uint32Array(64);
  const rotate = (value: number, bits: number): number => (value >>> bits) | (value << (32 - bits));
  for (let offset = 0; offset < length; offset += 64) {
    for (let index = 0; index < 16; index++) {
      words[index] = view.getUint32(offset + index * 4);
    }
    for (let index = 16; index < 64; index++) {
      const low = words[index - 15];
      const high = words[index - 2];
      const s0 = rotate(low, 7) ^ rotate(low, 18) ^ (low >>> 3);
      const s1 = rotate(high, 17) ^ rotate(high, 19) ^ (high >>> 10);
      words[index] = (words[index - 16] + s0 + words[index - 7] + s1) >>> 0;
    }

    let [a, b, c, d, e, f, g, h] = hash;
    for (let index = 0; index < 64; index++) {
      const s1 = rotate(e, 6) ^ rotate(e, 11) ^ rotate(e, 25);
      const choice = (e & f) ^ (~e & g);
      const first = (h + s1 + choice + sha256Constants[index] + words[index]) >>> 0;
      const s0 = rotate(a, 2) ^ rotate(a, 13) ^ rotate(a, 22);
      const majority = (a & b) ^ (a & c) ^ (b & c);
      const second = (s0 + majority) >>> 0;
      h = g;
      g = f;
      f = e;
      e = (d + first) >>> 0;
      d = c;
      c = b;
      b = a;
      a = (first + second) >>> 0;
    }
    hash[0] = (hash[0] + a) >>> 0;
    hash[1] = (hash[1] + b) >>> 0;
    hash[2] = (hash[2] + c) >>> 0;
    hash[3] = (hash[3] + d) >>> 0;
    hash[4] = (hash[4] + e) >>> 0;
    hash[5] = (hash[5] + f) >>> 0;
    hash[6] = (hash[6] + g) >>> 0;
    hash[7] = (hash[7] + h) >>> 0;
  }
  return [...hash].map(word => word.toString(16).padStart(8, '0')).join('');
}
