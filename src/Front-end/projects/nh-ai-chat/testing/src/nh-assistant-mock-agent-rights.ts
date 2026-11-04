import { AdminAgent, AgentRunSettings, DataConsultation, DataSkill } from '@newheap/platform-ai-chat';
import { NhAssistantMockResult, mockFailure as failure } from './nh-assistant-mock-admin-shared';

const autonomyLevels: readonly string[] = ['observe', 'explain', 'propose', 'simulate', 'execute'];

/**
 * The mock's `UseAgentRightsPolicy`: whether the caller may change whose rights an agent uses, who reaches it,
 * who decides its proposals and what runs without approval. The run catalog reports it as
 * `mayChangeAgentRights`; every save path asks it with what it would change, as on the server.
 */
export class NhAssistantMockAgentRights {
  private checks = true;

  constructor(private allowed: boolean) {}

  get mayChange(): boolean {
    return this.allowed;
  }

  set(mayChange: boolean): void {
    this.allowed = mayChange;
  }

  /** The refusal of `changes`, with the field error `rights-required` per change; `null` when they may go ahead. */
  refusal(changes: readonly string[]): NhAssistantMockResult | null {
    if (this.allowed || !this.checks || changes.length === 0) {
      return null;
    }

    return failure('assistant-agent-rights-forbidden', Object.fromEntries(changes.map(change => [change, ['rights-required']])));
  }

  /** Runs saves that do not answer for themselves: an import of an agent package asks once, for its whole plan. */
  withoutChecks<T>(work: () => T): T {
    const before = this.checks;
    this.checks = false;
    try {
      return work();
    } finally {
      this.checks = before;
    }
  }
}

/**
 * The changes of an agent's definition. A new agent may name a required policy, which only narrows who reaches
 * it; on an agent that exists every policy counts, and so does an added MCP server. When its runs use another
 * account's rights, so do more tools and more autonomy.
 */
export function mockDefinitionRights(
  before: AdminAgent | null,
  after: AdminAgent,
  mcpServersAfter: readonly string[] | null,
  lendsRights: boolean
): string[] {
  const changes: string[] = [];
  if (before && differ(before.requiredPolicy, after.requiredPolicy)) {
    changes.push('requiredPolicy');
  }
  if (differ(before?.runPolicy, after.runPolicy)) {
    changes.push('runPolicy');
  }
  if (differ(before?.viewPolicy, after.viewPolicy)) {
    changes.push('viewPolicy');
  }
  if (mcpServersAfter?.some(serverId => !before?.mcpServerIds.includes(serverId))) {
    changes.push('mcpServerIds');
  }
  if (before && lendsRights) {
    if (after.toolSelectors.some(selector => !before.toolSelectors.some(existing => covers(existing, selector)))) {
      changes.push('toolSelectors');
    }
    if (autonomyLevels.indexOf(after.autonomy) > autonomyLevels.indexOf(before.autonomy)) {
      changes.push('autonomy');
    }
  }
  return changes;
}

/** The changes of run settings; new ones compare with an agent that answers to itself and has no rights to use. */
export function mockRunSettingsRights(before: AgentRunSettings | null, after: AgentRunSettings): string[] {
  const changes: string[] = [];
  if ((before?.accountability ?? 'agent') !== after.accountability) {
    changes.push('accountability');
  }
  if (differ(before?.serviceAccountActorId, after.serviceAccountActorId)) {
    changes.push('serviceAccountActorId');
  }
  if ((before?.delegateFromInitiator ?? false) !== after.delegateFromInitiator) {
    changes.push('delegateFromInitiator');
  }
  if ((before?.allowGatewayReads ?? false) !== after.allowGatewayReads) {
    changes.push('allowGatewayReads');
  }
  if ((before?.elevatedReads ?? 'restricted') !== after.elevatedReads) {
    changes.push('elevatedReads');
  }
  if (differ(before?.approvalPolicy, after.approvalPolicy)) {
    changes.push('approvalPolicy');
  }
  return changes;
}

/** The changes of a skill; a new skill compares with one that uses its agent's approval policy. */
export function mockSkillRights(before: Pick<DataSkill, 'approvalPolicy'> | null, after: Pick<DataSkill, 'approvalPolicy'>): string[] {
  return differ(before?.approvalPolicy, after.approvalPolicy) ? ['approvalPolicy'] : [];
}

/** What of a consultation takes the policy. */
type ConsultationRights = Pick<DataConsultation, 'routes' | 'askerPolicy' | 'minimumRole' | 'proposalRole' | 'proposalPolicy' | 'exposeToMcp'>;

/** The changes of a consultation; a new one compares with the defaults it gets without these settings. */
export function mockConsultationRights(before: ConsultationRights | null, after: ConsultationRights): string[] {
  const changes: string[] = [];
  if (JSON.stringify(before?.routes ?? []) !== JSON.stringify(after.routes)) {
    changes.push('routes');
  }
  if (differ(before?.askerPolicy, after.askerPolicy)) {
    changes.push('askerPolicy');
  }
  if ((before?.minimumRole ?? 'reader') !== after.minimumRole) {
    changes.push('minimumRole');
  }
  if ((before?.proposalRole ?? 'participant') !== after.proposalRole) {
    changes.push('proposalRole');
  }
  if (differ(before?.proposalPolicy, after.proposalPolicy)) {
    changes.push('proposalPolicy');
  }
  if ((before?.exposeToMcp ?? false) !== after.exposeToMcp) {
    changes.push('exposeToMcp');
  }
  return changes;
}

function differ(before: string | null | undefined, after: string | null | undefined): boolean {
  return (before ?? null) !== (after ?? null);
}

/** Whether a selector the agent had covers another: a narrower one under it widens nothing. */
function covers(existing: string, selector: string): boolean {
  return existing === selector || (existing.endsWith('*') && selector.startsWith(existing.slice(0, -1)));
}
