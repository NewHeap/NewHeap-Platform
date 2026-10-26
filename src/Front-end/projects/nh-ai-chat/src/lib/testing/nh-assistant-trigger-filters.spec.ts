import { Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { DataTrigger, RunCatalog } from '../models/assistant-admin.models';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAdminApiService } from '../services/nh-assistant-admin-api.service';
import { NhAssistantApiError } from '../services/nh-assistant-transport';
import { NhAssistantAdminAgentsComponent } from '../../../admin/src/public-api';
import { NhAssistantTriggerFormComponent } from '../../../admin/src/agents/run-config/nh-assistant-trigger-form.component';
import { startNhAssistantAdminTranslations } from '../../../admin/src/i18n/nh-assistant-admin-translations';

/** Each searchable picker waits for its search, so a form flow takes several seconds. */
const formFlowTimeout = 20_000;

/** An agent in code and two events of the application, one with named filters. */
function filterScenario(): NhAssistantMockScenario {
  return {
    agents: [{ id: 'projects', version: 1, displayNameKey: 'agents.projects', descriptionKey: 'agents.projects', canMutate: true }],
    turns: [],
    admin: {
      context: 'Initial context.',
      tools: [{ id: 'sample-api.project.update-status', source: 'bridge', effect: 'mutation', description: 'Update status' }],
      agents: [{
        source: 'code', id: 'projects', displayName: 'Project assistant', description: 'Projects', instructions: 'Code instructions.',
        toolSelectors: ['sample-api.project.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'execute', isEnabled: true
      }],
      mcpServers: [],
      runCatalog: {
        triggerEvents: [
          {
            name: 'project.status-changed',
            description: 'The status of a project changed.',
            filters: [
              { name: 'not-own-change', description: 'Skips status changes the agent made itself.' },
              { name: 'not-bulk', description: 'Skips bulk imports.' }
            ]
          },
          { name: 'project.created', description: 'A project was created.' }
        ],
        subjectSets: [],
        ruleHandlers: [],
        approvalPolicies: [],
        profiles: []
      }
    }
  };
}

function flush(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve));
}

async function configure(): Promise<void> {
  TestBed.configureTestingModule({
    providers: [
      provideTranslateService({ fallbackLang: 'en' }),
      provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
      provideNhAssistantMockApi(filterScenario())
    ]
  });
  await firstValueFrom(TestBed.inject(TranslateService).use('en'));
  // The exported admin component that hosts this part starts the administration's texts; the
  // spec renders the part on its own.
  TestBed.runInInjectionContext(() => startNhAssistantAdminTranslations());
}

