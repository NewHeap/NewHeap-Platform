import { Signal, WritableSignal, inject, signal } from '@angular/core';
import { NhAssistantAdminApiService, RunCatalog } from '@newheap/platform-ai-chat';

/** The run settings that decide whose rights an agent's runs use and who decides their proposals. */
export const agentRightsRunSettingsFields: readonly string[] = [
  'accountability',
  'serviceAccountActorId',
  'delegateFromInitiator',
  'allowGatewayReads',
  'elevatedReads',
  'approvalPolicy'
];

/** The setting of a skill that decides who decides its proposals. */
export const agentRightsSkillFields: readonly string[] = ['approvalPolicy'];

/** What decides who may consult an agent, who reaches the consultation and who decides its proposals. */
export const agentRightsConsultationFields: readonly string[] = [
  'routes',
  'askerPolicy',
  'minimumRole',
  'proposalRole',
  'proposalPolicy',
  'exposeToMcp'
];

/**
 * Whether the person may change an agent's rights (`UseAgentRightsPolicy`). A server that does not say, or a
 * run catalog that could not be read, means yes: the server still decides, and its refusal shows at the field.
 */
export function mayChangeAgentRights(catalog: RunCatalog | null | undefined): boolean {
  return catalog?.mayChangeAgentRights !== false;
}

/** What is known of the person's rights through one administration service. */
interface AgentRightsRead {
  mayChange: WritableSignal<boolean>;
  reading: boolean;
}

/** Per administration service, so the rows of a page that each ask share one request. */
const agentRightsReads = new WeakMap<NhAssistantAdminApiService, AgentRightsRead>();

/**
 * Whether the person may change an agent's rights, for a component the host can show on its own and that has no
 * run catalog to go by, such as the MCP servers: reads the catalog when the component is created. Yes until the
 * answer is there; see `mayChangeAgentRights`.
 */
export function readMayChangeAgentRights(): Signal<boolean> {
  const admin = inject(NhAssistantAdminApiService);
  const read = agentRightsReads.get(admin) ?? { mayChange: signal(true), reading: false };
  agentRightsReads.set(admin, read);

  if (!read.reading) {
    read.reading = true;
    admin.getRunCatalog().subscribe({
      next: catalog => read.mayChange.set(mayChangeAgentRights(catalog)),
      error: () => {
        read.reading = false;
      },
      complete: () => {
        read.reading = false;
      }
    });
  }

  return read.mayChange.asReadonly();
}
