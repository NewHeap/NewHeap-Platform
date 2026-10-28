import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { map, startWith } from 'rxjs';
import {
  AdminAgent,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe
} from '@newheap/platform-ai-chat';
import { agentDisplayName, codeTextKeys } from '../agent-texts';
import { AgentStatus, agentAttention, agentStatus, agentWork, skillsInShadow, statusTone } from '../agent-operations';
import { startNhAssistantAdminTranslations } from '../../i18n/nh-assistant-admin-translations';

/** What the list shows: every agent, or those that need attention, are live, in shadow, held, or chat only. */
export type AgentListFilter = 'all' | 'attention' | 'live' | 'shadow' | 'held' | 'chat-only';

const filters: readonly AgentListFilter[] = ['all', 'attention', 'live', 'shadow', 'held', 'chat-only'];

/** The statuses that hold an agent's work. */
const held: ReadonlySet<AgentStatus> = new Set(['paused', 'off', 'stopped', 'configured-off']);

/** One row of the list: an agent, and below it its shadow variants. */
interface AgentRow {
  agent: AdminAgent;
  variant: boolean;
}

let nextId = 0;

/**
 * The agents of the administration as one scannable list: per agent its name, description and id,
 * one status, where it works, where it comes from and what needs attention. A search on name and id
 * and filters with their counts narrow the list; a shadow variant follows the agent it is a variant
 * of. A row has no actions of its own: it opens the agent's page (`opened`).
 */
@Component({
  selector: 'nh-assistant-agent-list',
  standalone: true,
  imports: [TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-agent-list.component.html',
  styleUrls: ['../../nh-assistant-admin-shared.scss', './agent-page.scss']
})
export class NhAssistantAgentListComponent {
  private readonly translate = inject(TranslateService);
  private readonly language = toSignal(
    this.translate.onLangChange.pipe(map(event => event.lang), startWith(this.translate.currentLang)),
    { initialValue: this.translate.currentLang });

  readonly agents = input.required<readonly AdminAgent[]>();
  /** Opens the page of an agent, by its id. */
  readonly opened = output<string>();

  readonly id = `nh-assistant-agent-list-${nextId++}`;
  readonly filters = filters;
  readonly search = signal('');
  readonly filter = signal<AgentListFilter>('all');
  readonly codeTextKeys = codeTextKeys;
  readonly agentStatus = agentStatus;
  readonly statusTone = statusTone;
  readonly agentWork = agentWork;

  /** How many agents each filter shows. */
  readonly counts = computed(() => {
    const counts: Record<AgentListFilter, number> = { all: 0, attention: 0, live: 0, shadow: 0, held: 0, 'chat-only': 0 };
    for (const agent of this.agents()) {
      for (const filter of filters) {
        if (matches(agent, filter)) {
          counts[filter]++;
        }
      }
    }
    return counts;
  });

  /** The rows that pass the search and the filter, each agent followed by its shadow variants. */
  readonly rows = computed<AgentRow[]>(() => {
    const term = this.search().trim().toLowerCase();
    const filter = this.filter();
    const agents = this.agents();
    const ids = new Set(agents.map(agent => agent.id));
    this.language();
    // An agent in code is found by its name in the reader's language.
    const shown = (agent: AdminAgent) => matches(agent, filter) && matchesSearch(agent, agentDisplayName(this.translate, agent), term);
    const rows: AgentRow[] = [];
    for (const agent of agents) {
      // A variant whose agent is listed follows that agent; one without it stands on its own.
      if (agent.shadowOf && ids.has(agent.shadowOf)) {
        continue;
      }
      const variants = agents.filter(item => item.shadowOf === agent.id);
      const shownVariants = variants.filter(shown);
      if (shown(agent) || shownVariants.length > 0) {
        rows.push({ agent, variant: false });
        rows.push(...shownVariants.map(item => ({ agent: item, variant: true })));
      }
    }
    return rows;
  });

  constructor() {
    startNhAssistantAdminTranslations();
  }

  /** What needs attention on the agent, as one short text key with its count; `null` for nothing. */
  attention(agent: AdminAgent): { key: string; count: number } | null {
    const problems = agentAttention(agent);
    if (problems > 0) {
      return { key: problems === 1 ? 'nh-assistant.admin.agents.list.attention-one' : 'nh-assistant.admin.agents.list.attention', count: problems };
    }
    if (agent.runState === 'paused' && agent.waitingInputs > 0) {
      return { key: 'nh-assistant.admin.agents.list.waiting', count: agent.waitingInputs };
    }
    const shadowSkills = skillsInShadow(agent).length;
    if (shadowSkills > 0) {
      return { key: shadowSkills === 1 ? 'nh-assistant.admin.agents.list.skill-in-shadow' : 'nh-assistant.admin.agents.list.skills-in-shadow', count: shadowSkills };
    }
    return null;
  }

  /** The key of where the agent comes from: code, code changed here, created here. */
  sourceKey(agent: AdminAgent): string {
    if (agent.source === 'admin') {
      return 'nh-assistant.admin.agents.source-admin';
    }
    return agent.isOverridden ? 'nh-assistant.admin.agents.list.source-changed' : 'nh-assistant.admin.agents.source-code';
  }

  /** The name of the agent a variant is a variant of. */
  variantOf(agent: AdminAgent): AdminAgent | null {
    return agent.shadowOf ? this.agents().find(item => item.id === agent.shadowOf) ?? null : null;
  }

  clear(): void {
    this.search.set('');
    this.filter.set('all');
  }

  value(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }
}

function matches(agent: AdminAgent, filter: AgentListFilter): boolean {
  const status = agentStatus(agent);
  switch (filter) {
    case 'attention':
      return agentAttention(agent) > 0 || (agent.runState === 'paused' && agent.waitingInputs > 0) || skillsInShadow(agent).length > 0;
    case 'live':
      return status === 'live';
    case 'shadow':
      return status === 'shadow' || skillsInShadow(agent).length > 0;
    case 'held':
      return held.has(status);
    case 'chat-only':
      return agent.chatOnly === true;
    default:
      return true;
  }
}

function matchesSearch(agent: AdminAgent, name: string, term: string): boolean {
  if (term.length === 0) {
    return true;
  }
  return agent.id.toLowerCase().includes(term)
    || name.toLowerCase().includes(term)
    || (agent.description ?? '').toLowerCase().includes(term);
}
