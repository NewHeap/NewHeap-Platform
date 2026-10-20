import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { AdminAgentInput } from '../models/assistant-admin.models';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAdminApiService } from '../services/nh-assistant-admin-api.service';
import { NhAssistantApiError } from '../services/nh-assistant-transport';
import { NhAssistantAdminAgentsComponent } from '../../../admin/src/public-api';

/**
 * The whole application as a scope: a schedule of an agent that works on the whole application runs
 * for it instead of a set of objects, once per division for an agent that works per division; the
 * forms offer it only to such agents, the run configuration names it, the agent editor sends per
 * division, and the mock API checks it as the server does.
 */
function scenario(divisions = false): NhAssistantMockScenario {
  return {
    agents: [{ id: 'projects', version: 1, displayNameKey: 'agents.projects', descriptionKey: 'agents.projects', canMutate: true }],
    turns: [],
    subjectTypes: [{ id: 'project', names: { en: 'Project', nl: 'Project' }, pluralNames: { en: 'Projects', nl: 'Projecten' } }],
    admin: {
      agents: [{
        source: 'code', id: 'projects', displayName: 'Project assistant', description: 'Projects', instructions: 'Code instructions.',
        toolSelectors: [], mcpServerIds: [], requiredPolicy: null, autonomy: 'execute', isEnabled: true,
        // Its threads on the whole application are no kind of object.
        knownSubjectTypes: ['nh-system', 'project']
      }],
      runCatalog: {
        triggerEvents: [{ name: 'project.created', description: 'A project was created.', subjectType: 'project' }],
        subjectSets: [{ name: 'active-projects', description: 'Active projects.' }],
        approvalPolicies: ['project-review'],
        divisions
      }
    }
  };
}

/** An agent created in the administration that counts the open projects for the whole application. */
const statistics: AdminAgentInput = {
  id: 'statistics',
  displayName: 'Project statistics',
  description: 'Counts the open projects.',
  instructions: 'Count the open projects.',
  toolSelectors: [],
  mcpServerIds: [],
  requiredPolicy: null,
  autonomy: 'propose',
  offeredInChat: false,
  worksOnSubjectTypes: [],
  worksOnAllSubjects: false,
  worksOnApplication: true
};

/** The same agent narrowed to projects: it no longer works on the whole application. */
const onProjects: Partial<AdminAgentInput> = { worksOnSubjectTypes: ['project'], worksOnAllSubjects: false, worksOnApplication: false };

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

function card(element: HTMLElement, name: string): HTMLElement {
  const match = Array.from(element.querySelectorAll<HTMLElement>('.card')).find(item => item.textContent?.includes(name));
  if (!match) {
    throw new Error(`Card "${name}" not found.`);
  }
  return match;
}

