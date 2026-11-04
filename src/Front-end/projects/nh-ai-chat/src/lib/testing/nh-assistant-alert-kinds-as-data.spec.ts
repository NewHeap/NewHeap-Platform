import { Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { NH_ASSISTANT_PACKAGE_LOCK_FIELDS } from '../models/assistant-agent-package.models';
import { AgentRunConfiguration, RunConfigurationProblem } from '../models/assistant-admin.models';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAdminApiService } from '../services/nh-assistant-admin-api.service';
import { NH_ASSISTANT_TRANSLATIONS } from '../i18n/nh-assistant-translations';
import { openAdminAgent } from './nh-assistant-admin-navigation';
import { NhAssistantAdminAgentsComponent } from '../../../admin/src/public-api';
import { problemStep, problemWarning } from '../../../admin/src/agents/run-config/agent-flow';
import { alertKindsToSave } from '../../../admin/src/agents/run-config/nh-assistant-alert-kinds-field.component';

/**
 * Alert kinds of agents created in the administration (FB93): the run catalog lists every kind the
 * application registers; the run settings form and the guided setup tick the kinds the agent may raise by
 * their translated names and urgency; the Build tab and the flow show them, an agent in code's read-only;
 * a kind the application no longer registers is a problem at the results; in the mock API as on the server.
 */
