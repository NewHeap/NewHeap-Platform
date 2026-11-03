import {
  AdminAgent,
  RuleScript,
  RuleScriptAlert,
  RuleScriptCall,
  RuleScriptCatalog,
  RuleScriptCheck,
  RuleScriptComparison,
  RuleScriptLine,
  RuleScriptRunRecord,
  RuleScriptSubject,
  RuleScriptSubjectRun,
  RuleScriptTool,
  RuleScriptVersion,
  RuleScriptWrite
} from '@newheap/platform-ai-chat';
import { NhAssistantMockResult, mockFailure as failure, mockHashText as hashText } from './nh-assistant-mock-admin-shared';
import {
  NhAssistantMockRuleScriptAgent,
  NhAssistantMockRuleScriptCeiling,
  NhAssistantMockRuleScriptTool,
  NhAssistantMockSubjectType
} from './nh-assistant-mock.models';

const maxScriptLength = 10_000;
const maxReason = 500;
const maxDryRunSubjects = 20;
const maxWritesPerVersion = 32;
const maxShadowRunsKept = 20;
const currentUser = 'current-user';
const systemType = 'nh-system';
const systemGlobalId = 'global';
const dryRunWrite = 'assistant-rule-script-dry-run';
const defaultSummary = 'The rule script ran.';

/** The declarations the server's engine gives a script of `subject`, `inputs`, `run` and the platform functions. */
const platformDeclarations = [
  'declare const subject: { type: string; id: string; displayName: string | null; wholeApplication: boolean };',
  'declare const inputs: { triggerId: string | null; text: string; payload: unknown; createdAt: string }[];',
  'declare const run: { agentId: string; skillId: string | null; version: number; triggerId: string; runId: string | null; now: string; mode: "live" | "shadow" | "dry-run" };',
  'declare const alerts: {',
  '  raise(alert: { kind: string; key?: string; urgency?: "info" | "warning" | "urgent"; parameters?: Record<string, string | number | boolean>; recipients?: string[]; expiresAt?: string; subject?: { type: string; id: string } }): void;',
  '  resolve(alert: { kind: string; key?: string | null; subject?: { type: string; id: string } }): void;',
  '};',
  'declare function propose(proposal: { tool: string; input?: object; rationale?: string }): Promise<{ outcome: "created" | "already-open" | "recently-rejected" | "refused" | "not-needed" | "dry-run"; approvalId?: string; code?: string }>;',
  'declare function done(summary: string): void;',
  'declare function handOver(reason: string): void;',
  'declare function useSkill(skillId: string, reason: string): void;'
].join('\n');

/** What the rule scripts of the mock read about agents and subjects. */
export interface NhAssistantMockRuleScriptHost {
  find(agentId: string): AdminAgent | undefined;
  subjectTypes(): readonly NhAssistantMockSubjectType[];
}

/** One rule script of the mock: the versions of an agent's own rules or of one skill. */
interface MockRuleScript {
  revision: number;
  liveVersion: number | null;
  candidateVersion: number | null;
  versions: RuleScriptVersion[];
  dryRuns: Map<number, RuleScriptRunRecord>;
  shadowRuns: Map<number, RuleScriptRunRecord[]>;
}

/**
 * Rule scripts of the mock API (RB-1 to RB-6): `admin/agents/{id}/rule-script` and
 * `admin/agents/{id}/skills/{skillId}/rule-script` with `catalog`, `check`, `versions`, the dry run, the shadow
 * runs, `live`, `shadow`, `discard` and `compare`. The scenario's `admin.ruleScripts` is the code's ceiling: the
 * agents that may have rule scripts, their writes, writes per run, destructive tools and four eyes, and what
 * their scripts see. Versions keep the server's rules: one candidate at a time, live supersedes, every change
 * expects the revision, four eyes refuses the author, and the signed-in administrator is `current-user`.
 *
 * The mock does not run scripts. Its check balances brackets and quotes, and its dry run reads which tools and
 * alert kinds a script names: each named tool is a call in order, a write it opted in to is recorded as
 * `would-propose` (or `would-run` for a tool without approval), a write it did not opt in to stops it with
 * `ai-script-tool-refused`, and a named alert kind is raised with the key `mock`.
 */
