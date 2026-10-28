import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { AdminAgentInput } from '../models/assistant-admin.models';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAdminApiService } from '../services/nh-assistant-admin-api.service';
import { NhAssistantApiError } from '../services/nh-assistant-transport';
import { NhAssistantAdminAgentsComponent } from '../../../admin/src/public-api';
import { backToAdminList, openAdminAgent, openAdminTab } from './nh-assistant-admin-navigation';
import { eventOptions, worksOnCovers } from '../../../admin/src/agents/run-config/run-config-shared';

/**
 * What an agent works on: the agent editor asks it with the object types by name, the agent cards
 * show it, and the mock API checks it as the server does.
 */
function scenario(): NhAssistantMockScenario {
  return {
    agents: [{ id: 'projects', version: 1, displayNameKey: 'agents.projects', descriptionKey: 'agents.projects', canMutate: true }],
    turns: [],
    subjectTypes: [
      { id: 'project', names: { en: 'Project', nl: 'Project' }, pluralNames: { en: 'Projects', nl: 'Projecten' } },
      { id: 'task', names: { en: 'Task', nl: 'Taak' }, pluralNames: { en: 'Tasks', nl: 'Taken' } }
    ],
    admin: {
      tools: [{ id: 'sample-api.project.list', source: 'bridge', effect: 'read-only', description: 'List projects' }],
      agents: [
        {
          source: 'code', id: 'projects', displayName: 'Project assistant', description: 'Projects', instructions: 'Code instructions.',
          toolSelectors: ['sample-api.project.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'execute', isEnabled: true,
          knownSubjectTypes: ['project']
        },
        {
          source: 'code', id: 'intake-reviewer', displayName: 'Intake reviewer', description: 'Reviews', instructions: 'Review.',
          toolSelectors: [], mcpServerIds: [], requiredPolicy: null, autonomy: 'propose', isEnabled: true,
          worksOnSubjectTypes: ['project'], runPolicy: 'app.project.manage'
        }
      ],
      runCatalog: {
        triggerEvents: [
          { name: 'project.changed', description: 'A project changed.', subjectType: 'project' },
          { name: 'work.changed', description: 'Some work changed.' }
        ],
        agentPolicies: [{ name: 'app.project.manage', description: 'People who manage projects.' }]
      }
    }
  };
}

const newAgent: AdminAgentInput = {
  id: 'follow-up',
  displayName: 'Follow-up',
  description: 'Follows up tasks.',
  instructions: 'Follow up.',
  toolSelectors: [],
  mcpServerIds: [],
  requiredPolicy: null,
  autonomy: 'propose'
};

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
  for (let round = 0; round < 6; round++) {
    fixture.detectChanges();
    await flush();
  }
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

function button(element: HTMLElement, text: string): HTMLButtonElement {
  const match = Array.from(element.querySelectorAll('button')).find(item => item.textContent?.trim() === text);
  if (!match) {
    throw new Error(`Button "${text}" not found.`);
  }
  return match;
}

/** Opens Build of an agent's page, from the list or the page that is open, and returns the agent's profile. */
async function profileOf(fixture: ComponentFixture<unknown>, element: HTMLElement, agentId: string): Promise<HTMLElement> {
  const open = element.querySelector('[data-agent-page]');
  if (open?.getAttribute('data-agent-page') === agentId) {
    await openAdminTab(fixture, element, 'build');
  } else {
    if (open) {
      await backToAdminList(fixture, element);
    }
    await openAdminAgent(fixture, element, agentId, 'build');
  }
  return element.querySelector('[data-build-section="profile"]') as HTMLElement;
}

function type(element: HTMLElement, selector: string, value: string): void {
  const input = element.querySelector(selector) as HTMLInputElement | HTMLTextAreaElement;
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

function choose(element: HTMLElement, selector: string, value: string): void {
  const select = element.querySelector(selector) as HTMLSelectElement;
  select.value = value;
  select.dispatchEvent(new Event('change'));
}

function labelled(scope: HTMLElement, text: string): HTMLInputElement {
  const label = Array.from(scope.querySelectorAll('label')).find(item => item.textContent?.includes(text));
  if (!label) {
    throw new Error(`Choice "${text}" not found.`);
  }
  return label.querySelector('input') as HTMLInputElement;
}

function requestBodies(method: string, path: string): unknown[] {
  return TestBed.inject(NhAssistantMockBackend).requests
    .filter(item => item.method === method && item.path === path)
    .map(item => item.body);
}

async function failure(promise: Promise<unknown>): Promise<NhAssistantApiError> {
  return promise.then(() => { throw new Error('Expected a failure.'); }, error => error as NhAssistantApiError);
}

describe('What an agent works on in the administration', () => {
  it('shows what agents in code work on, all objects with the known types for one that does not say', async () => {
    await configure();
    const fixture = TestBed.createComponent(NhAssistantAdminAgentsComponent);
    const element = await settle(fixture);

    const undeclared = await profileOf(fixture, element, 'projects');
    expect(undeclared.querySelector('[data-works-on]')?.textContent).toContain('All objects');
    expect(undeclared.querySelector('[data-works-on]')?.textContent).toContain('(not stated in code)');
    expect(undeclared.querySelector('[data-known-types]')?.textContent).toContain('Known to work on: Projects.');

    const reviewer = await profileOf(fixture, element, 'intake-reviewer');
    expect(reviewer.querySelector('[data-works-on]')?.textContent).toContain('Projects');
    expect(reviewer.querySelector('[data-works-on]')?.textContent).not.toContain('All objects');
    expect(reviewer.querySelector('[data-known-types]')).toBeNull();
    const access = element.querySelector('[data-access-summary]') as HTMLElement;
    expect(access.textContent).toContain('Who may run it for the whole application');
    expect(access.textContent).toContain('app.project.manage');

    // An agent in code says what it works on in its code; its editor does not ask.
    (reviewer.querySelector('[data-edit-section="profile"]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(element.querySelector('[data-build-section="profile"] fieldset[data-works-on]')).toBeNull();
  });

  it('creates an agent that works on chosen kinds of objects and the whole application, and edits it', async () => {
    await configure();
    const fixture = TestBed.createComponent(NhAssistantAdminAgentsComponent);
    let element = await settle(fixture);

    button(element, 'New agent').click();
    element = await settle(fixture);
    type(element, 'input[id$="-id"]', 'follow-up');
    type(element, 'input[id$="-name"]', 'Follow-up');
    type(element, 'textarea', 'Follow up tasks.');
    const worksOn = element.querySelector('[data-works-on]') as HTMLElement;
    expect(worksOn.querySelector('legend')?.textContent).toContain('What does this agent work on?');
    expect(labelled(worksOn, 'All objects').checked).toBeTrue();
    // Per division needs a list of divisions; this application has none.
    expect(worksOn.querySelector('[data-per-division]')).toBeNull();

    labelled(worksOn, 'Chosen kinds of objects').click();
    element = await settle(fixture);
    const types = element.querySelector('[data-works-on-types]') as HTMLElement;
    expect(Array.from(types.querySelectorAll('label')).map(item => item.textContent?.trim())).toEqual(['Projects project', 'Tasks task']);

    // Without a kind of object or the whole application, the agent would work on nothing.
    button(element, 'Save').click();
    element = await settle(fixture);
    expect(element.textContent).toContain('Choose at least one kind of object, or the whole application.');
    expect(requestBodies('POST', 'admin/agents')).toEqual([]);

    labelled(types, 'Tasks').click();
    labelled(element.querySelector('[data-works-on]') as HTMLElement, 'The whole application').click();
    // Who may run it for the whole application is part of its access, on its page.
    expect(element.querySelector('select[id$="-run-policy"]')).toBeNull();
    fixture.detectChanges();
    button(element, 'Save').click();
    element = await settle(fixture);

    expect(requestBodies('POST', 'admin/agents')).toEqual([jasmine.objectContaining({
      id: 'follow-up',
      worksOnSubjectTypes: ['task'],
      worksOnAllSubjects: false,
      worksOnApplication: true,
      perDivision: false,
      runPolicy: null,
      viewPolicy: null
    })]);
    let created = await profileOf(fixture, element, 'follow-up');
    expect(created.querySelector('[data-works-on]')?.textContent).toContain('Tasks');
    expect(created.querySelector('[data-works-on]')?.textContent).toContain('and the whole application');

    // Its access says who may run it for the whole application.
    (element.querySelector('[data-edit-section="access"]') as HTMLButtonElement).click();
    element = await settle(fixture);
    choose(element, '[data-guardrails-access] select[id$="-run-policy"]', 'app.project.manage');
    fixture.detectChanges();
    button(element.querySelector('[data-guardrails-access]') as HTMLElement, 'Save').click();
    element = await settle(fixture);
    expect(requestBodies('PUT', 'admin/agents/follow-up')).toEqual([jasmine.objectContaining({
      worksOnSubjectTypes: ['task'],
      worksOnApplication: true,
      runPolicy: 'app.project.manage',
      expectedVersion: 1
    })]);

    // The editor starts from what the agent works on and sends it back as a whole.
    created = await profileOf(fixture, element, 'follow-up');
    (created.querySelector('[data-edit-section="profile"]') as HTMLButtonElement).click();
    element = await settle(fixture);
    expect(labelled(element.querySelector('fieldset[data-works-on]') as HTMLElement, 'Chosen kinds of objects').checked).toBeTrue();
    expect(labelled(element.querySelector('[data-works-on-types]') as HTMLElement, 'Tasks').checked).toBeTrue();
    labelled(element.querySelector('fieldset[data-works-on]') as HTMLElement, 'All objects').click();
    fixture.detectChanges();
    button(element.querySelector('[data-build-section="profile"]') as HTMLElement, 'Save').click();
    element = await settle(fixture);
    expect(requestBodies('PUT', 'admin/agents/follow-up')[1]).toEqual(jasmine.objectContaining({
      worksOnSubjectTypes: [],
      worksOnAllSubjects: true,
      worksOnApplication: true,
      runPolicy: 'app.project.manage',
      expectedVersion: 2
    }));
    expect((await profileOf(fixture, element, 'follow-up')).querySelector('[data-works-on]')?.textContent).toContain('All objects');
  });

  it('offers per division only when the application lists its divisions', async () => {
    const script = scenario();
    script.admin!.runCatalog!.divisions = true;
    await configure(script);
    const fixture = TestBed.createComponent(NhAssistantAdminAgentsComponent);
    let element = await settle(fixture);

    button(element, 'New agent').click();
    element = await settle(fixture);

    const perDivision = element.querySelector('[data-per-division]') as HTMLElement;
    expect(perDivision.textContent).toContain('Per division');
    expect(perDivision.querySelector('input')?.getAttribute('type')).toBe('checkbox');
  });

  it('offers a trigger event that names its kind of object only to agents that work on it', () => {
    const catalog = {
      triggerEvents: [
        { name: 'project.changed', description: 'A project changed.', subjectType: 'project' },
        { name: 'work.changed', description: 'Some work changed.', subjectType: null }
      ],
      subjectSets: [],
      ruleHandlers: [],
      approvalPolicies: [],
      profiles: []
    };

    expect(eventOptions(catalog, { worksOnSubjectTypes: ['task'], worksOnAllSubjects: false }).map(option => option.value)).toEqual(['work.changed']);
    expect(eventOptions(catalog, { worksOnSubjectTypes: [], worksOnAllSubjects: true }).map(option => option.value)).toEqual(['project.changed', 'work.changed']);
    expect(eventOptions(catalog, null).length).toBe(2);
    expect(worksOnCovers({ worksOnAllSubjects: true }, 'nh-system')).toBeFalse();
    expect(worksOnCovers({ worksOnAllSubjects: false, worksOnApplication: true }, 'nh-system')).toBeTrue();
  });
});

describe('provideNhAssistantMockApi what an agent works on', () => {
  let admin: NhAssistantAdminApiService;

  beforeEach(async () => {
    await configure();
    admin = TestBed.inject(NhAssistantAdminApiService);
  });

  it('checks what an agent works on as the server does and keeps it when an update omits it', async () => {
    const fields = async (input: Partial<AdminAgentInput>): Promise<Readonly<Record<string, readonly string[]>>> =>
      (await failure(firstValueFrom(admin.createAgent({ ...newAgent, ...input })))).fieldErrors;

    expect(await fields({ worksOnSubjectTypes: ['order'] })).toEqual({ worksOnSubjectTypes: ['not-found'] });
    expect(await fields({ worksOnSubjectTypes: ['nh-system'] })).toEqual({ worksOnSubjectTypes: ['not-found'] });
    expect(await fields({ worksOnSubjectTypes: ['task'], worksOnAllSubjects: true })).toEqual({ worksOn: ['invalid'] });
    expect(await fields({ worksOnSubjectTypes: [], worksOnAllSubjects: false, worksOnApplication: false })).toEqual({ worksOn: ['required'] });
    expect(await fields({ worksOnSubjectTypes: ['task'], perDivision: true })).toEqual({ perDivision: ['invalid'] });
    expect(await fields({ worksOnSubjectTypes: ['task'], viewPolicy: 'app.unlisted' })).toEqual({ viewPolicy: ['not-found'] });

    // Without the fields a new agent works on all object types.
    const plain = await firstValueFrom(admin.createAgent(newAgent));
    expect([plain.worksOnAllSubjects, plain.worksOnApplication, plain.worksOnDeclared]).toEqual([true, false, true]);

    const chosen = await firstValueFrom(admin.updateAgent(newAgent.id, {
      ...newAgent,
      worksOnSubjectTypes: [' task ', 'task'],
      worksOnApplication: true,
      runPolicy: 'app.project.manage',
      expectedVersion: plain.version
    }));
    expect(chosen.worksOnSubjectTypes).toEqual(['task']);
    expect([chosen.worksOnAllSubjects, chosen.worksOnApplication, chosen.runPolicy]).toEqual([false, true, 'app.project.manage']);

    const kept = await firstValueFrom(admin.updateAgent(newAgent.id, { ...newAgent, description: 'Changed.', expectedVersion: chosen.version }));
    expect(kept.worksOnSubjectTypes).toEqual(['task']);
    expect(kept.worksOnApplication).toBeTrue();
  });

  it('ignores a statement for an agent in code and refuses a trigger on a kind of object the agent does not work on', async () => {
    const [code] = await firstValueFrom(admin.getAgents());
    const updated = await firstValueFrom(admin.updateAgent(code.id, { ...code, worksOnSubjectTypes: ['order'], expectedVersion: code.version }));
    expect([updated.worksOnAllSubjects, updated.worksOnDeclared]).toEqual([true, false]);

    await firstValueFrom(admin.createAgent({ ...newAgent, worksOnSubjectTypes: ['task'] }));
    await firstValueFrom(admin.saveRunSettings(newAgent.id, { accountability: 'service-account', serviceAccountActorId: 'operations' }));
    const refused = await failure(firstValueFrom(admin.saveDataTrigger(newAgent.id, 'project-follow-up', { source: 'event', event: 'project.changed' })));
    expect(refused.fieldErrors).toEqual({ event: ['invalid'] });
    const accepted = await firstValueFrom(admin.saveDataTrigger(newAgent.id, 'work-follow-up', { source: 'event', event: 'work.changed' }));
    expect(accepted.event).toBe('work.changed');
  });
});