function scenario(): NhAssistantMockScenario {
  return {
    agents: [{ id: 'deadline-watch', version: 1, displayNameKey: 'Deadline watch', descriptionKey: 'Warns about deadlines.', canMutate: false }],
    turns: [],
    subjectTypes: [{
      id: 'project',
      names: { en: 'Project', nl: 'Project' },
      pluralNames: { en: 'Projects', nl: 'Projecten' },
      subjects: [{ id: 'p-1', displayName: 'PRJ-1 Alpha' }]
    }],
    admin: {
      tools: [{ id: 'projects.list', source: 'local', effect: 'read-only', description: 'Lists projects.' }],
      agents: [
        {
          source: 'admin', id: 'weekly-count', displayName: 'Weekly count', description: 'Counts the changed projects.', instructions: 'Count.',
          toolSelectors: ['projects.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'propose', isEnabled: true,
          worksOnSubjectTypes: ['project'], worksOnAllSubjects: false, worksOnApplication: true
        },
        {
          source: 'code', id: 'deadline-watch', displayName: 'Deadline watch', description: 'Warns about deadlines.', instructions: 'Warn.',
          toolSelectors: ['projects.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'observe', isEnabled: true,
          worksOnSubjectTypes: ['project'], worksOnAllSubjects: false, worksOnApplication: false,
          codeRunSettings: { usesRules: true, alertKinds: ['task-due-today'] }
        }
      ],
      runCatalog: {
        triggerEvents: [{ name: 'project.changed', description: 'A project changed.', subjectType: 'project' }],
        subjectSets: [],
        ruleHandlers: [{ name: 'weekly-count', description: 'Counts the changed projects.' }],
        approvalPolicies: [],
        profiles: []
      },
      // The model of the deadline watch may raise the report reminder, so the catalog says so.
      alertModel: { 'deadline-watch': [{ kindId: 'report-reminder', description: 'A report is due.', parameters: [] }] }
    }
  };
}

/** A spec that walks a whole form flow takes one generous timeout. */
const formFlowTimeout = 20_000;

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
  const translate = TestBed.inject(TranslateService);
  translate.setTranslation('en', NH_ASSISTANT_TRANSLATIONS.en, true);
  // The application translates the names of its kinds, as it does for people's preferences.
  translate.setTranslation('en', {
    'nh-assistant': {
      'alert-kinds': {
        'invoice-overdue': { name: 'Invoice overdue' },
        'report-reminder': { name: 'Report reminder' },
        'task-due-today': { name: 'Task due today' }
      }
    }
  }, true);
  await firstValueFrom(translate.use('en'));
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
  const element = await settle(fixture);
  return { fixture, element };
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

function type(element: HTMLElement, selector: string, value: string): void {
  const input = element.querySelector(selector) as HTMLInputElement | HTMLTextAreaElement;
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
  const option = Array.from(scope.querySelectorAll<HTMLElement>('[role="option"]'))
    .find(item => item.querySelector('.label')?.textContent?.trim() === label);
  if (!option) {
    throw new Error(`Option "${label}" not found.`);
  }
  option.click();
  await settle(fixture);
}

function tick(scope: Element, kindId: string): void {
  const input = scope.querySelector(`[data-alert-kind="${kindId}"] input`) as HTMLInputElement | null;
  if (!input) {
    throw new Error(`Alert kind "${kindId}" not offered.`);
  }
  input.click();
}

async function call(method: string, path: string, body?: unknown): Promise<{ status: number; body: any }> {
  const response = await TestBed.inject(NhAssistantMockBackend).fetch(`/api/assistant/${path}`, {
    method,
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  const content = await response.text();
  return { status: response.status, body: content ? JSON.parse(content) : null };
}

describe('Alert kinds of agents created in the administration', () => {
  it('lists every registered kind in the run catalog and keeps, replaces and refuses the kinds of run settings in the mock API', async () => {
    await configure();

    const catalog = await call('GET', 'admin/run-catalog');
    expect(catalog.body.alertKinds).toEqual([
      { id: 'invoice-overdue', displayNameKey: 'nh-assistant.alert-kinds.invoice-overdue.name', urgency: 'urgent', raisedByModel: false },
      { id: 'report-reminder', displayNameKey: 'nh-assistant.alert-kinds.report-reminder.name', urgency: 'info', raisedByModel: true },
      { id: 'task-due-today', displayNameKey: 'nh-assistant.alert-kinds.task-due-today.name', urgency: 'warning', raisedByModel: false }
    ]);

    // A kind the application does not register, a malformed id or more than the bound are field errors.
    const path = 'admin/agents/weekly-count/run-settings';
    expect((await call('PUT', path, { accountability: 'agent', alertKinds: ['gone-kind'] })).body.errors).toEqual({ alertKinds: ['not-found'] });
    expect((await call('PUT', path, { accountability: 'agent', alertKinds: ['Not a kind'] })).body.errors).toEqual({ alertKinds: ['invalid'] });
    const many = Array.from({ length: 21 }, (_, index) => `kind-${index}`);
    expect((await call('PUT', path, { accountability: 'agent', alertKinds: many })).body.errors).toEqual({ alertKinds: ['invalid'] });

    // Each kind once, ordered; a save without the list keeps it and an empty list lists none.
    const saved = await call('PUT', path, { accountability: 'agent', alertKinds: ['task-due-today', 'invoice-overdue', 'task-due-today'] });
    expect(saved.body.alertKinds).toEqual(['invoice-overdue', 'task-due-today']);
    const kept = await call('PUT', path, { accountability: 'agent', expectedVersion: 1, retryCount: 1 });
    expect(kept.body.alertKinds).toEqual(['invoice-overdue', 'task-due-today']);
    const configuration = await call('GET', 'admin/agents/weekly-count/configuration');
    expect(configuration.body.runSettings.alertKinds).toEqual(['invoice-overdue', 'task-due-today']);
    // An agent in code shows the kinds its code lists.
    expect((await call('GET', 'admin/agents/deadline-watch/configuration')).body.codeRunSettings.alertKinds).toEqual(['task-due-today']);

    // An agent package carries the list, and a package can lock it.
    const exported = await firstValueFrom(TestBed.inject(NhAssistantAdminApiService).exportAgentPackage(['weekly-count']));
    expect(exported.package.agents[0].runSettings?.alertKinds).toEqual(['invoice-overdue', 'task-due-today']);
    expect(NH_ASSISTANT_PACKAGE_LOCK_FIELDS.runSettings).toContain('alertKinds');

    const cleared = await call('PUT', path, { accountability: 'agent', expectedVersion: 2, alertKinds: [] });
    expect(cleared.body.alertKinds).toEqual([]);
    // Without kinds the package leaves the list out, so its hash stays as it was.
    const plain = await firstValueFrom(TestBed.inject(NhAssistantAdminApiService).exportAgentPackage(['weekly-count']));
    expect(plain.package.agents[0].runSettings && 'alertKinds' in plain.package.agents[0].runSettings).toBeFalse();
  });

  it('ticks the kinds an agent may raise in its run settings and shows them on Build and in the flow, an agent in code read-only', async () => {
    await configure();
    await firstValueFrom(TestBed.inject(NhAssistantAdminApiService).saveRunSettings('weekly-count', { accountability: 'agent', ruleHandler: 'weekly-count' }));
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    await openAdminAgent(fixture, element, 'weekly-count', 'build');
    const view = () => element.querySelector('nh-assistant-agent-run-configuration') as HTMLElement;
    const summary = () => view().querySelector('[data-settings-summary]') as HTMLElement;
    expect(text(summary().querySelector('[data-settings-alert-kinds]'))).toBe('None');

    button(view(), 'Change run settings').click();
    await settle(fixture);
    const form = () => view().querySelector('[data-form="settings"]') as HTMLElement;
    const field = () => form().querySelector('[data-alert-kinds]') as HTMLElement;
    // Every kind by its name and urgency; the one the model may raise says so.
    expect(text(field().querySelector('legend'))).toBe('Alerts it may raise');
    expect(text(field().querySelector('[data-alert-kind="task-due-today"] span span'))).toBe('Task due today');
    expect(text(field().querySelector('[data-alert-kind="task-due-today"] [data-alert-kind-urgency]'))).toBe('Warning · task-due-today');
    expect(text(field().querySelector('[data-alert-kind="invoice-overdue"] [data-alert-kind-urgency]'))).toBe('Urgent · invoice-overdue');
    expect(field().querySelector('[data-alert-kind="report-reminder"] [data-alert-kind-model]')).not.toBeNull();
    tick(field(), 'task-due-today');
    await settle(fixture);
    tick(field(), 'invoice-overdue');
    await settle(fixture);
    button(form(), 'Save').click();
    await settle(fixture);

    const saved = (await call('GET', 'admin/agents/weekly-count/configuration')).body as AgentRunConfiguration;
    expect(saved.runSettings?.alertKinds).toEqual(['invoice-overdue', 'task-due-today']);
    expect(text(summary().querySelector('[data-settings-alert-kinds]'))).toBe('Invoice overdue, Task due today');

    // A save of another field keeps the list: the form sends it only when it changed.
    button(view(), 'Change run settings').click();
    await settle(fixture);
    const retries = form().querySelector('input[id$="-retries"]') as HTMLInputElement;
    retries.value = '2';
    retries.dispatchEvent(new Event('input'));
    button(form(), 'Save').click();
    await settle(fixture);
    const again = (await call('GET', 'admin/agents/weekly-count/configuration')).body as AgentRunConfiguration;
    expect(again.runSettings?.version).toBe(3);
    expect(again.runSettings?.alertKinds).toEqual(['invoice-overdue', 'task-due-today']);

    // The flow shows them at the agent's results.
    await back(fixture, element);
    await openAdminAgent(fixture, element, 'weekly-count');
    const results = element.querySelector('nh-assistant-agent-flow [data-flow-step="results"]') as HTMLElement;
    expect(text(results.querySelector('[data-flow-alert-kinds]'))).toBe('Alerts it may raise: Invoice overdue, Task due today');

    // An agent in code shows its code's kinds read-only.
    await back(fixture, element);
    await openAdminAgent(fixture, element, 'deadline-watch', 'build');
    expect(text(element.querySelector('[data-code-setting="alert-kinds"]'))).toBe('Task due today');
    expect(element.querySelector('[data-alert-kinds] input')).toBeNull();
  }, formFlowTimeout);

  it('offers the kinds in the guided setup, where the run settings are first made, and reviews them', async () => {
    await configure();
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    await openAdminAgent(fixture, element, 'weekly-count', 'build');
    const page = element.querySelector('[data-agent-page]') as HTMLElement;
    const setup = () => element.querySelector('[data-work-setup]') as HTMLElement;
    if (!setup()) {
      button(page, 'Let this agent work on its own').click();
      await settle(fixture);
    }

    // When: a project changes.
    await pick(fixture, setup(), 'input[id$="-event"]', 'changed', 'project.changed');
    button(setup(), 'Next').click();
    await settle(fixture);

    // What: by rules, with the kind it may raise.
    type(setup(), 'input[id$="-name"]', 'Count changes');
    type(setup(), 'input[id$="-description"]', 'Counts the changed projects.');
    await pick(fixture, setup(), 'input[id$="-rules"]', 'weekly', 'weekly-count');
    tick(setup().querySelector('[data-alert-kinds]') as HTMLElement, 'task-due-today');
    await settle(fixture);
    button(setup(), 'Next').click();
    await settle(fixture);

    // On whose behalf: the agent itself; then the approval and limits as they are.
    (setup().querySelector('input[type="radio"][value="agent"]') as HTMLInputElement).click();
    await settle(fixture);
    button(setup(), 'Next').click();
    await settle(fixture);
    button(setup(), 'Next').click();
    await settle(fixture);
    expect(text(setup().querySelector('[data-review-alert-kinds]'))).toBe('Task due today');

    button(setup(), 'Save in shadow').click();
    await settle(fixture);
    const saved = (await call('GET', 'admin/agents/weekly-count/configuration')).body as AgentRunConfiguration;
    expect(saved.runSettings?.alertKinds).toEqual(['task-due-today']);
  }, formFlowTimeout);

  it('shows a kind the application no longer registers as a problem of the agent\'s results that the run settings fix', () => {
    const problem: RunConfigurationProblem = { agentId: 'weekly-count', kind: 'alert-kind', id: 'gone-kind', code: 'unknown-alert-kind' };
    expect(problemStep(problem)).toBe('results');
    const configuration = { source: 'admin', runSettings: { alertKinds: ['gone-kind'] } } as unknown as AgentRunConfiguration;
    expect(problemWarning(problem, configuration)).toEqual(jasmine.objectContaining({
      key: 'nh-assistant.admin.run-config.problems.kind.alert-kind',
      detailKey: 'nh-assistant.admin.run-config.problems.code.unknown-alert-kind',
      entry: { kind: 'settings' }
    }));

    // The form sends the list only when it changed, so a kept kind the application removed is not refused.
    expect(alertKindsToSave(['gone-kind', 'task-due-today'], ['task-due-today', 'gone-kind'])).toBeUndefined();
    expect(alertKindsToSave(['task-due-today'], ['task-due-today', 'gone-kind'])).toEqual(['task-due-today']);
    expect(alertKindsToSave([], undefined)).toBeUndefined();
  });
});

async function back(fixture: ComponentFixture<unknown>, element: HTMLElement): Promise<HTMLElement> {
  (element.querySelector('[data-agent-page] [data-back]') as HTMLButtonElement).click();
  await settle(fixture);
  return element;
}