export class NhAssistantMockRuleScripts {
  private readonly scripts = new Map<string, MockRuleScript>();
  private readonly ceiling: NhAssistantMockRuleScriptCeiling;

  constructor(
    scenario: NhAssistantMockRuleScriptCeiling | undefined,
    private readonly host: NhAssistantMockRuleScriptHost,
    private readonly now: () => string
  ) {
    this.ceiling = scenario ?? { agents: {} };
    for (const [agentId, agent] of Object.entries(this.ceiling.agents)) {
      for (const seed of agent.versions ?? []) {
        const skillId = seed.skillId ?? null;
        const saved = this.add(agentId, skillId, {
          script: seed.script,
          writes: seed.writes ?? [],
          live: seed.status === 'live',
          shadow: seed.shadow === true,
          reason: seed.reason ?? null,
          authorActorId: seed.authorActorId ?? currentUser,
          authorAgentId: seed.authorAgentId ?? null,
          createdAt: seed.createdAt ?? this.now()
        });
        for (const subject of seed.shadowRunsOn ?? []) {
          this.recordShadowRun(agentId, skillId, saved, subject);
        }
      }
    }
  }

  /** Routes `admin/agents/{id}/...`; `null` for a route that is not one of rule scripts. */
  handle(method: string, agentId: string, rest: string[], body: unknown, query: URLSearchParams): NhAssistantMockResult | null {
    let skillId: string | null = null;
    let parts = rest;
    if (rest[0] === 'skills' && rest[2] === 'rule-script') {
      skillId = rest[1];
      parts = rest.slice(3);
    } else if (rest[0] === 'rule-script') {
      parts = rest.slice(1);
    } else {
      return null;
    }

    const agent = this.host.find(agentId);
    if (!agent) {
      return failure('assistant-agent-not-found');
    }
    if (skillId !== null && !(agent.skills ?? []).some(skill => skill.skillId === skillId)) {
      return failure('assistant-skill-not-found');
    }

    const [first, second, third] = parts;
    if (parts.length === 0) {
      return method === 'GET' ? { status: 200, body: this.dto(agentId, skillId) } : { status: 405 };
    }
    if (parts.length === 1 && first === 'catalog') {
      return method === 'GET' ? this.catalog(agentId, skillId, query) : { status: 405 };
    }
    if (parts.length === 1 && first === 'check') {
      return method === 'POST' ? this.check(agentId, body) : { status: 405 };
    }
    if (parts.length === 1 && first === 'versions') {
      return method === 'POST' ? this.save(agentId, skillId, body) : { status: 405 };
    }
    if (parts.length === 1 && first === 'compare') {
      return method === 'GET' ? this.compare(agentId, skillId, query) : { status: 405 };
    }
    if (parts.length === 3 && first === 'versions') {
      const version = Number(second);
      if (!Number.isInteger(version)) {
        return failure('assistant-not-found');
      }
      switch (third) {
        case 'dry-run':
          return method === 'POST' ? this.dryRun(agentId, skillId, version, body) : method === 'GET' ? this.latestDryRun(agentId, skillId, version) : { status: 405 };
        case 'shadow-runs':
          return method === 'GET' ? { status: 200, body: this.scriptOf(agentId, skillId)?.shadowRuns.get(version) ?? [] } : { status: 405 };
        case 'live':
        case 'shadow':
        case 'discard':
          return method === 'POST' ? this.change(agentId, skillId, version, third, body) : { status: 405 };
        default:
          return failure('assistant-not-found');
      }
    }
    return failure('assistant-not-found');
  }

  private agentCeiling(agentId: string): NhAssistantMockRuleScriptAgent | undefined {
    return this.ceiling.agents[agentId];
  }

