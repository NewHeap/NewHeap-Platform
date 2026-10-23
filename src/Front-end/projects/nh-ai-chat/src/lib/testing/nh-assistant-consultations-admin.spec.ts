import { Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom, throwError } from 'rxjs';
import { NhAssistantAdminAgentsComponent } from '../../../admin/src/public-api';
import { AgentRunConfiguration, RunCatalog, ToolCatalogEntry } from '../models/assistant-admin.models';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAdminApiService } from '../services/nh-assistant-admin-api.service';
import { NhAssistantApiError } from '../services/nh-assistant-transport';

/**
 * Consultations defined as data in the administration (AC20): the mock API keeps them within the
 * ceilings in code as the server does, and the run configuration of an agent shows its consultations
 * in code read only and those as data with a form, the agents that ask them, their problems, the route
 * switches and the flow view's when step.
 */

/** Each searchable picker waits for its search, so a form flow takes several seconds. */
const formFlowTimeout = 20_000;

function scenario(): NhAssistantMockScenario {
  return {
    agents: [{ id: 'projects', version: 1, displayNameKey: 'Project assistant', descriptionKey: 'Helps with projects.', canMutate: true }],
    turns: [],
    subjectTypes: [
      { id: 'project', names: { en: 'Project', nl: 'Project' }, pluralNames: { en: 'Projects', nl: 'Projecten' }, subjects: [] },
      { id: 'task', names: { en: 'Task', nl: 'Taak' }, pluralNames: { en: 'Tasks', nl: 'Taken' }, subjects: [] }
    ],
    admin: {
      tools: [{ id: 'projects.list', source: 'local', effect: 'read-only', description: 'Lists projects.' }],
      // The application lets administrators define consultations: the asker decides, or approvers with
      // the agent's rights; requests; lower limits than code allows.
      consultationsAsData: {
        routes: ['asker-decides', 'subject-agent-rights'],
        allowRequests: true,
        maxToolCalls: 8,
        maxTimeoutSeconds: 120,
        maxAnswerChars: 8_000,
        maxHistoryMessages: 40
      },
      agents: [
        {
          source: 'code', id: 'projects', displayName: 'Project assistant', description: 'Projects.', instructions: 'Help.',
          toolSelectors: ['projects.*', 'assistant.consult.starter.project'], mcpServerIds: [], requiredPolicy: null, autonomy: 'execute', isEnabled: true
        },
        {
          source: 'code', id: 'reviewer', displayName: 'Intake reviewer', description: 'Reviews intakes.', instructions: 'Review.',
          toolSelectors: [], mcpServerIds: [], requiredPolicy: null, autonomy: 'propose', isEnabled: true,
          consultations: { 'asker-decides': ['task'] },
          consultationDescriptions: { task: 'Reviews the intake of a task.' },
          consultationTools: { task: ['tasks.list'] }
        },
        {
          source: 'code', id: 'closer', displayName: 'Project closer', description: 'Closes projects.', instructions: 'Close.',
          toolSelectors: [], mcpServerIds: [], requiredPolicy: null, autonomy: 'execute', isEnabled: true,
          serviceAccount: 'operations',
          consultations: { 'subject-agent-rights': ['task'] },
          consultableAsData: { routes: ['asker-decides'] }
        },
        {
          source: 'admin', id: 'starter', displayName: 'Project starter', description: 'Starts projects.', instructions: 'Start.',
          toolSelectors: ['projects.*', 'tasks.list'], mcpServerIds: [], requiredPolicy: null, autonomy: 'execute', isEnabled: true,
          worksOnSubjectTypes: ['project'], worksOnAllSubjects: false, worksOnApplication: false,
          runConfiguration: { runSettings: { accountability: 'service-account', serviceAccountActorId: 'operations', approvalPolicy: 'project-review' } },
          dataConsultations: [{
            subjectType: 'project',
            description: 'Says whether a draft project can start.',
            minimumRole: 'participant',
            routes: ['asker-decides'],
            proposalPolicy: 'asker-only'
          }]
        },
        {
          source: 'admin', id: 'drafts', displayName: 'Draft helper', description: 'Helps with drafts.', instructions: 'Help.',
          toolSelectors: [], mcpServerIds: [], requiredPolicy: null, autonomy: 'propose', isEnabled: true
        },
        {
          source: 'admin', id: 'helper', displayName: 'Helper', description: 'Helps.', instructions: 'Help.',
          toolSelectors: [], mcpServerIds: [], requiredPolicy: null, autonomy: 'propose', isEnabled: true,
          runConfiguration: { runSettings: { accountability: 'agent' } }
        }
      ],
      accounts: [{ actorId: 'operations', displayName: 'Operations', detail: null }],
      runCatalog: {
        triggerEvents: [],
        subjectSets: [],
        ruleHandlers: [],
        approvalPolicies: ['project-review', 'asker-only'],
        approvalPolicySummaries: [
          { name: 'project-review', approvers: [{ kind: 'subject-role', role: 'approver', namedRole: null, policy: null }], lifetimeMinutes: 4320, fourEyes: true, mode: 'deferred' },
          { name: 'asker-only', approvers: [{ kind: 'initiator', role: null, namedRole: null, policy: null }], lifetimeMinutes: 60, fourEyes: false, mode: 'inline' }
        ],
        profiles: [],
        agentPolicies: [{ name: 'app.project.manage', description: 'Project managers.' }]
      }
    }
  };
}

