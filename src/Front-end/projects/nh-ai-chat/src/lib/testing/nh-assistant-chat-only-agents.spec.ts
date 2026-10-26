import { Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { NhAssistantAdminAgentsComponent } from '../../../admin/src/public-api';
import { AdminAgent } from '../models/assistant-admin.models';
import { provideNhAssistant } from '../provide-nh-assistant';

/**
 * Chat-only agents (FB8): an agent in code with `ChatOnly()` works on no subject. The administration
 * says so on its card instead of what it works on and offers no "Run now", runs, run state changes or
 * shadow mode, also not for its skills, which always work live; the mock API refuses its starts and
 * its shadow modes as the server does.
 */

function scenario(): NhAssistantMockScenario {
  return {
    agents: [],
    turns: [],
    subjectTypes: [{ id: 'project', names: { en: 'Project', nl: 'Project' }, subjects: [] }],
    admin: {
      agents: [
        {
          source: 'code', id: 'chat-helper', displayName: 'Chat helper', description: 'Helps in the chat.', instructions: 'Help.',
          toolSelectors: ['projects.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'propose', isEnabled: true, chatOnly: true,
          skills: [{ skillId: 'notes', description: 'Writes notes.' }]
        },
        {
          source: 'code', id: 'planner', displayName: 'Project planner', description: 'Plans projects.', instructions: 'Plan.',
          toolSelectors: ['projects.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'execute', isEnabled: true,
          worksOnSubjectTypes: ['project'], worksOnAllSubjects: false
        }
      ],
      runCatalog: { triggerEvents: [], subjectSets: [], ruleHandlers: [], approvalPolicies: [], profiles: [] }
    }
  };
}

function flush(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve));
}

async function configure(language = 'en'): Promise<void> {
  TestBed.configureTestingModule({
    providers: [
      provideTranslateService({ fallbackLang: 'en' }),
      provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
      provideNhAssistantMockApi(scenario())
    ]
  });
  await firstValueFrom(TestBed.inject(TranslateService).use(language));
}

async function render<T>(component: Type<T>): Promise<{ fixture: ComponentFixture<T>; element: HTMLElement }> {
  const fixture = TestBed.createComponent(component);
  for (let round = 0; round < 8; round++) {
    fixture.detectChanges();
    await flush();
  }
  fixture.detectChanges();
  return { fixture, element: fixture.nativeElement as HTMLElement };
}

function text(element: Element | null | undefined): string {
  return element?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

function cardOf(element: HTMLElement, agentId: string): HTMLElement {
  return Array.from(element.querySelectorAll<HTMLElement>('li.card'))
    .find(card => Array.from(card.querySelectorAll('.mono')).some(id => text(id) === agentId))!;
}

async function call(method: string, path: string, body?: unknown): Promise<{ status: number; body: any }> {
  const response = await TestBed.inject(NhAssistantMockBackend).fetch(`/api/assistant/${path}`, {
    method,
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  const content = await response.text();
  return { status: response.status, body: content ? JSON.parse(content) : null };
}

describe('Chat-only agents in the administration', () => {
  it('says the agent works in the chat only and offers nothing that runs it', async () => {
    await configure();
    const { element } = await render(NhAssistantAdminAgentsComponent);

    const helper = cardOf(element, 'chat-helper');
    expect(text(helper.querySelector('[data-chat-only]'))).toBe('Chat only');
    expect(text(helper.querySelector('[data-chat-only-hint]'))).toBe('Works in the chat only: it is on no object\'s page, has no triggers and does not run on its own.');
    expect(helper.querySelector('[data-works-on]')).toBeNull();
    expect(helper.querySelector('[data-run-now-agent]')).toBeNull();
    expect(helper.querySelector('[data-runs-agent]')).toBeNull();
    expect(Array.from(helper.querySelectorAll('[data-state-action]')).filter(action => !action.closest('li.skill'))).toEqual([]);
    expect(helper.querySelector('[data-shadow-action]')).toBeNull();
    // Its skills always work live: no shadow badge and no shadow action.
    const notes = helper.querySelector('li.skill[data-skill="notes"]');
    expect(notes).not.toBeNull();
    expect(notes!.querySelector('[data-shadow-badge]')).toBeNull();
    expect(notes!.querySelector('[data-shadow-action]')).toBeNull();
    // Its skills as data stay in its run configuration.
    expect(helper.querySelector('[data-run-config-agent="chat-helper"]')).not.toBeNull();

    // An agent that runs on objects keeps them.
    const planner = cardOf(element, 'planner');
    expect(planner.querySelector('[data-chat-only]')).toBeNull();
    expect(planner.querySelector('[data-run-now-agent="planner"]')).not.toBeNull();
    expect(planner.querySelector('[data-state-action]')).not.toBeNull();
  });

  it('reads in Dutch', async () => {
    await configure('nl');
    const { element } = await render(NhAssistantAdminAgentsComponent);

    expect(text(cardOf(element, 'chat-helper').querySelector('[data-chat-only]'))).toBe('Alleen chat');
  });

  it('refuses a start and the shadow mode in the mock API, as the server does', async () => {
    await configure();
    const listed = ((await call('GET', 'admin/agents')).body as AdminAgent[]).find(agent => agent.id === 'chat-helper')!;
    expect([listed.chatOnly, listed.worksOnAllSubjects, listed.worksOnApplication]).toEqual([true, false, false]);

    const shadow = await call('POST', 'admin/agents/chat-helper/shadow', { mode: 'shadow' });
    expect([shadow.status, shadow.body.code]).toEqual([409, 'assistant-agent-does-not-work-on']);

    // Its skills always work live, and the mode cannot change.
    const notes = listed.skills!.find(skill => skill.skillId === 'notes')!;
    expect([notes.shadow?.shadow, notes.shadow?.locked, listed.shadow?.locked]).toEqual([false, true, true]);
    const skillShadow = await call('POST', 'admin/agents/chat-helper/skills/notes/shadow', { mode: 'shadow' });
    expect([skillShadow.status, skillShadow.body.code]).toEqual([409, 'assistant-agent-does-not-work-on']);
  });
});