  private dto(agentId: string, skillId: string | null): RuleScript {
    const agent = this.agentCeiling(agentId);
    const script = this.scriptOf(agentId, skillId);
    const candidate = script?.versions.find(version => version.version === script.candidateVersion);
    return {
      agentId,
      skillId,
      allowed: agent !== undefined,
      revision: script?.revision ?? 0,
      liveVersion: script?.liveVersion ?? null,
      candidateVersion: script?.candidateVersion ?? null,
      candidateShadow: candidate?.shadow ?? false,
      codeRule: (skillId === null ? agent?.codeRule : agent?.skillCodeRules?.[skillId]) ?? null,
      ceiling: {
        writes: [...(agent?.writes ?? [])],
        maxWritesPerRun: agent?.maxWritesPerRun ?? 1,
        destructive: [...(agent?.destructive ?? [])],
        requireFourEyes: this.ceiling.requireFourEyes === true
      },
      versions: [...(script?.versions ?? [])].reverse().map(version => ({ ...version, revision: script!.revision }))
    };
  }

  private catalog(agentId: string, skillId: string | null, query: URLSearchParams): NhAssistantMockResult {
    const agent = this.agentCeiling(agentId);
    if (!agent) {
      return failure('assistant-rule-scripts-not-allowed');
    }

    const asked = query.has('writes')
      ? new Set((query.get('writes') ?? '').split(',').map(item => item.trim()).filter(item => item.length > 0))
      : new Set(agent.writes ?? []);
    const body: RuleScriptCatalog = {
      agentId,
      skillId,
      tools: (agent.tools ?? []).map(tool => this.toolOf(agent, tool, asked)),
      alertKinds: [...(agent.alertKinds ?? [])],
      platformFunctions: platformDeclarations,
      maxToolCalls: 20,
      timeoutSeconds: 5,
      maxScriptLength,
      maxWritesPerRun: agent.maxWritesPerRun ?? 1
    };
    return { status: 200, body };
  }

  private toolOf(agent: NhAssistantMockRuleScriptAgent, tool: NhAssistantMockRuleScriptTool, asked: ReadonlySet<string>): RuleScriptTool {
    const write = tool.write === true;
    const inCeiling = write && (agent.writes ?? []).includes(tool.toolId);
    const destructive = tool.destructive === true;
    let refusal: string | null = null;
    if (write && !(inCeiling && asked.has(tool.toolId))) {
      refusal = 'it changes data and this rule script version may not call it. Opt it in for the version within the code\'s ceiling, or propose it with propose().';
    } else if (destructive && !(agent.destructive ?? []).includes(tool.toolId)) {
      refusal = 'it is destructive and the code\'s ceiling does not let it run in a script.';
    }
    return {
      name: tool.name,
      toolId: tool.toolId,
      write,
      inCeiling,
      callable: refusal === null,
      destructive,
      signature: tool.signature ?? `${tool.name}(input: object): Promise<{ success: boolean; data?: unknown; code?: string }>;`,
      refusal
    };
  }

  private check(agentId: string, body: unknown): NhAssistantMockResult {
    const script = (body as { script?: unknown } | undefined)?.script;
    if (typeof script !== 'string') {
      return failure('assistant-validation', { script: ['required'] });
    }
    if (!this.agentCeiling(agentId)) {
      return failure('assistant-rule-scripts-not-allowed');
    }

    return { status: 200, body: checkScript(script) };
  }

