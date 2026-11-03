import { Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { NhAssistantAdminAgentsComponent } from '../../../admin/src/public-api';
import { ClientContext } from '../models/assistant-api.models';
import { NhAssistantStore } from '../services/nh-assistant.store';
import { adminAgentPage, openAdminAgent, settleAdmin } from './nh-assistant-admin-navigation';
import { provideNhAssistant } from '../provide-nh-assistant';

/**
 * The agent builder in the administration (RB-9): on an agent's Build tab its chat works on that agent,
 * which every message names in its page context; "New agent with help" opens it for a new agent and,
 * once the builder created the agent after the administrator's approval, continues on that agent's page
 * with the same chat. Only a person who may chat with the builder sees it, and never on its own page.
 */

/** How long a chat flow may take before the spec fails; it only matters when it does. */
const chatFlowTimeout = 5000;

const builder = {
  source: 'code' as const,
  id: 'nh-agent-builder',
  displayName: 'nh-assistant.agent-builder.name',
  description: 'nh-assistant.agent-builder.description',
  instructions: 'Build agents.',
  toolSelectors: ['assistant.agent-builder.*'],
  mcpServerIds: [],
  requiredPolicy: 'nh-assistant-agent-builder',
  autonomy: 'execute' as const,
  isEnabled: true,
  chatOnly: true
};

function scenario(withBuilder: boolean, contexts: (ClientContext | null)[] = []): NhAssistantMockScenario {
  return {
    agents: withBuilder
      ? [{ id: 'nh-agent-builder', version: 1, displayNameKey: 'nh-assistant.agent-builder.name', descriptionKey: 'nh-assistant.agent-builder.description', canMutate: true }]
      : [],
    timing: { firstEventDelayMs: 0, eventDelayMs: 0 },
    turns: [
      {
        match: (text, _agentId, pageContext) => {
          contexts.push(pageContext);
          return text.includes('deadlines');
        },
        steps: [
          {
            approval: {
              toolId: 'assistant.agent-builder.create-agent',
              displayName: 'Create an agent',
              summary: 'Approval is required before this tool can run.',
              presentation: {
                toolDisplayName: 'Create an agent',
                summary: "Creates the new agent 'Deadline watch' in the administration, without run settings, skills or triggers.",
                fields: [{ label: 'Agent', value: 'deadline-watch' }],
                notice: 'The agent builder works with your own rights. Nothing changes until you approve this.'
              },
              argumentsPreview: '{"input":{"agentId":"deadline-watch"}}',
              targets: [],
              resultPreview: '{"agentId":"deadline-watch","created":true}',
              approved: [{ text: 'The agent exists now.' }],
              rejected: [{ text: 'Nothing was created.' }]
            }
          }
        ]
      },
      { steps: [{ text: 'I can change this agent with you.' }] }
    ],
    admin: {
      agents: [
        ...(withBuilder ? [builder] : []),
        {
          source: 'admin', id: 'deadline-watch', displayName: 'Deadline watch', description: 'Warns about deadlines.', instructions: 'Watch.',
          toolSelectors: ['projects.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'propose', isEnabled: true
        },
        {
          source: 'code', id: 'planner', displayName: 'Project planner', description: 'Plans projects.', instructions: 'Plan.',
          toolSelectors: ['projects.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'execute', isEnabled: true, chatOnly: true
        }
      ],
      runCatalog: { triggerEvents: [], subjectSets: [], ruleHandlers: [], approvalPolicies: [], profiles: [] }
    }
  };
}

async function configure(withBuilder: boolean, contexts: (ClientContext | null)[] = [], language = 'en'): Promise<void> {
  TestBed.configureTestingModule({
    providers: [
      provideTranslateService({ fallbackLang: 'en' }),
      provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
      provideNhAssistantMockApi(scenario(withBuilder, contexts))
    ]
  });
  await firstValueFrom(TestBed.inject(TranslateService).use(language));
}

async function render<T>(component: Type<T>): Promise<{ fixture: ComponentFixture<T>; element: HTMLElement }> {
  const fixture = TestBed.createComponent(component);
  await settleAdmin(fixture);
  return { fixture, element: fixture.nativeElement as HTMLElement };
}

/** Lets the chat answer until `done` holds, within `chatFlowTimeout`. */
async function until(fixture: ComponentFixture<unknown>, done: () => boolean): Promise<void> {
  const deadline = Date.now() + chatFlowTimeout;
  while (!done()) {
    if (Date.now() > deadline) {
      throw new Error('The chat did not get there in time.');
    }
    await settleAdmin(fixture);
  }
}

function text(element: Element | null | undefined): string {
  return element?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

describe('The agent builder in the administration', () => {
  it('chats on an agent\'s Build tab about that agent, and not on its own page', async () => {
    const contexts: (ClientContext | null)[] = [];
    await configure(true, contexts);
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const store = TestBed.inject(NhAssistantStore);

    const page = await openAdminAgent(fixture, element, 'planner', 'build');
    const section = page.querySelector<HTMLElement>('[data-agent-builder]');
    expect(text(section?.querySelector('h4'))).toBe('Build with help');
    expect(section?.querySelector('[data-agent-builder-chat]')).toBeNull();

    (section!.querySelector('[data-agent-builder-open]') as HTMLButtonElement).click();
    await settleAdmin(fixture);
    expect(section!.querySelector('[data-agent-builder-chat] nh-assistant-panel')).not.toBeNull();
    expect(store.selectedAgentId()).toBe('nh-agent-builder');
    // The chip shows the agent the message names.
    expect(store.pageContext()?.entities?.[0]).toEqual({ type: 'assistant-agent', id: 'planner', label: 'Project planner' });

    await store.submit('What can you change here?');
    await until(fixture, () => store.activeConversation()?.status === 'idle' && (store.activeConversation()?.messages.length ?? 0) >= 2);
    expect(store.activeConversation()?.agentId).toBe('nh-agent-builder');
    expect(contexts.at(-1)).toEqual({
      route: 'assistant/admin/agents/planner',
      title: 'Project planner',
      entities: [{ type: 'assistant-agent', id: 'planner', label: 'Project planner' }]
    });

    // Closing gives the page context back to the host; the builder's own page has no builder chat.
    (section!.querySelector('[data-agent-builder-close]') as HTMLButtonElement).click();
    await settleAdmin(fixture);
    expect(store.pageContext()).toBeNull();
    (adminAgentPage(element).querySelector('[data-back]') as HTMLButtonElement).click();
    await settleAdmin(fixture);
    const own = await openAdminAgent(fixture, element, 'nh-agent-builder', 'build');
    expect(own.querySelector('[data-agent-builder]')).toBeNull();
  });

  it('is a chat with the builder only, and leaves the assistant\'s chat as it was', async () => {
    await configure(true);
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const store = TestBed.inject(NhAssistantStore);
    await store.initialize();
    store.selectAgent('planner');

    let page = await openAdminAgent(fixture, element, 'planner', 'build');
    let section = page.querySelector<HTMLElement>('[data-agent-builder]')!;
    (section.querySelector('[data-agent-builder-open]') as HTMLButtonElement).click();
    await settleAdmin(fixture);
    const panel = section.querySelector('[data-agent-builder-chat] nh-assistant-panel')!;
    expect(text(panel.querySelector('header h2'))).toBe('Agent builder');
    // The person may chat with the other agents too, but this chat offers the builder only.
    expect(store.pickerAgents().length).toBe(3);
    expect(panel.querySelector('nh-assistant-agent-picker')).toBeNull();
    expect(store.selectedAgentId()).toBe('nh-agent-builder');

    // Closing the builder's chat gives the assistant's chat its agent back.
    (section.querySelector('[data-agent-builder-close]') as HTMLButtonElement).click();
    await settleAdmin(fixture);
    expect(store.selectedAgentId()).toBe('planner');
    expect(store.activeConversation()).toBeNull();

    // So does leaving the page while it is open.
    (section.querySelector('[data-agent-builder-open]') as HTMLButtonElement).click();
    await settleAdmin(fixture);
    expect(store.selectedAgentId()).toBe('nh-agent-builder');
    (adminAgentPage(element).querySelector('[data-back]') as HTMLButtonElement).click();
    await settleAdmin(fixture);
    expect(store.selectedAgentId()).toBe('planner');
    page = await openAdminAgent(fixture, element, 'deadline-watch', 'build');
    section = page.querySelector<HTMLElement>('[data-agent-builder]')!;
    expect(section.querySelector('[data-agent-builder-chat]')).toBeNull();
  });

  it('makes a new agent with help and continues on its page with the same chat', async () => {
    const contexts: (ClientContext | null)[] = [];
    await configure(true, contexts);
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const store = TestBed.inject(NhAssistantStore);

    (element.querySelector('[data-new-agent-with-help]') as HTMLButtonElement).click();
    await settleAdmin(fixture);
    const help = element.querySelector<HTMLElement>('[data-new-agent-with-help-page]');
    expect(text(help?.querySelector('[data-agent-builder] h4'))).toBe('New agent with help');
    expect(help?.querySelector('[data-agent-builder-chat] nh-assistant-panel')).not.toBeNull();

    // The builder asks to create the agent; the administrator approves it on its card.
    await store.submit('Make an agent that warns about deadlines.');
    await until(fixture, () => store.pendingApproval()?.status === 'pending');
    expect(contexts.at(-1)).toEqual({ route: 'assistant/admin/agents/new', title: 'New agent' });
    const conversationId = store.activeConversation()!.id;
    store.decide('approve');

    // Once it exists, its page opens at Build, where the same chat continues.
    await until(fixture, () => element.querySelector('[data-agent-page="deadline-watch"]') !== null);
    const page = adminAgentPage(element);
    expect(page.querySelector('[data-agent-tab="build"]')?.getAttribute('aria-selected')).toBe('true');
    await until(fixture, () => page.querySelector('[data-agent-builder-chat] nh-assistant-panel') !== null);
    expect(store.activeConversation()?.id).toBe(conversationId);
    expect(store.pageContext()?.entities?.[0]?.id).toBe('deadline-watch');
  });

  it('is offered only where the person may chat with the builder', async () => {
    await configure(false);
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);

    expect(element.querySelector('[data-new-agent-with-help]')).toBeNull();
    const page = await openAdminAgent(fixture, element, 'planner', 'build');
    expect(page.querySelector('[data-agent-builder]')).toBeNull();
  });

  it('reads in Dutch', async () => {
    await configure(true, [], 'nl');
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);

    expect(text(element.querySelector('[data-new-agent-with-help]'))).toBe('Nieuwe agent met hulp');
    const page = await openAdminAgent(fixture, element, 'planner', 'build');
    expect(text(page.querySelector('[data-agent-builder] h4'))).toBe('Bouwen met hulp');
  });
});
