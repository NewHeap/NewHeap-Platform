import { Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { NhAssistantAdminAgentsComponent } from '../../../admin/src/public-api';
import { AdminAgent } from '../models/assistant-admin.models';
import { adminStatusOf, backToAdminList, openAdminAgent, openAdminTab, settleAdmin } from './nh-assistant-admin-navigation';
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
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);

    // The list says where it works.
    expect(text(element.querySelector('[data-agent-row="chat-helper"] [data-agent-work]'))).toBe('Chat only');

    // Its page: no rollout and no runs, no run now, no shadow mode; only resuming if it was held.
    const page = await openAdminAgent(fixture, element, 'chat-helper');
    expect(text(page.querySelector('[data-state-details]'))).toBe('Works in the chat only: it is on no object\'s page, has no triggers and does not run on its own.');
    expect(Array.from(page.querySelectorAll('[data-agent-tab]')).map(tab => tab.getAttribute('data-agent-tab'))).toEqual(['overview', 'build', 'activity']);
    expect(page.querySelector('[data-run-now-agent]')).toBeNull();
    expect(page.querySelector('[data-status-menu="agent"]')).toBeNull();
    expect(adminStatusOf(page.querySelector('.page-head') as HTMLElement)).toBe('live');
    await openAdminTab(fixture, element, 'activity');
    expect(page.querySelector('[data-activity]')).toBeNull();
    expect(page.querySelector('nh-assistant-agent-run-history')).toBeNull();
    expect(page.querySelector('nh-assistant-agent-feedback-report')).not.toBeNull();

    // In Build it has no triggers and no works-on; its skills always work live and only change state.
    await openAdminTab(fixture, element, 'build');
    expect(page.querySelector('[data-build-section="triggers"]')).toBeNull();
    expect(page.querySelector('[data-profile-summary] [data-works-on]')).toBeNull();
    const notes = page.querySelector('[data-skill-status="notes"]') as HTMLElement;
    expect(notes).not.toBeNull();
    expect(adminStatusOf(notes)).toBe('live');
    (notes.querySelector('[data-status-menu="notes"]') as HTMLButtonElement).click();
    await settleAdmin(fixture);
    expect(notes.querySelector('[data-state-action]')).not.toBeNull();
    expect(notes.querySelector('[data-shadow-action]')).toBeNull();

    // An agent that runs on objects keeps all of it.
    await backToAdminList(fixture, element);
    const planner = await openAdminAgent(fixture, element, 'planner');
    expect(planner.querySelector('[data-agent-tab="rollout"]')).not.toBeNull();
    expect(planner.querySelector('[data-run-now-agent="planner"]')).not.toBeNull();
    (planner.querySelector('[data-status-menu="agent"]') as HTMLButtonElement).click();
    await settleAdmin(fixture);
    expect(planner.querySelector('[data-state-action]')).not.toBeNull();
  });

  it('reads in Dutch', async () => {
    await configure('nl');
    const { element } = await render(NhAssistantAdminAgentsComponent);

    expect(text(element.querySelector('[data-agent-row="chat-helper"] [data-agent-work]'))).toBe('Alleen chat');
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