  private save(agentId: string, skillId: string | null, body: unknown): NhAssistantMockResult {
    const agent = this.agentCeiling(agentId);
    if (!agent) {
      return failure('assistant-rule-scripts-not-allowed');
    }

    const request = (body ?? {}) as { script?: unknown; writes?: unknown; expectedRevision?: unknown; reason?: unknown; live?: unknown; shadow?: unknown };
    const script = typeof request.script === 'string' ? request.script : '';
    const errors: Record<string, string[]> = {};
    if (script.trim().length === 0) {
      errors['script'] = ['required'];
    } else if (script.length > maxScriptLength) {
      errors['script'] = ['too-long'];
    } else if (!checkScript(script).valid) {
      errors['script'] = ['invalid'];
    }
    const reason = typeof request.reason === 'string' && request.reason.trim().length > 0 ? request.reason.trim() : null;
    if (reason !== null && reason.length > maxReason) {
      errors['reason'] = ['too-long'];
    }
    if (Object.keys(errors).length > 0) {
      return failure('assistant-validation', errors);
    }

    const writes = [...new Set(Array.isArray(request.writes) ? request.writes.filter((item): item is string => typeof item === 'string').map(item => item.trim()) : [])].sort();
    if (writes.length > maxWritesPerVersion) {
      return failure('assistant-rule-script-write-not-allowed', { writes: ['too-long'] });
    }
    if (writes.some(write => !(agent.writes ?? []).includes(write))) {
      return failure('assistant-rule-script-write-not-allowed', { writes: ['not-allowed'] });
    }
    const live = request.live === true;
    if (live && this.ceiling.requireFourEyes === true) {
      return failure('assistant-rule-script-four-eyes');
    }
    const expected = typeof request.expectedRevision === 'number' ? request.expectedRevision : 0;
    if (expected !== (this.scriptOf(agentId, skillId)?.revision ?? 0)) {
      return failure('assistant-version-conflict');
    }

    const saved = this.add(agentId, skillId, {
      script,
      writes,
      live,
      shadow: !live && request.shadow === true,
      reason,
      authorActorId: currentUser,
      authorAgentId: null,
      createdAt: this.now()
    });
    return { status: 200, body: { ...saved, revision: this.scriptOf(agentId, skillId)!.revision } };
  }

  private change(agentId: string, skillId: string | null, version: number, change: 'live' | 'shadow' | 'discard', body: unknown): NhAssistantMockResult {
    if (!this.agentCeiling(agentId)) {
      return failure('assistant-rule-scripts-not-allowed');
    }
    const request = (body ?? {}) as { expectedRevision?: unknown; reason?: unknown; shadow?: unknown };
    const reason = typeof request.reason === 'string' && request.reason.trim().length > 0 ? request.reason.trim() : null;
    if (reason !== null && reason.length > maxReason) {
      return failure('assistant-validation', { reason: ['too-long'] });
    }
    const script = this.scriptOf(agentId, skillId);
    const expected = typeof request.expectedRevision === 'number' ? request.expectedRevision : 0;
    if (expected !== (script?.revision ?? 0)) {
      return failure('assistant-version-conflict');
    }
    const target = script?.versions.find(item => item.version === version);
    if (!script || !target) {
      return failure('assistant-rule-script-version-not-found');
    }
    if (target.status !== 'candidate') {
      return failure('assistant-rule-script-not-candidate');
    }

    const now = this.now();
    switch (change) {
      case 'live': {
        if (this.ceiling.requireFourEyes === true && target.authorActorId === currentUser) {
          return failure('assistant-rule-script-four-eyes');
        }
        this.end(script, script.liveVersion, 'superseded', now);
        target.status = 'live';
        target.shadow = false;
        target.liveAt = now;
        target.liveBy = currentUser;
        script.liveVersion = target.version;
        script.candidateVersion = null;
        script.revision++;
        return { status: 200, body: { version: { ...target, revision: script.revision }, latestDryRun: script.dryRuns.get(version) ?? null } };
      }
      case 'shadow':
        target.shadow = request.shadow === true;
        script.revision++;
        return { status: 200, body: { ...target, revision: script.revision } };
      default:
        this.end(script, version, 'discarded', now);
        script.candidateVersion = null;
        script.revision++;
        return { status: 200, body: { ...target, revision: script.revision } };
    }
  }

  private dryRun(agentId: string, skillId: string | null, version: number, body: unknown): NhAssistantMockResult {
    const agent = this.agentCeiling(agentId);
    if (!agent) {
      return failure('assistant-rule-scripts-not-allowed');
    }
    const subjects = (body as { subjects?: unknown } | undefined)?.subjects;
    const valid = Array.isArray(subjects)
      && subjects.length > 0
      && subjects.length <= maxDryRunSubjects
      && subjects.every(item => typeof item?.type === 'string' && item.type.length > 0 && typeof item?.id === 'string' && item.id.length > 0);
    if (!valid) {
      return failure('assistant-rule-script-subjects-invalid', { subjects: ['invalid'] });
    }
    const script = this.scriptOf(agentId, skillId);
    const found = script?.versions.find(item => item.version === version);
    if (!script || !found) {
      return failure('assistant-rule-script-version-not-found');
    }

    const runs = (subjects as RuleScriptSubject[]).map(subject => this.simulate(agent, found, subject));
    const record: RuleScriptRunRecord = {
      id: crypto.randomUUID(),
      agentId,
      skillId,
      version,
      kind: 'dry-run',
      runId: null,
      ranBy: currentUser,
      ranAt: this.now(),
      subjectCount: runs.length,
      completedCount: runs.filter(run => run.status === 'completed').length,
      subjects: runs
    };
    // The latest dry run of a version is kept; the one before goes.
    script.dryRuns.set(version, record);
    return { status: 200, body: record };
  }