async function settle(fixture: ComponentFixture<unknown>): Promise<HTMLElement> {
  for (let round = 0; round < 6; round++) {
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

function button(element: HTMLElement, text: string): HTMLButtonElement {
  const match = Array.from(element.querySelectorAll('button')).find(item => item.textContent?.trim() === text);
  if (!match) {
    throw new Error(`Button "${text}" not found.`);
  }
  return match;
}

function type(element: HTMLElement, selector: string, value: string): void {
  const input = element.querySelector(selector) as HTMLInputElement;
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

/** Chooses an option of a searchable picker: opens it, types the query and clicks the option. */
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

function requestBodies(method: string, path: string): unknown[] {
  return TestBed.inject(NhAssistantMockBackend).requests
    .filter(item => item.method === method && item.path === path)
    .map(item => item.body);
}

describe('Trigger event filters in the administration', () => {
  it('offers the filters of the chosen event with what they skip and saves those selected in the event\'s order', async () => {
    await configure();
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    (element.querySelector('[data-run-config-agent="projects"]') as HTMLButtonElement).click();
    await settle(fixture);
    const view = () => element.querySelector('nh-assistant-agent-run-configuration') as HTMLElement;
    button(view(), 'Add trigger').click();
    await settle(fixture);
    const form = () => view().querySelector('[data-form="trigger"]') as HTMLElement;
    type(form(), 'input[id$="-trigger-id"]', 'status-check');

    // Without an event there is nothing to filter yet.
    expect(form().querySelector('[data-trigger-filters]')).toBeNull();

    // An event without filters says so.
    await pick(fixture, form(), 'input[id$="-trigger-event"]', 'created', 'project.created');
    expect(form().querySelector('[data-filters-empty]')?.textContent).toContain('This event offers no filters.');

    // The status event offers its two filters, each with what it skips.
    await pick(fixture, form(), 'input[id$="-trigger-event"]', 'status', 'project.status-changed');
    expect(form().querySelector('[data-filters-empty]')).toBeNull();
    const ownChange = form().querySelector('[data-filter="not-own-change"]') as HTMLElement;
    expect(ownChange.textContent).toContain('Skips status changes the agent made itself.');
    expect(form().querySelector('[data-filter="not-bulk"]')?.textContent).toContain('Skips bulk imports.');
    (form().querySelector('[data-filter="not-bulk"] input') as HTMLInputElement).click();
    (ownChange.querySelector('input') as HTMLInputElement).click();
    await settle(fixture);
    button(form(), 'Save').click();
    await settle(fixture);

    expect(requestBodies('PUT', 'admin/agents/projects/triggers/status-check')).toEqual([jasmine.objectContaining({
      source: 'event', event: 'project.status-changed', filters: ['not-own-change', 'not-bulk'], expectedVersion: null
    })]);
    const row = view().querySelector('[data-data-trigger="status-check"]') as HTMLElement;
    expect(row.querySelector('[data-trigger-filters]')?.textContent).toContain('Filters: not-own-change, not-bulk');
  }, formFlowTimeout);

  it('drops filters the next chosen event does not offer and sends none for a schedule', async () => {
    await configure();
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    (element.querySelector('[data-run-config-agent="projects"]') as HTMLButtonElement).click();
    await settle(fixture);
    const view = () => element.querySelector('nh-assistant-agent-run-configuration') as HTMLElement;
    button(view(), 'Add trigger').click();
    await settle(fixture);
    const form = () => view().querySelector('[data-form="trigger"]') as HTMLElement;
    type(form(), 'input[id$="-trigger-id"]', 'created-check');

    await pick(fixture, form(), 'input[id$="-trigger-event"]', 'status', 'project.status-changed');
    (form().querySelector('[data-filter="not-own-change"] input') as HTMLInputElement).click();
    await settle(fixture);
    await pick(fixture, form(), 'input[id$="-trigger-event"]', 'created', 'project.created');
    button(form(), 'Save').click();
    await settle(fixture);

    expect(requestBodies('PUT', 'admin/agents/projects/triggers/created-check')).toEqual([jasmine.objectContaining({
      event: 'project.created', filters: []
    })]);
    expect(view().querySelector('[data-data-trigger="created-check"] [data-trigger-filters]')).toBeNull();
  }, formFlowTimeout);

  it('shows a stored filter the event no longer offers and saves only once it is cleared', async () => {
    await configure();
    const admin = TestBed.inject(NhAssistantAdminApiService);
    const stored = await firstValueFrom(admin.saveDataTrigger('projects', 'status-check', {
      source: 'event', event: 'project.status-changed', filters: ['not-bulk']
    }));
    const catalog = await firstValueFrom(admin.getRunCatalog());
    // As the server lists it after a deploy removed the filter `not-weekend` the trigger still names.
    const trigger: DataTrigger = { ...stored, filters: ['not-bulk', 'not-weekend'], inEffect: false, problem: 'unknown-filter' };

    const fixture = TestBed.createComponent(NhAssistantTriggerFormComponent);
    fixture.componentRef.setInput('agentId', 'projects');
    fixture.componentRef.setInput('catalog', catalog satisfies RunCatalog);
    fixture.componentRef.setInput('trigger', trigger);
    const form = await settle(fixture);

    const missing = form.querySelector('[data-missing-filter="not-weekend"]') as HTMLElement;
    expect(missing.textContent).toContain('The event no longer offers this filter, so the trigger starts nothing.');
    expect((missing.querySelector('input') as HTMLInputElement).checked).toBeTrue();
    expect((form.querySelector('[data-filter="not-bulk"] input') as HTMLInputElement).checked).toBeTrue();

    // Saving is refused before it is sent while the missing filter is selected.
    button(form, 'Save').click();
    await settle(fixture);
    expect(requestBodies('PUT', 'admin/agents/projects/triggers/status-check').length).toBe(1);
    expect(form.querySelector('[data-trigger-filters]')?.textContent).toContain('The application does not know this name.');

    (missing.querySelector('input') as HTMLInputElement).click();
    await settle(fixture);
    expect(form.querySelector('[data-missing-filter="not-weekend"]')).toBeNull();
    button(form, 'Save').click();
    await settle(fixture);
    expect(requestBodies('PUT', 'admin/agents/projects/triggers/status-check').at(-1)).toEqual(jasmine.objectContaining({
      filters: ['not-bulk'], expectedVersion: 1
    }));
  });

  it('answers a filter the event does not register with its own code and a field error, as the server does', async () => {
    await configure();
    const admin = TestBed.inject(NhAssistantAdminApiService);
    const catalog = await firstValueFrom(admin.getRunCatalog());
    expect(catalog.triggerEvents.find(event => event.name === 'project.created')?.filters).toEqual([]);

    const refused = await firstValueFrom(admin.saveDataTrigger('projects', 'status-check', {
      source: 'event', event: 'project.status-changed', filters: ['not-weekend']
    })).then(() => null, (error: unknown) => error);

    expect(refused).toEqual(jasmine.any(NhAssistantApiError));
    const failure = refused as NhAssistantApiError;
    expect(failure.status).toBe(400);
    expect(failure.code).toBe('assistant-trigger-filter-not-found');
    expect(failure.fieldErrors['filters']).toEqual(['not-found']);
  });
});
