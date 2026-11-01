import { OverlayContainer } from '@angular/cdk/overlay';
import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockAdminAgent, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { Subject, firstValueFrom } from 'rxjs';
import { NhAssistantPanelComponent } from '../components/panel/nh-assistant-panel.component';
import { AgentSummary } from '../models/assistant-api.models';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantApiError, NhAssistantApiService } from '../services/nh-assistant-api.service';
import { NhAssistantPanelService } from '../services/nh-assistant-panel.service';
import { NhAssistantStore } from '../services/nh-assistant.store';

/** The application layout with its one drawer panel. */
@Component({
  standalone: true,
  imports: [NhAssistantPanelComponent],
  template: '<nh-assistant-panel />'
})
class LayoutComponent {}

function scenario(): NhAssistantMockScenario {
  return {
    agents: [
      { id: 'projects', version: 1, displayNameKey: 'agents.projects', descriptionKey: 'agents.projects', canMutate: true },
      { id: 'catalog', version: 1, displayNameKey: 'agents.catalog', descriptionKey: 'agents.catalog', canMutate: false }
    ],
    subjectTypes: [{
      id: 'project',
      names: { en: 'Project', nl: 'Project' },
      link: '/projects/{id}',
      subjects: [
        { id: 'p-1', displayName: 'Alpha migration' },
        { id: 'p-2', displayName: 'Hidden project', readable: false }
      ]
    }],
    timing: { firstEventDelayMs: 0, eventDelayMs: 0, chunkSize: 64 },
    turns: [{ match: 'status', steps: [{ text: 'Alpha is on track.' }] }]
  };
}

function flush(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve));
}

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  for (let round = 0; round < 6; round++) {
    fixture.detectChanges();
    await flush();
  }
  fixture.detectChanges();
}

async function render(mock: NhAssistantMockScenario = scenario()): Promise<ComponentFixture<LayoutComponent>> {
  TestBed.configureTestingModule({
    providers: [
      provideTranslateService({ fallbackLang: 'en' }),
      provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
      provideNhAssistantMockApi(mock)
    ]
  });
  await firstValueFrom(TestBed.inject(TranslateService).use('en'));
  const fixture = TestBed.createComponent(LayoutComponent);
  await settle(fixture);
  return fixture;
}

describe('NhAssistantStore chats about a subject (FB25)', () => {
  it('creates the next new chat about the subject the host set, and shows what it is about', async () => {
    const fixture = await render();
    const store = TestBed.inject(NhAssistantStore);
    await store.initialize();

    TestBed.inject(NhAssistantPanelService).open();
    expect(await store.setChatSubject({ subjectType: 'project', subjectId: 'p-1', subjectDisplayName: 'Alpha migration' })).toBeTrue();
    await settle(fixture);
    expect(store.newChatSubject()).toEqual({ subjectType: 'project', subjectId: 'p-1', subjectDisplayName: 'Alpha migration' });
    expect(store.chatAgents().map(agent => agent.id)).toEqual(['projects', 'catalog']);
    expect(store.activeConversation()).toBeNull();
    expect(subjectLine()?.textContent).toContain('Alpha migration');

    expect(await store.send('What is the status?')).toBeTrue();
    await settle(fixture);

    const conversation = store.activeConversation();
    expect(conversation?.subject).toEqual({
      subjectType: 'project',
      subjectId: 'p-1',
      subjectDisplayName: 'Alpha migration',
      subjectTypeName: 'Project',
      subjectLink: '/projects/p-1'
    });
    expect(store.conversations().find(item => item.id === conversation?.id)?.subject?.subjectId).toBe('p-1');
    expect(store.chatSubject()?.subjectId).toBe('p-1');
    expect(subjectLine()?.textContent).toContain('Project: Alpha migration');

    // The subject was for that chat: the next new chat is about nothing in particular.
    expect(store.newChatSubject()).toBeNull();
    store.startNewConversation();
    await settle(fixture);
    expect(store.chatSubject()).toBeNull();
    expect(subjectLine()).toBeNull();

    // A host that leaves its page clears a subject nobody used.
    await store.setChatSubject({ subjectType: 'project', subjectId: 'p-1' });
    expect(await store.setChatSubject(null)).toBeTrue();
    expect(store.newChatSubject()).toBeNull();
    expect(store.chatAgents().map(agent => agent.id)).toEqual(['projects', 'catalog']);
  });

  it('keeps the previous subject and shows the refusal when the person may not use a chat about it', async () => {
    const fixture = await render();
    const store = TestBed.inject(NhAssistantStore);
    await store.initialize();
    await store.setChatSubject({ subjectType: 'project', subjectId: 'p-1' });

    expect(await store.setChatSubject({ subjectType: 'project', subjectId: 'p-2' })).toBeFalse();
    await settle(fixture);

    expect(store.newChatSubject()?.subjectId).toBe('p-1');
    expect(store.error()?.code).toBe('assistant-subject-not-found');
  });

  it('asks the agents of a chat about a subject with the subject in the query', async () => {
    await render();
    const api = TestBed.inject(NhAssistantApiService);

    const agents = await firstValueFrom(api.agents({ subjectType: 'project', subjectId: 'p-1' }));
    expect(agents.map(agent => agent.id)).toEqual(['projects', 'catalog']);

    let refused: unknown = null;
    try {
      await firstValueFrom(api.agents({ subjectType: 'nh-system', subjectId: 'global' }));
    } catch (error) {
      refused = error;
    }
    expect(refused instanceof NhAssistantApiError && refused.code).toBe('assistant-subject-invalid');
  });
});