  private latestDryRun(agentId: string, skillId: string | null, version: number): NhAssistantMockResult {
    const record = this.scriptOf(agentId, skillId)?.dryRuns.get(version);
    return record ? { status: 200, body: record } : { status: 204 };
  }

  private compare(agentId: string, skillId: string | null, query: URLSearchParams): NhAssistantMockResult {
    const version = Number(query.get('version'));
    if (!query.has('version') || !Number.isInteger(version)) {
      return failure('assistant-validation', { version: ['required'] });
    }
    const script = this.scriptOf(agentId, skillId);
    const compared = script?.versions.find(item => item.version === version);
    if (!script || !compared) {
      return failure('assistant-rule-script-version-not-found');
    }

    const asked = query.has('with') ? Number(query.get('with')) : null;
    // By default the live version, or, for the live version itself, the one before it.
    const otherNumber = asked
      ?? (script.liveVersion !== null && script.liveVersion !== version
        ? script.liveVersion
        : Math.max(0, ...script.versions.filter(item => item.version < version).map(item => item.version)));
    const other = script.versions.find(item => item.version === otherNumber) ?? null;
    if (asked !== null && !other) {
      return failure('assistant-rule-script-version-not-found');
    }

    const body: RuleScriptComparison = {
      version,
      otherVersion: other?.version ?? 0,
      lines: compareLines(other?.script ?? '', compared.script),
      writesAdded: compared.writes.filter(write => !(other?.writes ?? []).includes(write)),
      writesRemoved: (other?.writes ?? []).filter(write => !compared.writes.includes(write)),
      dryRun: script.dryRuns.get(version) ?? null,
      otherDryRun: other ? script.dryRuns.get(other.version) ?? null : null
    };
    return { status: 200, body };
  }

  private add(
    agentId: string,
    skillId: string | null,
    next: { script: string; writes: string[]; live: boolean; shadow: boolean; reason: string | null; authorActorId: string; authorAgentId: string | null; createdAt: string }
  ): RuleScriptVersion {
    const key = keyOf(agentId, skillId);
    const script: MockRuleScript = this.scripts.get(key) ?? { revision: 0, liveVersion: null, candidateVersion: null, versions: [], dryRuns: new Map(), shadowRuns: new Map() };
    this.scripts.set(key, script);

    const saved: RuleScriptVersion = {
      version: script.versions.length + 1,
      status: next.live ? 'live' : 'candidate',
      script: next.script,
      scriptHash: hashText(next.script),
      writes: next.writes,
      authorActorId: next.authorActorId,
      authorAgentId: next.authorAgentId,
      reason: next.reason,
      createdAt: next.createdAt,
      liveAt: next.live ? next.createdAt : null,
      liveBy: next.live ? next.authorActorId : null,
      endedAt: null,
      endedBy: null,
      shadow: !next.live && next.shadow,
      revision: 0
    };
    // A newer version replaces the candidate that waited; one that goes live supersedes the live version.
    this.end(script, script.candidateVersion, 'discarded', next.createdAt);
    script.candidateVersion = null;
    if (next.live) {
      this.end(script, script.liveVersion, 'superseded', next.createdAt);
      script.liveVersion = saved.version;
    } else {
      script.candidateVersion = saved.version;
    }
    script.versions.push(saved);
    script.revision++;
    return saved;
  }

