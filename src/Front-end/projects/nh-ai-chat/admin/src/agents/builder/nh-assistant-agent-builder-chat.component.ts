import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import {
  AdminAgent,
  NhAssistantPanelComponent,
  NhAssistantStore,
  ɵNhAssistantIconComponent as NhAssistantIconComponent
} from '@newheap/platform-ai-chat';
import { agentDisplayName } from '../agent-texts';
import { startNhAssistantAdminTranslations } from '../../i18n/nh-assistant-admin-translations';
import {
  NH_ASSISTANT_AGENT_BUILDER_ID,
  NhAssistantAgentBuilderChats,
  nhAssistantAgentBuilderContext,
  nhAssistantCreatedAgents
} from './agent-builder';

let nextId = 0;

/**
 * The chat with the agent builder (RB-9) in the administration: on an agent's page it works on that agent,
 * and for "New agent with help" on a new one. It is the assistant's own chat panel in page mode, so the
 * builder's proposals wait on the same approval cards; each save and each step live changes nothing until
 * the administrator approves it there. The agent goes along as the page context of every message. The chat
 * continues where it was while the administration is open; once the builder created an agent, it says so
 * (`agentCreated`) and continues on that agent's page. It shows only when the person may chat with the
 * builder, never on the builder's own page.
 */
@Component({
  selector: 'nh-assistant-agent-builder-chat',
  standalone: true,
  imports: [TranslatePipe, NhAssistantIconComponent, NhAssistantPanelComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-agent-builder-chat.component.html',
  styleUrls: ['../../nh-assistant-admin-shared.scss', '../page/agent-page.scss', './nh-assistant-agent-builder-chat.component.scss']
})
export class NhAssistantAgentBuilderChatComponent {
  private readonly store = inject(NhAssistantStore);
  private readonly chats = inject(NhAssistantAgentBuilderChats);
  private readonly translate = inject(TranslateService);
  /** Per conversation, the create calls that had succeeded when the chat first showed it. */
  private readonly known = new Map<string, Set<string>>();

  /** The agent the builder works on; `null` for a new agent. */
  readonly agent = input<AdminAgent | null>(null);
  /** Opens the chat at once, as "New agent with help" does. */
  readonly startOpen = input(false);
  /** The builder created an agent in this chat, by its id. */
  readonly agentCreated = output<string>();

  readonly id = `nh-assistant-agent-builder-${nextId++}`;
  readonly open = signal(false);
  /** The administrator closed the chat: it no longer opens by itself. */
  private readonly closed = signal(false);
  /** The person may chat with the builder, and this is not the builder's own page. */
  readonly available = computed(() =>
    this.agent()?.id !== NH_ASSISTANT_AGENT_BUILDER_ID
    && this.store.agents().some(agent => agent.id === NH_ASSISTANT_AGENT_BUILDER_ID));

  constructor() {
    startNhAssistantAdminTranslations();
    // The chat may not have started yet on an administration page; its agents are the person's.
    void this.store.initialize();
    inject(DestroyRef).onDestroy(() => {
      if (this.open()) {
        this.store.setPageContextOverride(null);
      }
    });

    // It opens by itself for a new agent, and where an earlier chat about the agent continues.
    effect(() => {
      const agent = this.agent();
      if (this.available() && !this.open() && !this.closed() && (this.startOpen() || this.chats.conversationOf(agent?.id ?? null) !== null)) {
        untracked(() => this.show());
      }
    });

    // Another agent's page sends that agent along.
    effect(() => {
      const agent = this.agent();
      if (this.open()) {
        untracked(() => this.store.setPageContextOverride(this.contextOf(agent)));
      }
    });

    // The chat is remembered for its agent, and an agent the builder created continues on its page.
    effect(() => {
      const conversation = this.store.activeConversation();
      if (!this.open() || conversation === null || conversation.agentId !== NH_ASSISTANT_AGENT_BUILDER_ID) {
        return;
      }
      untracked(() => {
        const agentId = this.agent()?.id ?? null;
        this.chats.remember(agentId, conversation.id);
        const created = nhAssistantCreatedAgents(conversation);
        const known = this.known.get(conversation.id);
        if (known === undefined) {
          // What the chat created before it showed here was followed then; only new agents are.
          this.known.set(conversation.id, new Set(created.keys()));
          return;
        }
        for (const [invocationId, createdId] of created) {
          if (!known.has(invocationId)) {
            known.add(invocationId);
            this.chats.remember(createdId, conversation.id);
            if (agentId === null) {
              this.chats.forgetNew();
            }
            this.agentCreated.emit(createdId);
          }
        }
      });
    });
  }

  /** Opens the chat: the one about the agent when there is one, otherwise a new chat with the builder. */
  show(): void {
    const agent = this.agent();
    this.open.set(true);
    this.store.setPageContextOverride(this.contextOf(agent));
    const conversationId = this.chats.conversationOf(agent?.id ?? null);
    if (conversationId !== null) {
      void this.store.openConversation(conversationId);
      return;
    }

    this.store.selectAgent(NH_ASSISTANT_AGENT_BUILDER_ID);
    this.store.startNewConversation();
  }

  /** Closes the chat; it continues when it opens again. */
  hide(): void {
    this.closed.set(true);
    this.open.set(false);
    this.store.setPageContextOverride(null);
  }

  private contextOf(agent: AdminAgent | null) {
    return nhAssistantAgentBuilderContext(
      agent,
      agent === null ? '' : agentDisplayName(this.translate, agent),
      this.translate.instant('nh-assistant.admin.agent-builder.new-context'));
  }
}