function type(element: HTMLElement, selector: string, value: string): void {
  const input = element.querySelector(selector) as HTMLInputElement | HTMLTextAreaElement;
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

function labelled(scope: HTMLElement, text: string): HTMLInputElement {
  const label = Array.from(scope.querySelectorAll('label')).find(item => item.textContent?.includes(text));
  if (!label) {
    throw new Error(`Choice "${text}" not found.`);
  }
  return label.querySelector('input') as HTMLInputElement;
}

/** Chooses an option of a picker: opens it, types the query and clicks the option. */
async function pick(fixture: ComponentFixture<unknown>, scope: HTMLElement, selector: string, query: string, label: string): Promise<void> {
  const input = scope.querySelector(selector) as HTMLInputElement;
  input.click();
  input.value = query;
  input.dispatchEvent(new Event('input'));
  await settle(fixture);
  const option = Array.from(scope.querySelectorAll<HTMLElement>('[role="option"]'))
    .find(item => item.querySelector('.label')?.textContent?.trim() === label);
  if (!option) {
    throw new Error(`Option "${label}" not found.`);
  }
  option.click();
  await settle(fixture);
}

function requestBodies(method: string, path: string): unknown[] {
  return TestBed.inject(NhAssistantMockBackend).requests
    .filter(item => item.method === method && item.path === path)
    .map(item => item.body);
}

async function failure(promise: Promise<unknown>): Promise<NhAssistantApiError> {
  return promise.then(() => { throw new Error('Expected a failure.'); }, error => error as NhAssistantApiError);
}

/** Creates an agent in the administration, with run settings unless it should start with the guided setup. */
async function createAgent(input: AdminAgentInput, withSettings = true): Promise<void> {
  const admin = TestBed.inject(NhAssistantAdminApiService);
  await firstValueFrom(admin.createAgent(input));
  if (withSettings) {
    await firstValueFrom(admin.saveRunSettings(input.id, { accountability: 'service-account', serviceAccountActorId: 'operations' }));
  }
}

/** Narrows the agent to projects, as another administrator might while a page is open. */
async function narrowToProjects(input: AdminAgentInput): Promise<void> {
  const admin = TestBed.inject(NhAssistantAdminApiService);
  const current = (await firstValueFrom(admin.getAgents())).find(agent => agent.id === input.id)!;
  await firstValueFrom(admin.updateAgent(input.id, { ...input, ...onProjects, expectedVersion: current.version }));
}

async function openRunConfiguration(agentId: string): Promise<{ fixture: ComponentFixture<NhAssistantAdminAgentsComponent>; view: () => HTMLElement }> {
  const fixture = TestBed.createComponent(NhAssistantAdminAgentsComponent);
  const element = await settle(fixture);
  (element.querySelector(`[data-run-config-agent="${agentId}"]`) as HTMLButtonElement).click();
  await settle(fixture);
  return { fixture, view: () => element.querySelector('nh-assistant-agent-run-configuration') as HTMLElement };
}

/** Opens a new trigger and fills in a weekly schedule; what it runs for is left as offered. */
async function newSchedule(fixture: ComponentFixture<unknown>, view: () => HTMLElement, triggerId: string): Promise<HTMLElement> {
  button(view(), 'Add trigger').click();
  await settle(fixture);
  const form = view().querySelector('[data-form="trigger"]') as HTMLElement;
  type(form, 'input[id$="-trigger-id"]', triggerId);
  labelled(form, 'A schedule').click();
  await settle(fixture);
  type(form, 'input[id$="-trigger-cron"]', '0 9 * * 1');
  type(form, 'textarea[id$="-trigger-message"]', 'Count the open projects.');
  await settle(fixture);
  return form;
}

describe('A schedule for the whole application in the run configuration', () => {
  it('offers the whole application to an agent that works on it and sends the schedule without a subject set', async () => {
    await configure();
    await createAgent(statistics);
    const { fixture, view } = await openRunConfiguration('statistics');
    // A phone-sized screen.
    (fixture.nativeElement as HTMLElement).style.width = '375px';

    const form = await newSchedule(fixture, view, 'weekly-count');
    expect(form.textContent).toContain('At fixed times, for the whole application or for every object in a set.');
    const target = form.querySelector('[data-schedule-target]') as HTMLElement;
    const group = target.querySelector('[role="radiogroup"]') as HTMLElement;
    expect(document.getElementById(group.getAttribute('aria-labelledby')!)?.textContent).toContain('What does it run for each time?');
    expect(labelled(target, 'The whole application').checked).toBeTrue();
    expect(target.querySelector('[data-target-hint]')?.textContent).toContain('One run each time, about the application as a whole.');
    // No subject set is asked while the schedule runs for the whole application.
    expect(form.querySelector('input[id$="-trigger-set"]')).toBeNull();
    // The choices stack within the form at 375px instead of overflowing it.
    expect(group.scrollWidth).toBeLessThanOrEqual(group.clientWidth + 1);
    expect(group.getBoundingClientRect().right).toBeLessThanOrEqual(form.getBoundingClientRect().right);
    const [first, second] = Array.from(group.querySelectorAll<HTMLElement>('.choice')).map(choice => choice.getBoundingClientRect());
    expect(second.top).toBeGreaterThanOrEqual(first.bottom);

    button(form, 'Save').click();
    await settle(fixture);
    expect(requestBodies('PUT', 'admin/agents/statistics/triggers/weekly-count')).toEqual([jasmine.objectContaining({
      source: 'schedule', cron: '0 9 * * 1', subjectSet: null, message: 'Count the open projects.', expectedVersion: null
    })]);
    const listed = view().querySelector('[data-data-trigger="weekly-count"]') as HTMLElement;
    expect(listed.querySelector('[data-trigger-target]')?.textContent?.trim()).toBe('The whole application');

    // A stored schedule for the whole application opens with that choice; a set of objects is required again.
    button(listed, 'Edit').click();
    await settle(fixture);
    const edit = view().querySelector('[data-form="trigger"]') as HTMLElement;
    expect(labelled(edit.querySelector('[data-schedule-target]') as HTMLElement, 'The whole application').checked).toBeTrue();
    labelled(edit, 'Every object in a set').click();
    await settle(fixture);
    button(edit, 'Save').click();
    await settle(fixture);
    const set = edit.querySelector('input[id$="-trigger-set"]') as HTMLInputElement;
    expect(set.getAttribute('aria-invalid')).toBe('true');
    expect(edit.textContent).toContain('Required.');
    expect(requestBodies('PUT', 'admin/agents/statistics/triggers/weekly-count').length).toBe(1);

    await pick(fixture, edit, 'input[id$="-trigger-set"]', 'active', 'active-projects');
    button(edit, 'Save').click();
    await settle(fixture);
    expect(requestBodies('PUT', 'admin/agents/statistics/triggers/weekly-count').at(-1)).toEqual(jasmine.objectContaining({
      subjectSet: 'active-projects', expectedVersion: 1
    }));
    expect(view().querySelector('[data-data-trigger="weekly-count"]')?.textContent).toContain('active-projects');
  });

  it('keeps a subject set required for an agent that does not work on the whole application', async () => {
    await configure();
    await createAgent({ ...statistics, ...onProjects });
    const { fixture, view } = await openRunConfiguration('statistics');

    const form = await newSchedule(fixture, view, 'weekly-count');
    expect(form.querySelector('[data-schedule-target]')).toBeNull();
    expect(form.textContent).toContain('At fixed times, for every subject in a set.');
    button(form, 'Save').click();
    await settle(fixture);

    expect(requestBodies('PUT', 'admin/agents/statistics/triggers/weekly-count')).toEqual([]);
    expect(form.querySelector('input[id$="-trigger-set"]')?.getAttribute('aria-invalid')).toBe('true');
    expect(document.activeElement).toBe(form.querySelector('input[id$="-trigger-set"]'));
  });

  it('shows the refusal of the whole application at the choice when the agent stopped working on it meanwhile', async () => {
    await configure();
    await createAgent(statistics);
    const { fixture, view } = await openRunConfiguration('statistics');
    await narrowToProjects(statistics);

    const form = await newSchedule(fixture, view, 'weekly-count');
    button(form, 'Save').click();
    await settle(fixture);

    const group = form.querySelector('[data-schedule-target] [role="radiogroup"]') as HTMLElement;
    expect(group.getAttribute('aria-invalid')).toBe('true');
    const error = document.getElementById(group.getAttribute('aria-describedby')!)!;
    expect(error.textContent).toContain('The agent does not work on the whole application. Choose a set of objects');
    expect(form.querySelector('[data-form-error]')?.textContent).toContain('Check the marked fields.');
    expect(document.activeElement).toBe(group);

    // Choosing a set of objects clears the refusal and saves.
    labelled(form, 'Every object in a set').click();
    await settle(fixture);
    expect(form.querySelector('[data-schedule-target] [role="radiogroup"]')?.getAttribute('aria-invalid')).toBeNull();
    await pick(fixture, form, 'input[id$="-trigger-set"]', 'active', 'active-projects');
    button(form, 'Save').click();
    await settle(fixture);
    expect(requestBodies('PUT', 'admin/agents/statistics/triggers/weekly-count').at(-1)).toEqual(jasmine.objectContaining({ subjectSet: 'active-projects' }));
    expect(view().querySelector('[data-data-trigger="weekly-count"]')).not.toBeNull();
  });

  it('explains that a schedule of a per-division agent runs once per division and lists it for every division', async () => {
    await configure(scenario(true));
    await createAgent({ ...statistics, perDivision: true });
    const { fixture, view } = await openRunConfiguration('statistics');

    const form = await newSchedule(fixture, view, 'weekly-count');
    expect(form.querySelector('[data-target-hint]')?.textContent).toContain('One run each time for every division, each in its own thread.');
    button(form, 'Save').click();
    await settle(fixture);

    expect(requestBodies('PUT', 'admin/agents/statistics/triggers/weekly-count')).toEqual([jasmine.objectContaining({ subjectSet: null })]);
    expect(view().querySelector('[data-data-trigger="weekly-count"] [data-trigger-target]')?.textContent?.trim()).toBe('Every division');
  });

  it('keeps a schedule for the whole application out of effect once the agent no longer works on it', async () => {
    await configure();
    await createAgent(statistics);
    const admin = TestBed.inject(NhAssistantAdminApiService);
    await firstValueFrom(admin.saveDataTrigger('statistics', 'weekly-count', {
      source: 'schedule', cron: '0 9 * * 1', message: 'Count the open projects.'
    }));
    await narrowToProjects(statistics);
    const { fixture, view } = await openRunConfiguration('statistics');

    expect(view().querySelector('[data-problems]')?.textContent?.replace(/\s+/g, ' '))
      .toContain('Trigger weekly-count: it concerns a kind of object, or the whole application, that the agent does not work on.');
    const listed = view().querySelector('[data-data-trigger="weekly-count"]') as HTMLElement;
    expect(listed.textContent).toContain('Out of effect');
    expect(listed.querySelector('[data-trigger-target]')?.textContent?.trim()).toBe('The whole application');

    // Its form asks for a set of objects and says why.
    button(listed, 'Edit').click();
    await settle(fixture);
    const form = view().querySelector('[data-form="trigger"]') as HTMLElement;
    expect(form.querySelector('[data-schedule-target]')).toBeNull();
    expect(form.textContent).toContain('This schedule ran for the whole application, which the agent no longer works on.');
    expect(form.querySelector('input[id$="-trigger-set"]')).not.toBeNull();
  });
});

describe('A schedule for the whole application in the guided setup', () => {
  it('runs a per-division agent on a schedule for every division and says so in the review', async () => {
    await configure(scenario(true));
    await createAgent({ ...statistics, perDivision: true }, false);
    const { fixture, view } = await openRunConfiguration('statistics');
    button(view(), 'Let this agent work on its own').click();
    await settle(fixture);
    const setup = () => view().querySelector('[data-work-setup]') as HTMLElement;

    labelled(setup(), 'A schedule').click();
    await settle(fixture);
    const target = setup().querySelector('[data-schedule-target]') as HTMLElement;
    expect(labelled(target, 'The whole application').checked).toBeTrue();
    expect(target.querySelector('[data-target-hint]')?.textContent).toContain('One run each time for every division');
    expect(setup().querySelector('input[id$="-set"]')).toBeNull();
    button(setup(), 'Mondays at 9:00').click();
    type(setup(), 'textarea[id$="-message"]', 'Count the open projects.');
    button(setup(), 'Next').click();
    await settle(fixture);

    type(setup(), 'input[id$="-name"]', 'Count open projects');
    type(setup(), 'input[id$="-description"]', 'Counts the open projects.');
    type(setup(), 'textarea[id$="-instructions"]', 'Count the open projects and propose completions.');
    button(setup(), 'Next').click();
    await settle(fixture);
    type(setup(), 'input[id$="-account"]', 'operations');
    button(setup(), 'Next').click();
    await settle(fixture);
    button(setup(), 'Next').click();
    await settle(fixture);

    expect(setup().querySelector('[data-review-target]')?.textContent?.trim())
      .toBe('On the schedule 0 9 * * 1 (UTC), for the whole application in every division');
    button(setup(), 'Save in shadow').click();
    await settle(fixture);

    expect(requestBodies('PUT', 'admin/agents/statistics/triggers/count-open-projects-schedule')).toEqual([jasmine.objectContaining({
      source: 'schedule', cron: '0 9 * * 1', subjectSet: null, skill: 'count-open-projects'
    })]);
    expect(view().querySelector('[data-data-trigger="count-open-projects-schedule"] [data-trigger-target]')?.textContent?.trim()).toBe('Every division');
  });

  it('names the whole application in the review of an app-wide agent', async () => {
    await configure();
    await createAgent(statistics, false);
    const { fixture, view } = await openRunConfiguration('statistics');
    button(view(), 'Let this agent work on its own').click();
    await settle(fixture);
    const setup = () => view().querySelector('[data-work-setup]') as HTMLElement;

    labelled(setup(), 'A schedule').click();
    await settle(fixture);
    expect(setup().querySelector('[data-target-hint]')?.textContent).toContain('One run each time, about the application as a whole.');
    button(setup(), 'Mondays at 9:00').click();
    type(setup(), 'textarea[id$="-message"]', 'Count the open projects.');
    button(setup(), 'Next').click();
    await settle(fixture);
    type(setup(), 'input[id$="-name"]', 'Count open projects');
    type(setup(), 'input[id$="-description"]', 'Counts the open projects.');
    type(setup(), 'textarea[id$="-instructions"]', 'Count the open projects.');
    button(setup(), 'Next').click();
    await settle(fixture);
    type(setup(), 'input[id$="-account"]', 'operations');
    button(setup(), 'Next').click();
    await settle(fixture);
    button(setup(), 'Next').click();
    await settle(fixture);

    expect(setup().querySelector('[data-review-target]')?.textContent?.trim()).toBe('On the schedule 0 9 * * 1 (UTC), for the whole application');
  });

  it('asks an agent that does not work on the whole application for a set of objects', async () => {
    await configure();
    await createAgent({ ...statistics, ...onProjects }, false);
    const { fixture, view } = await openRunConfiguration('statistics');
    button(view(), 'Let this agent work on its own').click();
    await settle(fixture);
    const setup = () => view().querySelector('[data-work-setup]') as HTMLElement;

    labelled(setup(), 'A schedule').click();
    await settle(fixture);
    expect(setup().querySelector('[data-schedule-target]')).toBeNull();
    button(setup(), 'Mondays at 9:00').click();
    type(setup(), 'textarea[id$="-message"]', 'Count the open projects.');
    button(setup(), 'Next').click();
    await settle(fixture);

    expect(setup().querySelector('[aria-current="step"]')?.textContent).toContain('When');
    expect(setup().querySelector('input[id$="-set"]')?.getAttribute('aria-invalid')).toBe('true');
  });
});

describe('Per division and the whole application in the agent editor', () => {
  it('sends per division when the application lists its divisions and shows it on the card', async () => {
    await configure(scenario(true));
    const fixture = TestBed.createComponent(NhAssistantAdminAgentsComponent);
    let element = await settle(fixture);

    button(element, 'New agent').click();
    element = await settle(fixture);
    type(element, 'input[id$="-id"]', 'statistics');
    type(element, 'input[id$="-name"]', 'Project statistics');
    type(element, 'textarea', 'Count the open projects.');
    const worksOn = element.querySelector('[data-works-on]') as HTMLElement;
    labelled(worksOn, 'The whole application').click();
    const perDivision = element.querySelector('[data-per-division]') as HTMLElement;
    expect(perDivision.textContent).toContain('Its work for the whole application, such as its schedules, runs separately for every division.');
    (perDivision.querySelector('input') as HTMLInputElement).click();
    fixture.detectChanges();
    button(element, 'Save').click();
    element = await settle(fixture);

    expect(requestBodies('POST', 'admin/agents')).toEqual([jasmine.objectContaining({
      id: 'statistics', worksOnAllSubjects: true, worksOnApplication: true, perDivision: true
    })]);
    const created = card(element, 'Project statistics');
    expect(created.querySelector('[data-works-on]')?.textContent).toContain('and the whole application');
    expect(created.querySelector('[data-works-on]')?.textContent).toContain('per division');

    // The editor starts from the stored choice and sends a change back.
    button(created, 'Edit').click();
    element = await settle(fixture);
    const edited = element.querySelector('[data-per-division] input') as HTMLInputElement;
    expect(edited.checked).toBeTrue();
    edited.click();
    fixture.detectChanges();
    button(element, 'Save').click();
    element = await settle(fixture);
    expect(requestBodies('PUT', 'admin/agents/statistics')).toEqual([jasmine.objectContaining({ perDivision: false, expectedVersion: 1 })]);
    expect(card(element, 'Project statistics').querySelector('[data-works-on]')?.textContent).not.toContain('per division');
  });

  it('does not offer or send per division when the application lists no divisions', async () => {
    await configure(scenario(false));
    const fixture = TestBed.createComponent(NhAssistantAdminAgentsComponent);
    let element = await settle(fixture);

    button(element, 'New agent').click();
    element = await settle(fixture);
    type(element, 'input[id$="-id"]', 'statistics');
    type(element, 'input[id$="-name"]', 'Project statistics');
    type(element, 'textarea', 'Count the open projects.');
    labelled(element.querySelector('[data-works-on]') as HTMLElement, 'The whole application').click();
    expect(element.querySelector('[data-per-division]')).toBeNull();
    fixture.detectChanges();
    button(element, 'Save').click();
    element = await settle(fixture);

    expect(requestBodies('POST', 'admin/agents')).toEqual([jasmine.objectContaining({ worksOnApplication: true, perDivision: false })]);
    expect(card(element, 'Project statistics').querySelector('[data-works-on]')?.textContent).not.toContain('per division');
  });

  it('never offers the whole application as a kind of object, nor counts it as a known kind', async () => {
    await configure();
    const fixture = TestBed.createComponent(NhAssistantAdminAgentsComponent);
    let element = await settle(fixture);
    expect(card(element, 'Project assistant').querySelector('[data-known-types]')?.textContent?.trim()).toBe('Known to work on: Projects.');

    button(element, 'New agent').click();
    element = await settle(fixture);
    labelled(element.querySelector('[data-works-on]') as HTMLElement, 'Chosen kinds of objects').click();
    element = await settle(fixture);
    const types = element.querySelector('[data-works-on-types]') as HTMLElement;
    expect(Array.from(types.querySelectorAll('label')).map(item => item.textContent?.trim())).toEqual(['Projects project']);
  });
});

describe('provideNhAssistantMockApi the whole application', () => {
  let admin: NhAssistantAdminApiService;

  async function start(script = scenario()): Promise<void> {
    await configure(script);
    admin = TestBed.inject(NhAssistantAdminApiService);
  }

  async function get(path: string): Promise<{ status: number; body: any }> {
    const response = await TestBed.inject(NhAssistantMockBackend).fetch(`/api/assistant/${path}`, { method: 'GET' });
    const text = await response.text();
    return { status: response.status, body: text ? JSON.parse(text) : null };
  }

  it('lists the whole application in the run catalog in the request language and reports divisions from the scenario', async () => {
    await start();
    const catalog = await firstValueFrom(admin.getRunCatalog());
    expect(catalog.subjectTypes!.map(item => item.id)).toEqual(['nh-system', 'project']);
    expect(catalog.subjectTypes!.find(item => item.id === 'nh-system')).toEqual({
      id: 'nh-system',
      name: 'The whole application',
      pluralName: null,
      entityType: null,
      entityTypeFull: null,
      link: null,
      searchable: false,
      isSystem: true
    });
    expect(catalog.divisions).toBeFalse();

    await firstValueFrom(TestBed.inject(TranslateService).use('nl'));
    expect((await firstValueFrom(admin.getRunCatalog())).subjectTypes!.find(item => item.id === 'nh-system')?.name).toBe('De hele applicatie');
  });

  it('names the whole application as a subject and does not search it', async () => {
    await start();
    const work = await get('subjects/nh-system/global/work');
    expect(work.status).toBe(200);
    expect(work.body).toEqual(jasmine.objectContaining({
      subjectType: 'nh-system',
      subjectId: 'global',
      subjectDisplayName: 'The whole application',
      subjectTypeName: 'The whole application',
      subjectLink: null
    }));
    expect((await get('subjects/nh-system/division-7f0c/work')).body.code).toBe('assistant-subject-not-found');
    expect((await get('subjects/nh-system/search?query=whole')).body.code).toBe('assistant-subject-search-unavailable');
  });

  it('accepts a schedule without a subject set only for an agent that works on the whole application', async () => {
    await start();
    await createAgent({ ...statistics, ...onProjects });
    const refused = await failure(firstValueFrom(admin.saveDataTrigger('statistics', 'weekly-count', {
      source: 'schedule', cron: '0 9 * * 1', message: 'Count.'
    })));
    expect(refused.code).toBe('assistant-validation');
    expect(refused.fieldErrors).toEqual({ subjectSet: ['required'] });
    // A set of objects still has to be one the application registers.
    const unknown = await failure(firstValueFrom(admin.saveDataTrigger('statistics', 'weekly-count', {
      source: 'schedule', cron: '0 9 * * 1', subjectSet: 'closed-projects', message: 'Count.'
    })));
    expect(unknown.fieldErrors).toEqual({ subjectSet: ['not-found'] });

    await createAgent({ ...statistics, id: 'overview' });
    const accepted = await firstValueFrom(admin.saveDataTrigger('overview', 'weekly-count', {
      source: 'schedule', cron: '0 9 * * 1', subjectSet: null, message: 'Count.'
    }));
    expect([accepted.subjectSet, accepted.inEffect, accepted.problem]).toEqual([null, true, null]);
  });

  it('keeps a stored schedule for the whole application out of effect after the agent stopped working on it', async () => {
    await start();
    await createAgent(statistics);
    await firstValueFrom(admin.saveDataTrigger('statistics', 'weekly-count', { source: 'schedule', cron: '0 9 * * 1', message: 'Count.' }));
    expect((await firstValueFrom(admin.getAgentConfiguration('statistics'))).problems).toEqual([]);

    await narrowToProjects(statistics);
    const configuration = await firstValueFrom(admin.getAgentConfiguration('statistics'));
    expect(configuration.dataTriggers.map(trigger => [trigger.triggerId, trigger.inEffect, trigger.problem]))
      .toEqual([['weekly-count', false, 'not-worked-on']]);
    expect(configuration.problems).toEqual([{ agentId: 'statistics', kind: 'trigger', id: 'weekly-count', code: 'not-worked-on' }]);
  });

  it('lets an agent work per division only when the scenario lists divisions', async () => {
    await start(scenario(true));
    expect((await firstValueFrom(admin.getRunCatalog())).divisions).toBeTrue();
    const created = await firstValueFrom(admin.createAgent({ ...statistics, perDivision: true }));
    expect([created.perDivision, created.worksOnApplication]).toEqual([true, true]);

    TestBed.resetTestingModule();
    await start(scenario(false));
    const refused = await failure(firstValueFrom(admin.createAgent({ ...statistics, perDivision: true })));
    expect(refused.fieldErrors).toEqual({ perDivision: ['invalid'] });
  });
});