  private end(script: MockRuleScript, version: number | null, status: 'superseded' | 'discarded', at: string): void {
    const ended = script.versions.find(item => item.version === version);
    if (ended) {
      ended.status = status;
      ended.shadow = false;
      ended.endedAt = at;
      ended.endedBy = currentUser;
    }
  }

  private recordShadowRun(agentId: string, skillId: string | null, version: RuleScriptVersion, subject: RuleScriptSubject): void {
    const agent = this.agentCeiling(agentId);
    const script = this.scriptOf(agentId, skillId);
    if (!agent || !script) {
      return;
    }

    const run = this.simulate(agent, version, subject);
    const records = script.shadowRuns.get(version.version) ?? [];
    records.unshift({
      id: crypto.randomUUID(),
      agentId,
      skillId,
      version: version.version,
      kind: 'shadow',
      runId: crypto.randomUUID(),
      ranBy: agentId,
      ranAt: this.now(),
      subjectCount: 1,
      completedCount: run.status === 'completed' ? 1 : 0,
      subjects: [run]
    });
    script.shadowRuns.set(version.version, records.slice(0, maxShadowRunsKept));
  }

  /** What a version would do on a subject, from the tools and alert kinds its script names. */
  private simulate(agent: NhAssistantMockRuleScriptAgent, version: RuleScriptVersion, subject: RuleScriptSubject): RuleScriptSubjectRun {
    const displayName = this.displayNameOf(subject);
    const run: RuleScriptSubjectRun = {
      subjectType: subject.type,
      subjectId: subject.id,
      displayName,
      status: 'completed',
      code: null,
      message: null,
      line: null,
      column: null,
      end: null,
      calls: [],
      writes: [],
      proposals: [],
      alerts: [],
      resolves: [],
      logs: []
    };
    if (displayName === null) {
      return { ...run, status: 'failed', code: 'assistant-subject-not-found', message: 'The subject was not found.' };
    }

    const named = (agent.tools ?? [])
      .map(tool => ({ tool, at: version.script.indexOf(`tools.${tool.name}(`) }))
      .filter(item => item.at >= 0)
      .sort((left, right) => left.at - right.at);
    const calls: RuleScriptCall[] = [];
    const writes: RuleScriptWrite[] = [];
    for (const { tool, at } of named) {
      const call = calls.length + 1;
      if (tool.write !== true) {
        calls.push({ call, tool: tool.name, toolId: tool.toolId, outcome: 'done', code: null, write: false });
        continue;
      }
      if (!version.writes.includes(tool.toolId)) {
        const position = positionOf(version.script, at);
        calls.push({ call, tool: tool.name, toolId: tool.toolId, outcome: 'refused', code: 'ai-script-tool-refused', write: true });
        return {
          ...run,
          status: 'stopped',
          code: 'ai-script-tool-refused',
          message: `The rule script version may not call ${tool.name}.`,
          line: position.line,
          column: position.column,
          calls
        };
      }
      calls.push({ call, tool: tool.name, toolId: tool.toolId, outcome: 'failed', code: dryRunWrite, write: true });
      writes.push({
        call,
        tool: tool.name,
        toolId: tool.toolId,
        outcome: tool.needsApproval === false ? 'would-run' : 'would-propose',
        input: JSON.stringify({ subjectId: subject.id })
      });
    }

    const alerts: RuleScriptAlert[] = (agent.alertKinds ?? [])
      .filter(kind => version.script.includes(`"${kind}"`) || version.script.includes(`'${kind}'`))
      .map(kind => ({
        kind,
        subjectType: subject.type,
        subjectId: subject.id,
        key: 'mock',
        urgency: null,
        parameters: {},
        recipients: [],
        expiresAt: null
      }));
    return { ...run, end: { kind: 'done', text: defaultSummary, skillId: null }, calls, writes, alerts };
  }

  private displayNameOf(subject: RuleScriptSubject): string | null {
    if (subject.type === systemType && subject.id === systemGlobalId) {
      return 'The whole application';
    }

    const found = this.host.subjectTypes()
      .find(type => type.id === subject.type)?.subjects
      ?.find(item => item.id === subject.id);
    return found ? found.displayName : null;
  }