function flush(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve));
}

async function configure(script = scenario()): Promise<void> {
  TestBed.configureTestingModule({
    providers: [
      provideTranslateService({ fallbackLang: 'en' }),
      provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
      provideNhAssistantMockApi(script)
    ]
  });
  await firstValueFrom(TestBed.inject(TranslateService).use('en'));
}

async function settle(fixture: ComponentFixture<unknown>): Promise<HTMLElement> {
  for (let round = 0; round < 8; round++) {
    fixture.detectChanges();
    await flush();
  }
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

async function render<T>(component: Type<T>): Promise<{ fixture: ComponentFixture<T>; element: HTMLElement }> {
  const fixture = TestBed.createComponent(component);
  return { fixture, element: await settle(fixture) };
}

function text(element: Element | null | undefined): string {
  return element?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

function button(scope: Element, label: string): HTMLButtonElement {
  const match = Array.from(scope.querySelectorAll('button')).find(item => text(item) === label);
  if (!match) {
    throw new Error(`Button "${label}" not found.`);
  }
  return match;
}

function type(scope: Element, selector: string, value: string): void {
  const input = scope.querySelector(selector) as HTMLInputElement | HTMLTextAreaElement;
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

/** Chooses an option of a picker: opens it, types the query and clicks the option. */
async function pick(fixture: ComponentFixture<unknown>, scope: HTMLElement, selector: string, query: string, label: string): Promise<void> {
  const input = scope.querySelector(selector) as HTMLInputElement;
  input.click();
  input.value = query;
  input.dispatchEvent(new Event('input'));
  await settle(fixture);
  await new Promise(resolve => setTimeout(resolve, 300));
  await settle(fixture);
  const option = Array.from(scope.querySelectorAll<HTMLElement>('[role="option"]'))
    .find(item => item.querySelector('.label')?.textContent?.trim() === label);
  if (!option) {
    throw new Error(`Option "${label}" not found.`);
  }
  option.click();
  await settle(fixture);
}

async function call(method: string, path: string, body?: unknown): Promise<{ status: number; body: any }> {
  const response = await TestBed.inject(NhAssistantMockBackend).fetch(`/api/assistant/${path}`, {
    method,
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  const content = await response.text();
  return { status: response.status, body: content ? JSON.parse(content) : null };
}

function requestBodies(method: string, path: string): unknown[] {
  return TestBed.inject(NhAssistantMockBackend).requests
    .filter(item => item.method === method && item.path === path)
    .map(item => item.body);
}

async function configuration(agentId: string): Promise<AgentRunConfiguration> {
  return (await call('GET', `admin/agents/${agentId}/configuration`)).body as AgentRunConfiguration;
}

/** Changes the starter's own tool selectors, as an administrator does in its editor. */
async function changeStarterTools(toolSelectors: string[]): Promise<void> {
  const agents = (await call('GET', 'admin/agents')).body as { id: string; version: number }[];
  const { version, ...definition } = agents.find(item => item.id === 'starter')!;
  const changed = await call('PUT', 'admin/agents/starter', { ...definition, toolSelectors, expectedVersion: version });
  expect(changed.status).toBe(200);
}

/** Opens the run configuration of the agent on the agents page and returns it. */
async function openRunConfiguration(fixture: ComponentFixture<unknown>, element: HTMLElement, agentId: string): Promise<HTMLElement> {
  (element.querySelector(`[data-run-config-agent="${agentId}"]`) as HTMLButtonElement).click();
  await settle(fixture);
  return element.querySelector('nh-assistant-agent-run-configuration') as HTMLElement;
}

describe('Consultations defined as data in the mock API', () => {
  beforeEach(async () => configure());

  it('reports the ceilings in code, the consultations in code and those defined as data with their askers and tools', async () => {
    const catalog = (await call('GET', 'admin/run-catalog')).body as RunCatalog;
    const ceiling = {
      routes: ['asker-decides', 'subject-agent-rights'],
      allowRequests: true,
      allowMcp: false,
      maxToolCalls: 8,
      maxTimeoutSeconds: 120,
      maxAnswerChars: 8_000,
      maxHistoryMessages: 40,
      maxConsultations: 20,
      maxInstructionsLength: 4_000
    };
    expect(catalog.consultationCeiling).toEqual(ceiling as RunCatalog['consultationCeiling']);

    // An agent created here gets the application's ceiling; its consultation answers, and the asker
    // decides the proposals, as the project assistant selects its tool.
    const starter = await configuration('starter');
    expect(starter.consultationCeiling).toEqual(ceiling as AgentRunConfiguration['consultationCeiling']);
    expect(starter.codeConsultations).toEqual([]);
    expect(starter.dataConsultations).toEqual([jasmine.objectContaining({
      subjectType: 'project',
      version: 1,
      description: 'Says whether a draft project can start.',
      minimumRole: 'participant',
      askerPolicy: null,
      routes: ['asker-decides'],
      proposalRole: 'participant',
      proposalPolicy: 'asker-only',
      // Limits left out start at the code's defaults within the ceiling.
      maxToolCalls: 4,
      timeoutSeconds: 60,
      maxAnswerChars: 4_000,
      maxHistoryMessages: 20,
      inEffect: true,
      problem: null,
      problems: [],
      routesInEffect: ['asker-decides'],
      // No selection offers every tool of the agent.
      toolSelectors: null,
      toolSelectorsInEffect: null,
      consultToolId: 'assistant.consult.starter.project',
      requestToolId: null,
      askingAgents: ['projects']
    })]);

    // An agent in code shows its consultations in code; only one that is consultable as data gets a
    // ceiling, within the application's.
    const reviewer = await configuration('reviewer');
    expect(reviewer.codeConsultations).toEqual([{
      subjectType: 'task',
      description: 'Reviews the intake of a task.',
      allowedRoutes: ['asker-decides'],
      allowRequests: false,
      exposure: ['local', 'agent'],
      consultToolId: 'assistant.consult.reviewer.task',
      requestToolId: null,
      askingAgents: [],
      toolSelectors: ['tasks.list']
    }]);
    expect(reviewer.consultationCeiling).toBeNull();
    const closer = await configuration('closer');
    expect(closer.consultationCeiling).toEqual(jasmine.objectContaining({ routes: ['asker-decides'], allowRequests: false, allowMcp: false, maxToolCalls: 8 }));

    // The consult tool is in the tool list, and the consultation counts for the starter's route switches.
    const tools = (await call('GET', 'admin/tools')).body as ToolCatalogEntry[];
    expect(tools).toContain(jasmine.objectContaining({ id: 'assistant.consult.starter.project', effect: 'read-only' }));
    const state = (await call('GET', 'admin/agents/starter/state')).body;
    expect(state.consultationRoutes.routes).toEqual([
      { route: 'asker-decides', allowed: true, override: null, on: true, subjectTypes: ['project'] },
      { route: 'subject-agent-rights', allowed: false, override: null, on: false, subjectTypes: [] },
      { route: 'subject-asker-rights', allowed: false, override: null, on: false, subjectTypes: [] }
    ]);
    // Nobody can consult an agent without consultations.
    expect((await call('GET', 'admin/agents/helper/state')).body.consultationRoutes).toBeNull();
  });

  it('refuses what the server refuses, with its codes and field errors', async () => {
    const save = (agentId: string, subjectType: string, body: object) => call('PUT', `admin/agents/${agentId}/consultations/${subjectType}`, {
      description: 'Answers about it.',
      ...body
    });
    const outcome = async (request: Promise<{ status: number; body: any }>) => {
      const result = await request;
      return [result.status, result.body?.code, result.body?.errors ?? null];
    };

    expect(await outcome(save('reviewer', 'project', {}))).toEqual([409, 'assistant-consultations-not-allowed', null]);
    expect(await outcome(save('drafts', 'project', {}))).toEqual([409, 'assistant-agent-not-configured', null]);
    expect(await outcome(save('closer', 'task', {}))).toEqual([409, 'assistant-consultation-in-code', null]);
    expect(await outcome(call('DELETE', 'admin/agents/closer/consultations/task'))).toEqual([409, 'assistant-consultation-in-code', null]);
    expect(await outcome(save('starter', 'task', {}))).toEqual([400, 'assistant-validation', { subjectType: ['invalid'] }]);
    expect(await outcome(save('starter', 'project', { description: ' ', expectedVersion: 1 })))
      .toEqual([400, 'assistant-validation', { description: ['required'] }]);
    expect(await outcome(save('starter', 'project', { routes: ['subject-asker-rights'], expectedVersion: 1 })))
      .toEqual([400, 'assistant-validation', { routes: ['not-allowed'] }]);
    expect(await outcome(save('starter', 'project', { routes: ['subject-agent-rights'], proposalPolicy: 'asker-only', expectedVersion: 1 })))
      .toEqual([400, 'assistant-validation', { proposalPolicy: ['invalid'] }]);
    expect(await outcome(save('helper', 'project', { routes: ['subject-agent-rights'] })))
      .toEqual([400, 'assistant-validation', { routes: ['invalid'] }]);
    expect(await outcome(save('closer', 'project', { allowRequests: true })))
      .toEqual([400, 'assistant-validation', { allowRequests: ['not-allowed'] }]);
    expect(await outcome(save('starter', 'project', { maxToolCalls: 9, expectedVersion: 1 })))
      .toEqual([400, 'assistant-validation', { limits: ['not-allowed'] }]);
    expect(await outcome(save('starter', 'project', { maxToolCalls: 40, expectedVersion: 1 })))
      .toEqual([400, 'assistant-validation', { limits: ['invalid'] }]);
    expect(await outcome(save('starter', 'project', { askerPolicy: 'app.unknown', expectedVersion: 1 })))
      .toEqual([400, 'assistant-validation', { askerPolicy: ['not-found'] }]);
    expect(await outcome(save('starter', 'project', {}))).toEqual([409, 'assistant-version-conflict', null]);
    expect(await outcome(call('DELETE', 'admin/agents/starter/consultations/task'))).toEqual([404, 'assistant-consultation-not-found', null]);
    // Run settings stay while consultations belong to the agent.
    expect(await outcome(call('DELETE', 'admin/agents/starter/run-settings'))).toEqual([409, 'assistant-run-settings-in-use', null]);
  });

  it('keeps a consultation that no longer fits in effect without the parts that no longer fit', async () => {
    const saved = await call('PUT', 'admin/agents/starter/consultations/project', {
      description: 'Says whether a draft project can start.',
      routes: ['asker-decides', 'subject-agent-rights'],
      expectedVersion: 1
    });
    expect([saved.status, saved.body.version, saved.body.routesInEffect]).toEqual([200, 2, ['asker-decides', 'subject-agent-rights']]);

    // The starter now answers for itself: it has no rights of its own to lend.
    const settings = (await configuration('starter')).runSettings!;
    expect((await call('PUT', 'admin/agents/starter/run-settings', { accountability: 'agent', expectedVersion: settings.version })).status).toBe(200);

    const starter = await configuration('starter');
    expect(starter.dataConsultations![0]).toEqual(jasmine.objectContaining({
      inEffect: true,
      problem: 'agent-has-no-rights',
      problems: ['agent-has-no-rights'],
      routes: ['asker-decides', 'subject-agent-rights'],
      routesInEffect: ['asker-decides']
    }));
    expect(starter.problems).toContain({ agentId: 'starter', kind: 'consultation', id: 'project', code: 'agent-has-no-rights' });
  });

  it('keeps the tools of a consultation within the agent\'s own and offers none when they no longer fit', async () => {
    const save = (body: object) => call('PUT', 'admin/agents/starter/consultations/project', {
      description: 'Says whether a draft project can start.',
      routes: ['asker-decides'],
      proposalPolicy: 'asker-only',
      expectedVersion: 1,
      ...body
    });
    const outcome = async (request: Promise<{ status: number; body: any }>) => {
      const result = await request;
      return [result.status, result.body?.errors ?? null];
    };

    // Saving refuses a tool outside the agent's own and a selector that is none.
    expect(await outcome(save({ toolSelectors: ['orders.*'] }))).toEqual([400, { toolSelectors: ['not-allowed'] }]);
    expect(await outcome(save({ toolSelectors: ['Projects List'] }))).toEqual([400, { toolSelectors: ['invalid'] }]);
    const saved = await save({ toolSelectors: ['projects.list'] });
    expect([saved.status, saved.body.version, saved.body.toolSelectors, saved.body.toolSelectorsInEffect])
      .toEqual([200, 2, ['projects.list'], ['projects.list']]);

    // The agent's own tools change: the selection drops with a problem and offers no tools, never all.
    await changeStarterTools(['tasks.list']);
    const starter = await configuration('starter');
    expect(starter.dataConsultations![0]).toEqual(jasmine.objectContaining({
      inEffect: true,
      problems: ['tools-not-allowed'],
      toolSelectors: ['projects.list'],
      toolSelectorsInEffect: []
    }));
    expect(starter.problems).toContain({ agentId: 'starter', kind: 'consultation', id: 'project', code: 'tools-not-allowed' });
    expect(await outcome(save({ toolSelectors: ['projects.list'], expectedVersion: 2 }))).toEqual([400, { toolSelectors: ['not-allowed'] }]);

    // Without a selection, or an empty one, every tool of the agent again.
    const cleared = await save({ toolSelectors: [], expectedVersion: 2 });
    expect([cleared.status, cleared.body.toolSelectors, cleared.body.toolSelectorsInEffect, cleared.body.problems]).toEqual([200, null, null, []]);
  });

  it('removes a consultation; its tool and its routes go with it', async () => {
    const removed = await call('DELETE', 'admin/agents/starter/consultations/project?expectedVersion=1');

    expect(removed.status).toBe(204);
    expect(((await call('GET', 'admin/tools')).body as ToolCatalogEntry[]).map(tool => tool.id)).not.toContain('assistant.consult.starter.project');
    expect((await call('GET', 'admin/agents/starter/state')).body.consultationRoutes).toBeNull();
    expect((await configuration('starter')).dataConsultations).toEqual([]);
    expect((await call('DELETE', 'admin/agents/starter/consultations/project')).status).toBe(404);
  });
});

describe('The Consultations section of the run configuration', () => {
  beforeEach(async () => configure());

  it('lists a consultation in code read only and one as data with who may ask, who decides, its askers and route switches', async () => {
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    let view = await openRunConfiguration(fixture, element, 'closer');

    // A consultation in code: read only, with the routes code allows and why only the switches change it.
    let section = view.querySelector('[data-consultations]') as HTMLElement;
    expect(text(section.querySelector('h4'))).toBe('Consultations');
    const code = section.querySelector('[data-consultation="task"][data-consultation-origin="code"]') as HTMLElement;
    expect(text(code.querySelector('.card-header'))).toContain('Task');
    expect(text(code.querySelector('.card-header'))).toContain('Code');
    expect(text(code.querySelector('[data-consultation-decides]'))).toBe("The agent may propose changes: Approvers decide, agent's rights.");
    expect(text(code.querySelector('[data-consultation-no-askers]'))).toContain('No agent asks it yet.');
    expect(text(code.querySelector('[data-consultation-tool]'))).toBe('assistant.consult.closer.task');
    expect(code.querySelector('button')).toBeNull();
    // The agent in code is consultable as data, so administrators may add one.
    expect(button(section, 'Add consultation').disabled).toBeFalse();
    expect(section.querySelector('[data-consultations-closed]')).toBeNull();
    expect(section.querySelector('nh-assistant-agent-consultation-routes [data-consultation-routes]')).not.toBeNull();

    // A consultation as data of an agent created here: the sentences, the asking agent and the routes.
    button(element, 'Back to agents').click();
    await settle(fixture);
    view = await openRunConfiguration(fixture, element, 'starter');
    section = view.querySelector('[data-consultations]') as HTMLElement;
    const data = section.querySelector('[data-consultation="project"][data-consultation-origin="data"]') as HTMLElement;
    expect(text(data.querySelector('.card-header'))).toContain('Version 1');
    expect(text(data)).toContain('Says whether a draft project can start.');
    expect(text(data.querySelector('[data-consultation-ask]')))
      .toBe('People who are at least participant of the object may ask. They also need the policy the agent requires, or else access to the assistant.');
    expect(text(data.querySelector('[data-consultation-decides]')))
      .toBe('The agent may propose changes when the person who asked is at least participant: The person who asked decides.');
    expect(text(data)).toContain('At most 4 tool calls, 60 seconds, 4000 characters and 20 thread messages');
    const asker = data.querySelector('[data-asker="projects"]') as HTMLButtonElement;
    expect(text(asker)).toBe('Project assistant');
    expect(asker.getAttribute('aria-label')).toBe('Open the editor of Project assistant');
    expect(button(data, 'Edit').getAttribute('aria-label')).toBe('Edit the consultation about Project');
    // Its only kind of object has a consultation already.
    expect(button(section, 'Add consultation').disabled).toBeTrue();
    expect(text(section.querySelector('[data-consultations-closed="no-types"]'))).toBe('Every kind of object this agent works on already has a consultation.');

    // The route switches show for an agent whose only consultation is data, with the type it allows.
    const routes = section.querySelector('nh-assistant-agent-consultation-routes') as HTMLElement;
    expect(text(routes.querySelector('h5'))).toBe('Consultation routes');
    expect(text(routes.querySelector('[data-route="asker-decides"] [data-route-types]'))).toBe('For: Projects');
    expect(text(routes.querySelector('[data-route="asker-decides"] [data-route-source]'))).toBe('As the consultations allow');
    expect(text(routes.querySelector('[data-route="subject-asker-rights"] [data-route-source]'))).toBe('Not allowed');

    // The asking agent opens its editor; cancelling returns to the starter's run configuration.
    asker.click();
    await settle(fixture);
    const editor = element.querySelector('nh-assistant-admin-agent-editor') as HTMLElement;
    expect((editor.querySelector('input[id$="-id"]') as HTMLInputElement).value).toBe('projects');
    button(editor, 'Cancel').click();
    await settle(fixture);
    expect(text(element.querySelector('nh-assistant-agent-run-configuration h3'))).toBe('Run configuration of Project starter');
  }, formFlowTimeout);

  it('says why an agent cannot get consultations', async () => {
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);

    // An agent in code that is not consultable as data shows its consultations in code and why.
    let view = await openRunConfiguration(fixture, element, 'reviewer');
    let section = view.querySelector('[data-consultations]') as HTMLElement;
    expect(section.querySelector('[data-consultation="task"]')).not.toBeNull();
    expect(text(section.querySelector('[data-consultations-closed="agent"]')))
      .toBe('This agent is defined in code and does not let administrators add consultations to it.');
    expect(Array.from(section.querySelectorAll('button')).some(item => text(item) === 'Add consultation')).toBeFalse();

    // An agent created here needs run settings first.
    button(element, 'Back to agents').click();
    await settle(fixture);
    view = await openRunConfiguration(fixture, element, 'drafts');
    section = view.querySelector('[data-consultations]') as HTMLElement;
    const add = button(section, 'Add consultation');
    expect(add.disabled).toBeTrue();
    const closed = section.querySelector('[data-consultations-closed="needs-settings"]') as HTMLElement;
    expect(text(closed)).toBe('Give the agent run settings before you add consultations.');
    expect(add.getAttribute('aria-describedby')).toBe(closed.id);
    expect(text(section.querySelector('[data-consultations-empty]'))).toBe('Nobody can consult this agent yet.');
  }, formFlowTimeout);

  it('adds a consultation with only the routes and options the ceiling and the agent allow', async () => {
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const view = await openRunConfiguration(fixture, element, 'helper');
    const section = () => view.querySelector('[data-consultations]') as HTMLElement;
    button(section(), 'Add consultation').click();
    await settle(fixture);
    const form = () => section().querySelector('[data-form="consultation"]') as HTMLElement;
    expect(text(form().querySelector('h5'))).toBe('New consultation');
    expect(form().contains(document.activeElement)).toBeTrue();

    // Without a description and with proposals but no route, the form says so at the fields.
    (form().querySelector('[data-mode="propose"]') as HTMLInputElement).click();
    await settle(fixture);
    const routes = () => form().querySelector('[data-consultation-routes]') as HTMLElement;
    // Proposing starts with the first route the agent may use.
    expect((routes().querySelector('[data-route-choice="asker-decides"] input') as HTMLInputElement).checked).toBeTrue();
    (routes().querySelector('[data-route-choice="asker-decides"] input') as HTMLInputElement).click();
    await settle(fixture);
    button(form(), 'Save').click();
    await settle(fixture);
    expect(text(form().querySelector('[id$="-type-error"]'))).toBe('Required.');
    expect(text(form().querySelector('[id$="-description-error"]'))).toBe('Required.');
    expect(text(routes().querySelector('.field-error'))).toBe('Choose at least one route, or let it only answer.');
    expect(requestBodies('PUT', 'admin/agents/helper/consultations/project')).toEqual([]);

    // A route outside the ceiling and the route with the agent's rights for an agent that answers for
    // itself stay disabled with the reason.
    const outside = routes().querySelector('[data-route-choice="subject-asker-rights"] input') as HTMLInputElement;
    expect(outside.disabled).toBeTrue();
    expect(text(form().querySelector(`#${outside.getAttribute('aria-describedby')}`))).toBe('The application does not allow this route.');
    const agentRights = routes().querySelector('[data-route-choice="subject-agent-rights"] input') as HTMLInputElement;
    expect(agentRights.disabled).toBeTrue();
    expect(text(form().querySelector(`#${agentRights.getAttribute('aria-describedby')}`)))
      .toBe('Not for an agent that answers for itself: it has no rights of its own to lend.');

    // The kinds of object the agent works on; the ceiling's limits and options.
    await pick(fixture, form(), 'input[id$="-type"]', 'Pro', 'Project');
    type(form(), 'textarea[id$="-description"]', 'Answers questions about a project.');
    (routes().querySelector('[data-route-choice="asker-decides"] input') as HTMLInputElement).click();
    const toolCalls = form().querySelector('input[id$="-maxToolCalls"]') as HTMLInputElement;
    expect([toolCalls.max, toolCalls.placeholder]).toEqual(['8', '4']);
    expect(text(form().querySelector('[id$="-maxToolCalls-hint"]'))).toBe('From 0 to 8.');
    expect(form().querySelector('[data-consultation-requests] input[type="checkbox"]')).not.toBeNull();
    expect(text(form().querySelector('[data-mcp-closed]'))).toBe('The application does not offer consultations to MCP clients.');
    type(form(), 'input[id$="-maxToolCalls"]', '9');
    await settle(fixture);
    button(form(), 'Save').click();
    await settle(fixture);
    // Advanced opens to show the error and take the focus.
    expect((form().querySelector('[data-consultation-advanced]') as HTMLDetailsElement).open).toBeTrue();
    expect(text(form().querySelector('[id$="-maxToolCalls-error"]'))).toBe('Enter a whole number from 0 to 8.');
    expect(document.activeElement).toBe(form().querySelector('input[id$="-maxToolCalls"]'));
    type(form(), 'input[id$="-maxToolCalls"]', '6');
    (form().querySelector('[data-consultation-requests] input[type="checkbox"]') as HTMLInputElement).click();
    await settle(fixture);
    button(form(), 'Save').click();
    await settle(fixture);

    expect(requestBodies('PUT', 'admin/agents/helper/consultations/project')).toEqual([{
      description: 'Answers questions about a project.',
      instructions: null,
      minimumRole: 'reader',
      askerPolicy: null,
      routes: ['asker-decides'],
      proposalRole: 'participant',
      proposalPolicy: null,
      toolSelectors: null,
      maxToolCalls: 6,
      timeoutSeconds: null,
      maxAnswerChars: null,
      maxHistoryMessages: null,
      allowRequests: true,
      exposeToMcp: false,
      dataClassification: null,
      expectedVersion: null,
      reason: null
    }]);
    expect(form()).toBeNull();
    expect(text(view.querySelector('[data-saved]'))).toBe('The consultation was added. It answers once an agent that asks selects its tool.');
    const row = section().querySelector('[data-consultation="project"]') as HTMLElement;
    expect(text(row.querySelector('[data-consultation-requests]'))).toBe('Takes requests');
    expect(text(row.querySelector('[data-consultation-no-askers]'))).toContain('No agent asks it yet.');
    // The route switches read the routes the new consultation allows.
    expect(text(section().querySelector('nh-assistant-agent-consultation-routes [data-route="asker-decides"] [data-route-types]'))).toBe('For: Projects');
  }, formFlowTimeout);

  it('warns when the approval policy keeps a route from applying', async () => {
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const view = await openRunConfiguration(fixture, element, 'starter');
    button(view.querySelector('[data-consultation="project"]') as HTMLElement, 'Edit').click();
    await settle(fixture);
    const form = () => view.querySelector('[data-form="consultation"]') as HTMLElement;

    expect(text(form().querySelector('h5'))).toBe('Consultation about Project, version 1');
    expect((form().querySelector('textarea[id$="-description"]') as HTMLTextAreaElement).value).toBe('Says whether a draft project can start.');
    // The asker decides under a policy of the initiator only: nothing to warn about.
    expect(form().querySelector('[data-policy-warning]')).toBeNull();

    // The agent's own policy uses four eyes: the person who asked cannot decide in their chat.
    await pick(fixture, form(), 'input[id$="-proposal-policy"]', 'review', 'project-review');
    expect(text(form().querySelector('[data-policy-warning="four-eyes"]')))
      .toContain('This approval policy uses four eyes, so the person who asked cannot decide their own proposal in their chat');

    // The initiator alone leaves no approver for the route with the agent's account.
    await pick(fixture, form(), 'input[id$="-proposal-policy"]', 'asker', 'asker-only');
    (form().querySelector('[data-route-choice="subject-agent-rights"] input') as HTMLInputElement).click();
    await settle(fixture);
    expect(text(form().querySelector('[data-policy-warning="initiator-only"]')))
      .toContain("so the route with the agent's technical account is refused");
  }, formFlowTimeout);

  it('changes and removes a consultation defined as data', async () => {
    const admin = TestBed.inject(NhAssistantAdminApiService);
    spyOn(admin, 'removeDataConsultation').and.callThrough();
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const view = await openRunConfiguration(fixture, element, 'starter');
    const row = () => view.querySelector('[data-consultation="project"]') as HTMLElement;
    button(row(), 'Edit').click();
    await settle(fixture);
    const form = view.querySelector('[data-form="consultation"]') as HTMLElement;
    type(form, 'textarea[id$="-description"]', 'Says whether a draft project can start, and why not.');
    button(form, 'Save').click();
    await settle(fixture);

    expect(requestBodies('PUT', 'admin/agents/starter/consultations/project').at(-1)).toEqual(jasmine.objectContaining({
      description: 'Says whether a draft project can start, and why not.',
      routes: ['asker-decides'],
      minimumRole: 'participant',
      proposalPolicy: 'asker-only',
      maxToolCalls: 4,
      expectedVersion: 1
    }));
    expect(text(view.querySelector('[data-saved]'))).toBe('The consultation was saved.');
    expect(text(row().querySelector('.card-header'))).toContain('Version 2');
    // Back to the action that opened the form.
    expect(view.querySelector('[data-form="consultation"]')).toBeNull();

    button(row(), 'Delete').click();
    await settle(fixture);
    expect(text(row())).toContain('Its tools disappear; proposals it made can still be decided.');
    button(row(), 'Confirm delete').click();
    await settle(fixture);

    expect(admin.removeDataConsultation).toHaveBeenCalledOnceWith('starter', 'project', { expectedVersion: 2 });
    expect(text(view.querySelector('[data-saved]'))).toBe('The consultation was removed.');
    expect(view.querySelector('[data-consultation="project"]')).toBeNull();
    expect(view.querySelector('[data-consultations] nh-assistant-agent-consultation-routes')).toBeNull();
  }, formFlowTimeout);

  it('chooses the agent\'s tools under Advanced and shows a tool the agent no longer has until it is cleared', async () => {
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const view = await openRunConfiguration(fixture, element, 'starter');
    const row = () => view.querySelector('[data-consultation="project"]') as HTMLElement;
    expect(row().querySelector('[data-consultation-tools]')!.getAttribute('data-tools')).toBe('all');
    expect(text(row().querySelector('[data-consultation-tools]'))).toContain('every tool of the agent');

    // The agent's own selectors as check boxes, with what each offers; none checked offers all.
    button(row(), 'Edit').click();
    await settle(fixture);
    const form = () => view.querySelector('[data-form="consultation"]') as HTMLElement;
    const tools = () => form().querySelector('[data-consultation-tools]') as HTMLElement;
    expect(text(form().querySelector('[data-consultation-advanced] summary'))).toBe('Advanced: tools, limits, requests and classification');
    expect(Array.from(tools().querySelectorAll('[data-tool-choice]')).map(choice => choice.getAttribute('data-tool-choice'))).toEqual(['projects.*', 'tasks.list']);
    expect(text(tools().querySelector('[data-tool-choice="projects.*"]'))).toContain('All tools that match: 1 now.');
    expect(Array.from(tools().querySelectorAll('input')).every(input => !(input as HTMLInputElement).checked)).toBeTrue();
    (tools().querySelector('[data-tool-choice="projects.*"] input') as HTMLInputElement).click();
    await settle(fixture);
    button(form(), 'Save').click();
    await settle(fixture);
    expect(requestBodies('PUT', 'admin/agents/starter/consultations/project').at(-1)).toEqual(jasmine.objectContaining({ toolSelectors: ['projects.*'] }));
    expect(row().querySelector('[data-consultation-tools]')!.getAttribute('data-tools')).toBe('selected');
    expect(text(row().querySelector('[data-consultation-tool-selector="projects.*"]'))).toBe('projects.*');

    // The agent no longer selects the project tools: the consultation offers none, with the problem.
    await changeStarterTools(['tasks.list']);
    button(element, 'Back to agents').click();
    await settle(fixture);
    const reopened = await openRunConfiguration(fixture, element, 'starter');
    const consultation = reopened.querySelector('[data-consultation="project"]') as HTMLElement;
    expect(consultation.querySelector('[data-consultation-tools]')!.getAttribute('data-tools')).toBe('none');
    expect(text(consultation.querySelector('[data-consultation-problem="tools-not-allowed"]')))
      .toContain("a chosen tool is no longer among the agent's own tools");

    // In the form the saved tool stays checked with why, so it can be cleared, and saving it is refused.
    button(consultation, 'Edit').click();
    await settle(fixture);
    const edit = reopened.querySelector('[data-form="consultation"]') as HTMLElement;
    const stale = edit.querySelector('[data-tool-choice="projects.*"] input') as HTMLInputElement;
    expect([stale.checked, stale.disabled]).toEqual([true, false]);
    expect(text(edit.querySelector(`#${CSS.escape(stale.getAttribute('aria-describedby')!)}`))).toBe("No longer among the agent's own tools.");
    const sent = requestBodies('PUT', 'admin/agents/starter/consultations/project').length;
    button(edit, 'Save').click();
    await settle(fixture);
    expect((edit.querySelector('[data-consultation-advanced]') as HTMLDetailsElement).open).toBeTrue();
    expect(text(edit.querySelector('[data-consultation-tools] .field-error')))
      .toBe("A chosen tool is outside the agent's own tools. Clear it, or add the tool to the agent first.");
    expect(requestBodies('PUT', 'admin/agents/starter/consultations/project').length).toBe(sent);
    stale.click();
    await settle(fixture);
    button(edit, 'Save').click();
    await settle(fixture);
    expect(requestBodies('PUT', 'admin/agents/starter/consultations/project').at(-1)).toEqual(jasmine.objectContaining({ toolSelectors: null }));
    expect(text(reopened.querySelector('[data-consultation="project"] [data-consultation-tools]'))).toContain('every tool of the agent');
  }, formFlowTimeout);

  it('says at the tools when the server refuses a tool outside the agent\'s own', async () => {
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const view = await openRunConfiguration(fixture, element, 'starter');
    spyOn(TestBed.inject(NhAssistantAdminApiService), 'saveDataConsultation').and.returnValue(throwError(() => new NhAssistantApiError(
      400, 'assistant-validation', 'nh-assistant.errors.assistant-validation', { toolSelectors: ['not-allowed'] })));
    button(view.querySelector('[data-consultation="project"]') as HTMLElement, 'Edit').click();
    await settle(fixture);
    const form = view.querySelector('[data-form="consultation"]') as HTMLElement;
    button(form, 'Save').click();
    await settle(fixture);

    const tools = form.querySelector('[data-consultation-tools]') as HTMLElement;
    expect((form.querySelector('[data-consultation-advanced]') as HTMLDetailsElement).open).toBeTrue();
    expect(text(tools.querySelector('.field-error'))).toBe("A chosen tool is outside the agent's own tools. Clear it, or add the tool to the agent first.");
    expect(tools.getAttribute('aria-describedby')).toContain(tools.querySelector('.field-error')!.id);
  }, formFlowTimeout);

  it('shows a refusal of the server at its field', async () => {
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const view = await openRunConfiguration(fixture, element, 'starter');
    spyOn(TestBed.inject(NhAssistantAdminApiService), 'saveDataConsultation').and.returnValue(throwError(() => new NhAssistantApiError(
      400, 'assistant-validation', 'nh-assistant.errors.assistant-validation', { routes: ['not-allowed'] })));
    button(view.querySelector('[data-consultation="project"]') as HTMLElement, 'Edit').click();
    await settle(fixture);
    const form = view.querySelector('[data-form="consultation"]') as HTMLElement;
    button(form, 'Save').click();
    await settle(fixture);

    const routes = form.querySelector('[data-consultation-routes]') as HTMLElement;
    expect(text(routes.querySelector('.field-error'))).toBe('The application does not allow this.');
    expect(routes.getAttribute('aria-describedby')).toContain(routes.querySelector('.field-error')!.id);
    expect(text(form.querySelector('[data-form-error]'))).toContain('Check the marked fields.');
  }, formFlowTimeout);
});

describe('Consultations in the flow view', () => {
  beforeEach(async () => configure());

  it('lists the consultations in the when step with their askers, problems and entry points', async () => {
    // The starter may propose with its account's rights, then answers for itself: that route no longer fits.
    await call('PUT', 'admin/agents/starter/consultations/project', {
      description: 'Says whether a draft project can start.',
      routes: ['asker-decides', 'subject-agent-rights'],
      expectedVersion: 1
    });
    const settings = (await configuration('starter')).runSettings!;
    await call('PUT', 'admin/agents/starter/run-settings', { accountability: 'agent', expectedVersion: settings.version });
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const view = await openRunConfiguration(fixture, element, 'starter');
    const when = () => view.querySelector('nh-assistant-agent-flow [data-flow-step="when"]') as HTMLElement;

    const consultation = when().querySelector('[data-flow-consultations] [data-flow-consultation="project"]') as HTMLElement;
    expect(text(when().querySelector('[data-flow-consultations] .flow-label'))).toBe('When another assistant consults it');
    expect(text(consultation)).toContain('Project');
    expect(text(consultation)).toContain('Answers and proposes');
    expect(text(consultation.querySelector('[data-flow-consultation-askers]'))).toBe('Asked by 1 agent');
    expect(when().querySelector('[data-flow-entry="add-consultation"]')).toBeNull();

    // The problem shows at the when step and its fix opens the consultation's form.
    expect(text(when().querySelector('[data-flow-attention]'))).toBe('1 to fix');
    const warning = when().querySelector('[data-flow-warning="problem:consultation:project:agent-has-no-rights"]') as HTMLElement;
    expect(text(warning)).toContain('Consultation about Project: the route with the agent\'s account is left out, because the agent answers for itself');
    expect(text(view.querySelector('[data-consultation="project"] [data-consultation-problem="agent-has-no-rights"]')))
      .toContain("the route with the agent's account is left out");
    button(warning, 'Change the consultation').click();
    await settle(fixture);
    const form = view.querySelector('[data-form="consultation"]') as HTMLElement;
    expect(text(form.querySelector('h5'))).toBe('Consultation about Project, version 2');
    // The stored route that no longer fits stays checked, so it can be cleared, with its reason.
    const agentRights = form.querySelector('[data-route-choice="subject-agent-rights"] input') as HTMLInputElement;
    expect([agentRights.checked, agentRights.disabled]).toEqual([true, false]);
    expect(text(form.querySelector('[data-route-block="agent-has-no-rights"]'))).toContain('Not for an agent that answers for itself');
  }, formFlowTimeout);

  it('offers to add a consultation from the when step of an agent that may get one', async () => {
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const view = await openRunConfiguration(fixture, element, 'helper');
    const when = view.querySelector('nh-assistant-agent-flow [data-flow-step="when"]') as HTMLElement;

    expect(text(when.querySelector('[data-flow-no-consultations]'))).toBe('No other assistant can consult it yet.');
    (when.querySelector('[data-flow-entry="add-consultation"]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(view.querySelector('[data-form="consultation"]')).not.toBeNull();
  }, formFlowTimeout);
});