/**
 * FB74: an agent offered only for chats about a subject (`RequireChatSubject()`) is not in the status, and
 * `sendMessage` reads the status before it sends. The status must not take that agent away from the chat.
 */
describe('NhAssistantStore with an agent offered only for chats about a subject (FB74)', () => {
  const subject = { subjectType: 'project', subjectId: 'p-1', subjectDisplayName: 'Alpha migration' };
  const projects: AgentSummary = { id: 'projects', version: 1, displayNameKey: 'agents.projects', descriptionKey: 'agents.projects', canMutate: true };
  const projectChat: AgentSummary = { id: 'project-chat', version: 1, displayNameKey: 'agents.project-chat', descriptionKey: 'agents.project-chat', canMutate: false };

  function codeAgent(agent: AgentSummary, worksOn: Partial<NhAssistantMockAdminAgent>): NhAssistantMockAdminAgent {
    return {
      source: 'code', id: agent.id, displayName: agent.id, description: '', instructions: 'Help.', toolSelectors: [], mcpServerIds: [],
      requiredPolicy: null, autonomy: 'explain', isEnabled: true, worksOnAllSubjects: false, worksOnSubjectTypes: ['project'], ...worksOn
    };
  }

  /** The project chat is left out of the status; the `general` agents are in it and work on projects. */
  function subjectOnly(general: AgentSummary[] = [projects]): NhAssistantMockScenario {
    return {
      ...scenario(),
      agents: [...general, projectChat],
      subjectTypes: [
        ...scenario().subjectTypes!,
        { id: 'task', names: { en: 'Task' }, subjects: [{ id: 't-1', displayName: 'Write the plan' }] }
      ],
      admin: {
        agents: [
          ...general.map(agent => codeAgent(agent, {})),
          codeAgent(projectChat, { requireChatSubject: true })
        ]
      }
    };
  }

  it('sends the first message of a chat about a subject to the agent the status leaves out', async () => {
    const fixture = await render(subjectOnly());
    const store = TestBed.inject(NhAssistantStore);
    await store.initialize();
    expect(store.chatAgents().map(agent => agent.id)).toEqual(['projects']);

    expect(await store.setChatSubject(subject)).toBeTrue();
    TestBed.inject(NhAssistantPanelService).open({ agentId: projectChat.id });
    await settle(fixture);
    expect(store.chatAgents().map(agent => agent.id)).toEqual(['projects', 'project-chat']);
    expect(store.selectedAgentId()).toBe('project-chat');

    expect(await store.send('What is the status?')).toBeTrue();
    await settle(fixture);

    expect(store.error()).toBeNull();
    expect(store.activeConversation()?.agentId).toBe('project-chat');
    expect(store.activeConversation()?.subject?.subjectId).toBe('p-1');
  });

  it('keeps the agent of the open chat while the status lists no agent at all', async () => {
    const fixture = await render(subjectOnly([]));
    const store = TestBed.inject(NhAssistantStore);
    await store.initialize();
    expect(store.selectedAgentId()).toBeNull();

    expect(await store.setChatSubject(subject)).toBeTrue();
    expect(store.selectedAgentId()).toBe('project-chat');
    expect(await store.send('What is the status?')).toBeTrue();
    await settle(fixture);

    // The subject was for that chat, so the status alone lists the agents of the next one.
    expect(store.newChatSubject()).toBeNull();
    expect(store.chatAgents()).toEqual([]);
    expect(store.selectedAgentId()).toBe('project-chat');
    expect(await store.send('And the status now?')).toBeTrue();
    await settle(fixture);

    expect(store.error()).toBeNull();
    expect(store.activeConversation()?.messages.filter(message => message.role === 'user').length).toBe(2);
    expect(store.selectedAgentId()).toBe('project-chat');
  });

  it('keeps the agent of the open chat when the host clears the subject of the next one', async () => {
    const fixture = await render(subjectOnly());
    const store = TestBed.inject(NhAssistantStore);
    await store.initialize();
    await store.setChatSubject(subject);
    TestBed.inject(NhAssistantPanelService).open({ agentId: projectChat.id });
    await settle(fixture);
    await store.send('What is the status?');
    await settle(fixture);

    expect(await store.setChatSubject(null)).toBeTrue();

    expect(store.activeConversation()?.agentId).toBe('project-chat');
    expect(store.selectedAgentId()).toBe('project-chat');

    // A new chat is about nothing in particular again, so the status decides its agent.
    store.startNewConversation();
    await store.reloadStatus();
    expect(store.selectedAgentId()).toBe('projects');
  });

  it('leaves the choice alone while the agents of the subject load', async () => {
    const fixture = await render(subjectOnly());
    const store = TestBed.inject(NhAssistantStore);
    const api = TestBed.inject(NhAssistantApiService);
    await store.initialize();
    await store.setChatSubject(subject);
    TestBed.inject(NhAssistantPanelService).open({ agentId: projectChat.id });
    await settle(fixture);
    await store.send('What is the status?');
    await settle(fixture);
    store.startNewConversation();
    expect(store.selectedAgentId()).toBe('project-chat');

    const listed = new Subject<AgentSummary[]>();
    const agents = spyOn(api, 'agents').and.returnValue(listed);
    const started = store.setChatSubject({ subjectType: 'project', subjectId: 'p-1' });
    await settle(fixture);
    expect(agents).toHaveBeenCalled();

    // A status that arrives now knows nothing of the subject's agents, so it does not choose.
    await store.reloadStatus();
    expect(store.selectedAgentId()).toBe('project-chat');

    listed.next([projects, projectChat]);
    listed.complete();
    expect(await started).toBeTrue();
    expect(store.selectedAgentId()).toBe('project-chat');
  });

  it('chooses from the status again once a subject was refused', async () => {
    const fixture = await render(subjectOnly());
    const store = TestBed.inject(NhAssistantStore);
    await store.initialize();
    await settle(fixture);

    expect(await store.setChatSubject({ subjectType: 'project', subjectId: 'p-2' })).toBeFalse();
    await store.reloadStatus();

    expect(store.newChatSubject()).toBeNull();
    expect(store.selectedAgentId()).toBe('projects');
  });

  it('lets the server refuse an agent for a subject without agents instead of dropping the message', async () => {
    const fixture = await render(subjectOnly());
    const store = TestBed.inject(NhAssistantStore);
    await store.initialize();

    expect(await store.setChatSubject({ subjectType: 'task', subjectId: 't-1' })).toBeTrue();
    expect(store.chatAgents()).toEqual([]);
    TestBed.inject(NhAssistantPanelService).open({ agentId: projects.id });
    await settle(fixture);
    expect(store.selectedAgentId()).toBe('projects');

    expect(await store.send('What is the status?')).toBeFalse();
    await settle(fixture);

    expect(store.selectedAgentId()).toBe('projects');
    expect(store.error()?.code).toBe('assistant-agent-does-not-work-on');
  });

  it('has the mock refuse what the server refuses for such an agent', async () => {
    await render(subjectOnly());
    const api = TestBed.inject(NhAssistantApiService);
    const refused = async (work: () => Promise<unknown>): Promise<string | null> => {
      try {
        await work();
        return null;
      } catch (error) {
        return error instanceof NhAssistantApiError ? error.code : 'unexpected';
      }
    };

    expect((await firstValueFrom(api.status())).agents.map(agent => agent.id)).toEqual(['projects']);
    expect(await firstValueFrom(api.agents({ subjectType: 'task', subjectId: 't-1' }))).toEqual([]);
    expect(await refused(() => firstValueFrom(api.createConversation({ agentId: projectChat.id })))).toBe('assistant-subject-required');
    expect(await refused(() => firstValueFrom(api.createConversation({ agentId: projectChat.id, subjectType: 'task', subjectId: 't-1' }))))
      .toBe('assistant-agent-does-not-work-on');
    expect(await refused(() => firstValueFrom(api.createConversation({ agentId: projectChat.id, subjectType: 'project', subjectId: 'p-1' })))).toBeNull();
  });
});

/** The panel's line that says what the open or next chat is about, in the drawer's overlay. */
function subjectLine(): HTMLElement | null {
  return TestBed.inject(OverlayContainer).getContainerElement().querySelector<HTMLElement>('.chat-subject');
}