  private scriptOf(agentId: string, skillId: string | null): MockRuleScript | undefined {
    return this.scripts.get(keyOf(agentId, skillId));
  }
}

function keyOf(agentId: string, skillId: string | null): string {
  return `${agentId}\u0000${skillId ?? ''}`;
}

/** The line and column, from 1, of an offset. */
function positionOf(text: string, offset: number): { line: number; column: number } {
  const lines = text.slice(0, offset).split('\n');
  return { line: lines.length, column: lines[lines.length - 1].length + 1 };
}

/**
 * The mock's check: brackets and quotes balance, outside strings and comments. It names the first place that
 * does not, as the server's parser names a syntax error; it does not check the whole grammar.
 */
function checkScript(script: string): RuleScriptCheck {
  if (script.length > maxScriptLength) {
    return { valid: false, code: 'ai-script-too-long', message: 'The script is longer than a version stores.', line: null, column: null };
  }

  const pairs: Record<string, string> = { ')': '(', ']': '[', '}': '{' };
  const open: { char: string; at: number }[] = [];
  let quote: { char: string; at: number } | null = null;
  for (let index = 0; index < script.length; index++) {
    const char = script[index];
    if (quote) {
      if (char === '\\') {
        index++;
      } else if (char === quote.char) {
        quote = null;
      } else if (char === '\n' && quote.char !== '`') {
        return problem(script, quote.at, 'Unterminated string');
      }
      continue;
    }
    if (char === '/' && script[index + 1] === '/') {
      const end = script.indexOf('\n', index);
      index = end < 0 ? script.length : end;
      continue;
    }
    if (char === '/' && script[index + 1] === '*') {
      const end = script.indexOf('*/', index + 2);
      if (end < 0) {
        return problem(script, index, 'Unterminated comment');
      }
      index = end + 1;
      continue;
    }
    if (char === '"' || char === '\'' || char === '`') {
      quote = { char, at: index };
    } else if (char === '(' || char === '[' || char === '{') {
      open.push({ char, at: index });
    } else if (char in pairs) {
      const last = open.pop();
      if (!last || last.char !== pairs[char]) {
        return problem(script, index, `Unexpected token ${char}`);
      }
    }
  }
  if (quote) {
    return problem(script, quote.at, 'Unterminated string');
  }
  if (open.length > 0) {
    return problem(script, script.length, 'Unexpected end of input');
  }

  return { valid: true, code: null, message: null, line: null, column: null };
}

function problem(script: string, offset: number, message: string): RuleScriptCheck {
  const position = positionOf(script, offset);
  return { valid: false, code: 'ai-script-syntax-error', message, line: position.line, column: position.column };
}

/** The other script turned into this one line by line, from their longest common subsequence. */
function compareLines(other: string, script: string): RuleScriptLine[] {
  const before = other.length > 0 ? other.split('\n') : [];
  const after = script.length > 0 ? script.split('\n') : [];
  const lengths: number[][] = Array.from({ length: before.length + 1 }, () => new Array<number>(after.length + 1).fill(0));
  for (let left = before.length - 1; left >= 0; left--) {
    for (let right = after.length - 1; right >= 0; right--) {
      lengths[left][right] = before[left] === after[right]
        ? lengths[left + 1][right + 1] + 1
        : Math.max(lengths[left + 1][right], lengths[left][right + 1]);
    }
  }

  const lines: RuleScriptLine[] = [];
  let left = 0;
  let right = 0;
  while (left < before.length || right < after.length) {
    if (left < before.length && right < after.length && before[left] === after[right]) {
      lines.push({ change: 'same', line: right + 1, otherLine: left + 1, text: after[right] });
      left++;
      right++;
    } else if (left < before.length && (right >= after.length || lengths[left + 1][right] >= lengths[left][right + 1])) {
      // A changed line reads as the old line removed, then the new one added.
      lines.push({ change: 'removed', line: null, otherLine: left + 1, text: before[left] });
      left++;
    } else {
      lines.push({ change: 'added', line: right + 1, otherLine: null, text: after[right] });
      right++;
    }
  }
  return lines;
}
