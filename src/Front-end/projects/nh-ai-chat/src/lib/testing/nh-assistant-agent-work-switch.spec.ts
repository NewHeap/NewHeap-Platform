import { Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { NhAssistantAdminAgentsComponent } from '../../../admin/src/public-api';
import { mockStatusOf } from '../../../testing/src/nh-assistant-mock-status';
import { AdminAgent, AgentState } from '../models/assistant-admin.models';
import { AssistantStatus } from '../models/assistant-api.models';
import { NH_ASSISTANT_SERVER_ERROR_CODES } from '../i18n/nh-assistant-error-codes';
import { provideNhAssistant } from '../provide-nh-assistant';

/**
 * Agent work switched off on the host (FB17, `NewHeap:AI:Assistant:AgentWork`): `GET status` and the
 * administration report `agentWork: false`, the agent list says so in a banner above the agents, and the
 * mock API refuses to resume a paused agent or skill with `409 assistant-agent-work-off`, as the server does.
 */

function scenario(agentWork: boolean): NhAssistantMockScenario {
  return {
    agentWork,
    agents: [{ id: 'chat-helper', version: 1, displayNameKey: 'Chat helper', descriptionKey: 'Helps in the chat.', canMutate: false }],
    turns: [],
    subjectTypes: [{ id: 'project', names: { en: 'Project', nl: 'Project' }, subjects: [] }],
    admin: {
      agents: [
        {
          source: 'code', id: 'planner', displayName: 'Project planner', description: 'Plans projects.', instructions: 'Plan.',
          toolSelectors: ['projects.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'execute', isEnabled: true,
          worksOnSubjectTypes: ['project'], worksOnAllSubjects: false,
          skills: [{ skillId: 'notes', description: 'Writes notes.' }]
        }
      ],
      runCatalog: { triggerEvents: [], subjectSets: [], ruleHandlers: [], approvalPolicies: [], profiles: [] }
    }
  };
}

function flush(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve));
}

async function configure(agentWork: boolean, language = 'en'): Promise<void> {
  TestBed.configureTestingModule({
    providers: [
      provideTranslateService({ fallbackLang: 'en' }),
      provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
      provideNhAssistantMockApi(scenario(agentWork))
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

describe('Agent work switched off on the host', () => {
  it('shows a banner above the agent list, whose statuses stay as they are', async () => {
    await configure(false);
    const { element } = await render(NhAssistantAdminAgentsComponent);

    const banner = element.querySelector('[data-agent-list] [data-agent-work-off]');
    expect(banner).not.toBeNull();
    expect(banner!.getAttribute('role')).toBe('status');
    expect(text(banner)).toBe(
      'Agent work is off. No agent works on its own, takes work from others or is consulted, and approved proposals wait until '
      + 'agent work is on again. People keep chatting, and you can keep managing agents here; a paused agent resumes once agent work is on.');
    expect(element.querySelector('[data-agent-row="planner"] [data-status]')?.getAttribute('data-status-value')).toBe('live');
  });

  it('reads in Dutch', async () => {
    await configure(false, 'nl');
    const { element } = await render(NhAssistantAdminAgentsComponent);

    expect(text(element.querySelector('[data-agent-work-off] strong'))).toBe('Agentwerk staat uit.');
  });

  it('shows no banner while agents work', async () => {
    await configure(true);
    const { element } = await render(NhAssistantAdminAgentsComponent);

    expect(element.querySelector('[data-agent-row="planner"]')).not.toBeNull();
    expect(element.querySelector('[data-agent-work-off]')).toBeNull();
  });

  it('is reported by the mock API, which refuses to resume a paused agent or skill as the server does', async () => {
    await configure(true);
    const backend = TestBed.inject(NhAssistantMockBackend);
    expect(((await call('GET', 'status')).body as AssistantStatus).agentWork).toBeTrue();
    expect(((await call('GET', 'admin/agents')).body as AdminAgent[]).every(agent => agent.agentWork === true)).toBeTrue();

    backend.setAgentWork(false);
    const status = (await call('GET', 'status')).body as AssistantStatus;
    const listed = (await call('GET', 'admin/agents')).body as AdminAgent[];
    const state = (await call('GET', 'admin/agents/planner/state')).body as AgentState;
    expect([status.enabled, status.agentWork, status.agents.length]).toEqual([true, false, 1]);
    expect(listed.map(agent => agent.agentWork)).toEqual([false]);
    expect([state.runState, state.active, state.agentWork]).toEqual(['active', false, false]);

    // Pausing serves; resuming a paused skill of the active agent, and then the paused agent, is refused.
    expect((await call('POST', 'admin/agents/planner/skills/notes/state', { state: 'paused' })).status).toBe(200);
    const skill = await call('POST', 'admin/agents/planner/skills/notes/state', { state: 'active' });
    expect([skill.status, skill.body.code]).toEqual([409, 'assistant-agent-work-off']);
    expect((await call('POST', 'admin/agents/planner/state', { state: 'paused', reason: null })).status).toBe(200);
    const resumed = await call('POST', 'admin/agents/planner/state', { state: 'active', reason: null });
    expect([resumed.status, resumed.body.code]).toEqual([409, 'assistant-agent-work-off']);

    // Once agents work again, the agent resumes; with the assistant disabled nobody works.
    backend.setAgentWork(true);
    expect((await call('POST', 'admin/agents/planner/state', { state: 'active', reason: null })).body.active).toBeTrue();
    backend.setEnabled(false);
    expect(((await call('GET', 'status')).body as AssistantStatus).agentWork).toBeFalse();
  });

  it('names the code with its status and texts', () => {
    expect(NH_ASSISTANT_SERVER_ERROR_CODES).toContain('assistant-agent-work-off');
    expect(mockStatusOf('assistant-agent-work-off')).toBe(409);
  });
});
