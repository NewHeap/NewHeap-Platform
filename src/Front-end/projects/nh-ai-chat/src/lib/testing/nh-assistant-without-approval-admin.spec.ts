import { Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { NhAssistantAdminAgentsComponent } from '../../../admin/src/public-api';
import { AgentWithoutApproval, ToolCatalogEntry, WithoutApprovalSwitch, WithoutApprovalVersion } from '../models/assistant-admin.models';
import { provideNhAssistant } from '../provide-nh-assistant';
import { openAdminAgent as openAgent, settleAdmin as settle } from './nh-assistant-admin-navigation';

/**
 * FB82: the kinds of action an agent may run without approval, on the agent's Rollout. A tool whose code
 * allows it lists with its switch; it goes to shadow first, and only from shadow live, with a note, and it
 * goes back to shadow or off at any time. The mock API keeps the versions and refuses what the server refuses.
 */

function scenario(canAdminister = true): NhAssistantMockScenario {
  return {
    agents: [{ id: 'chat-helper', version: 1, displayNameKey: 'Chat helper', descriptionKey: 'Helps in the chat.', canMutate: false }],
    turns: [],
    subjectTypes: [{ id: 'project', names: { en: 'Project', nl: 'Project' }, subjects: [] }],
    admin: {
      canAdminister,
      tools: [
        { id: 'projects.complete-task', source: 'local', effect: 'idempotent-mutation', description: 'Complete a task.', allowWithoutApproval: true },
        { id: 'projects.change-status', source: 'local', effect: 'mutation', description: 'Change the status.' }
      ],
      agents: [
        {
          source: 'code', id: 'planner', displayName: 'Project planner', description: 'Plans projects.', instructions: 'Plan.',
          toolSelectors: ['projects.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'execute', isEnabled: true,
          worksOnSubjectTypes: ['project'], worksOnAllSubjects: false,
          skills: [{ skillId: 'notes', description: 'Writes notes.' }]
        },
        {
          source: 'code', id: 'chat-helper', displayName: 'Chat helper', description: 'Helps in the chat.', instructions: 'Help.',
          toolSelectors: ['projects.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'execute', isEnabled: true,
          chatOnly: true
        }
      ],
      runCatalog: { triggerEvents: [], subjectSets: [], ruleHandlers: [], approvalPolicies: [], profiles: [] }
    }
  };
}

async function configure(canAdminister = true, language = 'en'): Promise<void> {
  TestBed.configureTestingModule({
    providers: [
      provideTranslateService({ fallbackLang: 'en' }),
      provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
      provideNhAssistantMockApi(scenario(canAdminister))
    ]
  });
  await firstValueFrom(TestBed.inject(TranslateService).use(language));
}

async function render<T>(component: Type<T>): Promise<{ fixture: ComponentFixture<T>; element: HTMLElement }> {
  const fixture = TestBed.createComponent(component);
  await settle(fixture);
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

async function choose(fixture: ComponentFixture<unknown>, row: HTMLElement, mode: string, note?: string): Promise<void> {
  (row.querySelector(`[data-without-approval-action="${mode}"]`) as HTMLButtonElement).click();
  await settle(fixture);
  if (note !== undefined) {
    const input = row.querySelector('[data-without-approval-form] input') as HTMLInputElement;
    input.value = note;
    input.dispatchEvent(new Event('input'));
  }
  (row.querySelector('[data-without-approval-confirm]') as HTMLButtonElement).click();
  await settle(fixture);
}

describe('Running a kind of action without approval in the administration', () => {
  it('lists the tools that allow it on the rollout and goes to shadow first, then live, then off', async () => {
    await configure();
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const page = await openAgent(fixture, element, 'planner', 'rollout');
    const section = page.querySelector('[data-without-approval]') as HTMLElement;
    const row = () => section.querySelector('[data-without-approval-tool="projects.complete-task"]') as HTMLElement;

    // Only the tool whose code allows it lists, asking for approval, with shadow as its only way on.
    expect(section.querySelectorAll('[data-without-approval-tool]').length).toBe(1);
    expect(row().getAttribute('data-without-approval-mode')).toBe('off');
    expect(text(row().querySelector('[data-without-approval-status]'))).toBe('Asks for approval');
    expect(Array.from(row().querySelectorAll('[data-without-approval-action]')).map(item => item.getAttribute('data-without-approval-action'))).toEqual(['shadow']);

    // Shadow explains that proposals still wait and keeps the note.
    (row().querySelector('[data-without-approval-action="shadow"]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(text(row().querySelector('[data-without-approval-form] p'))).toContain('still wait for approval');
    (row().querySelector('[data-without-approval-confirm]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(row().getAttribute('data-without-approval-mode')).toBe('shadow');
    expect(text(row().querySelector('[data-without-approval-changed]'))).toContain('Version 1');
    expect(Array.from(row().querySelectorAll('[data-without-approval-action]')).map(item => item.getAttribute('data-without-approval-action'))).toEqual(['live', 'off']);

    await choose(fixture, row(), 'live', 'Approved as a rule for a month');
    expect(row().getAttribute('data-without-approval-mode')).toBe('live');
    expect(text(row().querySelector('[data-without-approval-status]'))).toBe('Runs without approval');
    expect(text(row())).toContain('Approved as a rule for a month');

    await choose(fixture, row(), 'off');
    expect(row().getAttribute('data-without-approval-mode')).toBe('off');

    const versions = (await call('GET', 'admin/agents/planner/tools/projects.complete-task/without-approval/versions')).body as WithoutApprovalVersion[];
    expect(versions.map(version => [version.version, version.mode])).toEqual([[3, 'off'], [2, 'live'], [1, 'shadow']]);
  });

  it('shows why a refused switch did not apply', async () => {
    await configure();
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const page = await openAgent(fixture, element, 'planner', 'rollout');
    // Someone else switched it in the meantime, so the version the page read is stale.
    await call('PUT', 'admin/agents/planner/tools/projects.complete-task/without-approval', { mode: 'shadow', expectedVersion: 0 });
    const row = page.querySelector('[data-without-approval-tool="projects.complete-task"]') as HTMLElement;

    await choose(fixture, row, 'shadow');

    expect(row.querySelector('[data-without-approval-error]')).not.toBeNull();
    expect(text(row.querySelector('[data-without-approval-error]'))).not.toBe('');
  });

  it('reads in Dutch', async () => {
    await configure(true, 'nl');
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const page = await openAgent(fixture, element, 'planner', 'rollout');
    const section = page.querySelector('[data-without-approval]') as HTMLElement;

    expect(text(section.querySelector('h4'))).toBe('Soorten acties zonder goedkeuring');
    expect(text(section.querySelector('[data-without-approval-status]'))).toBe('Vraagt om goedkeuring');
    expect(section.querySelector('[data-without-approval-not-applicable]')).toBeNull();
  });

  it('is refused by the mock API as by the server: live only from shadow, the code key and the administration policy', async () => {
    await configure();
    const route = 'admin/agents/planner/tools/projects.complete-task/without-approval';

    const listed = (await call('GET', 'admin/agents/planner/without-approval')).body as AgentWithoutApproval;
    const tools = (await call('GET', 'admin/tools')).body as ToolCatalogEntry[];
    expect(listed.applies).toBeTrue();
    expect(listed.tools.map(tool => [tool.toolId, tool.mode, tool.version])).toEqual([['projects.complete-task', 'off', 0]]);
    expect(tools.find(tool => tool.id === 'projects.change-status')?.allowWithoutApproval).toBeFalse();

    const liveFirst = await call('PUT', route, { mode: 'live', expectedVersion: 0 });
    const withoutKey = await call('PUT', 'admin/agents/planner/tools/projects.change-status/without-approval', { mode: 'shadow', expectedVersion: 0 });
    const chatOnly = await call('PUT', 'admin/agents/chat-helper/tools/projects.complete-task/without-approval', { mode: 'shadow', expectedVersion: 0 });
    const invalid = await call('PUT', route, { mode: 'sometimes', expectedVersion: 0 });
    const shadow = await call('PUT', route, { mode: 'shadow', expectedVersion: 0 });
    const stale = await call('PUT', route, { mode: 'live', expectedVersion: 0 });

    expect([liveFirst.status, liveFirst.body.code]).toEqual([409, 'assistant-without-approval-shadow-first']);
    expect([withoutKey.status, withoutKey.body.code]).toEqual([409, 'assistant-without-approval-not-allowed']);
    expect([chatOnly.status, chatOnly.body.code]).toEqual([409, 'assistant-without-approval-not-allowed']);
    expect(invalid.status).toBe(400);
    expect((shadow.body as WithoutApprovalSwitch).version).toBe(1);
    expect([stale.status, stale.body.code]).toEqual([409, 'assistant-version-conflict']);
  });

  it('answers 403 to someone outside the administration', async () => {
    await configure(false);

    const read = await call('GET', 'admin/agents/planner/without-approval');
    const write = await call('PUT', 'admin/agents/planner/tools/projects.complete-task/without-approval', { mode: 'shadow', expectedVersion: 0 });

    expect([read.status, read.body.code]).toEqual([403, 'assistant-forbidden']);
    expect([write.status, write.body.code]).toEqual([403, 'assistant-forbidden']);
  });
});
