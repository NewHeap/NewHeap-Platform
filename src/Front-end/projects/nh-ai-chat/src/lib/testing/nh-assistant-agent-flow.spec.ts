import { Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import {
  NhAssistantMockBackend,
  NhAssistantMockRunConfigurationPreset,
  NhAssistantMockScenario,
  provideNhAssistantMockApi
} from '@newheap/platform-ai-chat/testing';
import { firstValueFrom, throwError } from 'rxjs';
import { AdminAgent, AdminAgentInput, AgentRunConfiguration, ToolCatalogEntry } from '../models/assistant-admin.models';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAdminApiService } from '../services/nh-assistant-admin-api.service';
import { NhAssistantApiError } from '../services/nh-assistant-transport';
import { NH_ASSISTANT_TRANSLATIONS } from '../i18n/nh-assistant-translations';
import { NhAssistantAdminAgentsComponent, NhAssistantAdminComponent } from '../../../admin/src/public-api';

/**
 * The flow view of an agent (U6): the run configuration opens with what the agent works on, when it
 * runs, how it works, whom it answers to, who approves its proposals and its results, each step with
 * its problems and the entry point to the form that changes it; in the mock API as on the server.
 */
function scenario(): NhAssistantMockScenario {
  return {
    agents: [{ id: 'planner', version: 1, displayNameKey: 'Project planner', descriptionKey: 'Plans projects.', canMutate: true }],
    turns: [],
    subjectTypes: [{
      id: 'project',
      names: { en: 'Project', nl: 'Project' },
      pluralNames: { en: 'Projects', nl: 'Projecten' },
      subjects: [{ id: 'p-1', displayName: 'PRJ-1 Alpha' }]
    }],
    agentGroups: [{ id: 'project-management', names: { en: 'Project management', nl: 'Projectbeheer' }, agentIds: ['follow-up', 'planner'] }],
    runs: [
      { id: 'run-4', agentId: 'follow-up', subjectType: 'nh-system', subjectId: 'global', result: '5 open projects; proposed completing PRJ-1.', createdAt: '2026-10-19T07:00:00Z' },
      { id: 'run-3', agentId: 'follow-up', subjectType: 'nh-system', subjectId: 'global', result: '4 open projects; nothing new to complete.', createdAt: '2026-10-12T07:00:00Z' },
      { id: 'run-2', agentId: 'follow-up', subjectType: 'project', subjectId: 'p-1', status: 'failed', failureCode: 'assistant-turn-failed', createdAt: '2026-10-08T07:00:00Z' },
      { id: 'run-1', agentId: 'follow-up', subjectType: 'nh-system', subjectId: 'global', result: '3 open projects.', createdAt: '2026-10-05T07:00:00Z' }
    ],
    runNow: { 'follow-up': { result: 'Counted now.' } },
    admin: {
      tools: [
        { id: 'projects.change-status', source: 'local', effect: 'mutation', description: 'Changes the status of a project.', subjectType: 'project' },
        { id: 'projects.list', source: 'local', effect: 'read-only', description: 'Lists projects.' },
        { id: 'projects.note', source: 'local', effect: 'mutation', description: 'Adds a note to the portfolio.' }
      ],
      agents: [
        {
          source: 'code', id: 'planner', displayName: 'Project planner', description: 'Plans projects.', instructions: 'Plan.',
          toolSelectors: ['projects.*'], mcpServerIds: ['team-calendar'], requiredPolicy: null, autonomy: 'execute', isEnabled: true,
          worksOnSubjectTypes: ['project'], worksOnAllSubjects: false, worksOnApplication: true,
          serviceAccount: 'operations',
          codeRunSettings: { approvalPolicy: 'project-review', usesRules: true },
          skills: [{ skillId: 'task-follow-up', description: 'Follows up completed tasks by rule.' }]
        },
        {
          source: 'admin', id: 'follow-up', displayName: 'Project follow-up', description: 'Counts the open projects.', instructions: 'Count.',
          toolSelectors: ['projects.change-status', 'projects.removed-tool'], mcpServerIds: ['documents'], requiredPolicy: null,
          autonomy: 'propose', isEnabled: true, offeredInChat: false,
          worksOnSubjectTypes: ['project'], worksOnAllSubjects: false, worksOnApplication: true,
          runPolicy: 'app.project.manage', viewPolicy: 'app.project.view'
        },
        {
          source: 'admin', id: 'drafts', displayName: 'Draft helper', description: 'Helps with drafts.', instructions: 'Help.',
          toolSelectors: [], mcpServerIds: [], requiredPolicy: null, autonomy: 'propose', isEnabled: true,
          worksOnSubjectTypes: ['project'], worksOnAllSubjects: false, worksOnApplication: false
        }
      ],
      mcpServers: [
        {
          id: 'documents', displayName: 'Project documents', url: 'https://documents.example.com/mcp', authMode: 'api-key',
          headerName: 'X-Api-Key', secret: 'sample-only-key', requiredPolicy: null, isEnabled: true, headlessUse: 'never'
        },
        {
          id: 'team-calendar', displayName: 'Team calendar', url: 'https://planning.example.com/mcp', authMode: 'forward-user-token',
          headerName: null, requiredPolicy: null, isEnabled: true, headlessUse: 'personal'
        }
      ],
      forwardUserTokenHosts: ['planning.example.com'],
      accounts: [{ actorId: 'operations', displayName: 'Operations', detail: 'operations@example.test' }],
      runCatalog: {
        triggerEvents: [{ name: 'project.created', description: 'A project was created.', subjectType: 'project' }],
        subjectSets: [{ name: 'active-projects', description: 'Active projects.' }],
        ruleHandlers: [{ name: 'project-follow-up', description: 'Counts the open projects and proposes completions.' }],
        approvalPolicies: ['project-review'],
        approvalPolicySummaries: [{
          name: 'project-review',
          approvers: [{ kind: 'subject-role', role: 'approver', namedRole: null, policy: null }],
          lifetimeMinutes: 3 * 24 * 60,
          fourEyes: true,
          mode: 'deferred'
        }],
        agentPolicies: [
          { name: 'app.project.manage', description: 'Project managers.' },
          { name: 'app.project.view', description: 'Everyone who reads projects.' }
        ]
      },
      codeTriggers: [
        { agentId: 'planner', triggerId: 'daily-deadline-check', source: 'schedule', subjectType: 'nh-system' },
        { agentId: 'planner', triggerId: 'task-completed', source: 'event', skillId: 'task-follow-up', subjectType: 'project' },
        { agentId: 'planner', triggerId: 'portfolio-import', source: 'webhook' }
      ]
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
  const translate = TestBed.inject(TranslateService);
  translate.setTranslation('en', NH_ASSISTANT_TRANSLATIONS.en, true);
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

function text(element: Element | null | undefined): string {
  return element?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

/** The account step's line: how the agent answers, and its technical account by name only. */
function expectAccount(scope: Element, name: string): void {
  expect(text(scope.querySelector('[data-flow-account] strong'))).toBe('A technical account');
  expect(text(scope.querySelector('[data-flow-account-name]'))).toBe(name);
}

function button(scope: Element, label: string): HTMLButtonElement {
  const match = Array.from(scope.querySelectorAll('button')).find(item => text(item) === label);
  if (!match) {
    throw new Error(`Button "${label}" not found.`);
  }
  return match;
}

async function call(method: string, path: string, body?: unknown): Promise<{ status: number; body: any }> {
  const response = await TestBed.inject(NhAssistantMockBackend).fetch(`/api/assistant/${path}`, {
    method,
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  const content = await response.text();
  return { status: response.status, body: content ? JSON.parse(content) : null };
}

/** Gives the follow-up what the sample's seeder gives it: run settings, its skill and two triggers. */
async function configureFollowUp(): Promise<void> {
  const admin = TestBed.inject(NhAssistantAdminApiService);
  await firstValueFrom(admin.saveRunSettings('follow-up', {
    accountability: 'service-account',
    serviceAccountActorId: 'operations',
    approvalPolicy: 'project-review',
    ruleHandler: 'project-follow-up'
  }));
  await firstValueFrom(admin.saveDataSkill('follow-up', 'complete-projects', {
    description: 'Counts the open projects and proposes to complete the finished ones.',
    ruleHandler: 'project-follow-up',
    tools: ['projects.change-status']
  }));
  await firstValueFrom(admin.saveDataTrigger('follow-up', 'weekly', {
    source: 'schedule', skill: 'complete-projects', cron: '0 7 * * 1', timeZone: 'Europe/Amsterdam', message: 'Count the open projects.'
  }));
  await firstValueFrom(admin.saveDataTrigger('follow-up', 'created', { source: 'event', event: 'project.created' }));
}

async function render<T>(component: Type<T>): Promise<{ fixture: ComponentFixture<T>; element: HTMLElement }> {
  const fixture = TestBed.createComponent(component);
  const element = await settle(fixture);
  return { fixture, element };
}

/** Opens the run configuration of the agent on the agents page and returns its flow. */
async function openFlow(fixture: ComponentFixture<unknown>, element: HTMLElement, agentId: string): Promise<HTMLElement> {
  (element.querySelector(`[data-run-config-agent="${agentId}"]`) as HTMLButtonElement).click();
  await settle(fixture);
  return flowOf(element);
}

function flowOf(element: HTMLElement): HTMLElement {
  const flow = element.querySelector('nh-assistant-agent-flow [data-flow]') as HTMLElement | null;
  if (!flow) {
    throw new Error('The flow is not shown.');
  }
  return flow;
}

function step(flow: HTMLElement, id: string): HTMLElement {
  return flow.querySelector(`[data-flow-step="${id}"]`) as HTMLElement;
}

describe('The flow view of an agent', () => {
  it('shows an agent created here as one flow in order, from what it works on to its latest runs', async () => {
    await configure();
    await configureFollowUp();
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const flow = await openFlow(fixture, element, 'follow-up');

    // An ordered list of six steps, each with its heading, at the top of the run configuration.
    const configuration = element.querySelector('nh-assistant-agent-run-configuration') as HTMLElement;
    const settings = configuration.querySelector('[data-run-settings]') as HTMLElement;
    expect(flow.compareDocumentPosition(settings) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(flow.querySelector('ol.flow-steps')?.children.length).toBe(6);
    expect(Array.from(flow.querySelectorAll('ol.flow-steps > li h5')).map(text)).toEqual([
      'What it works on', 'When it runs', 'How it works', 'Who it answers to', 'Who approves its proposals', 'Its results'
    ]);

    // What it works on: the kind of object by name and the whole application.
    expect(text(step(flow, 'works-on').querySelector('[data-flow-works-on]'))).toBe('Projects and the whole application');

    // When: each trigger with what it runs for, and run now with who may run it for the whole application.
    const when = step(flow, 'when');
    expect(text(when.querySelector('[data-flow-trigger="weekly"] [data-flow-target]'))).toBe('For the whole application');
    expect(text(when.querySelector('[data-flow-trigger="weekly"]'))).toContain('0 7 * * 1 Europe/Amsterdam');
    expect(text(when.querySelector('[data-flow-trigger="created"] [data-flow-target]'))).toBe('For the object it names: Project');
    expect(text(when.querySelector('[data-flow-run-now]')))
      .toBe('Run now: for the whole application or one of the objects it works on. For the whole application only by: app.project.manage.');

    // How: the rules by their description, the skill, and where a run's proposal of each tool waits.
    const skills = step(flow, 'skills');
    expect(text(skills.querySelector('[data-flow-way]'))).toContain('Rules: project-follow-up');
    expect(text(skills.querySelector('[data-flow-way]'))).toContain('Counts the open projects and proposes completions.');
    expect(skills.querySelector('[data-flow-skill="complete-projects"]')).not.toBeNull();
    expect(text(skills.querySelector('[data-flow-tool="projects.change-status"] [data-flow-places]')))
      .toBe("A run's proposal waits at the object it names (Project), where that object's approvers decide.");
    expect(text(skills.querySelector('[data-flow-server="documents"] [data-flow-server-use]')))
      .toBe('Its tools are left out of runs that start on their own, such as trigger runs.');

    // Who it answers to: the technical account by name only; its ID is the tooltip and can be copied.
    const account = step(flow, 'account');
    expectAccount(account, 'Operations');
    expect(account.querySelector('[data-account-label-name]')?.getAttribute('title')).toBe('Account ID: operations');
    expect(account.querySelector('[data-account-copy-id]')?.getAttribute('aria-label')).toBe('Copy the account ID of Operations');
    expect(text(account)).toContain('Runs use what this account may use, within its tools.');

    // Who approves: the policy in a sentence.
    expect(text(step(flow, 'approval').querySelector('[data-flow-policy="agent"]'))).toContain('project-review');
    expect(step(flow, 'approval').querySelector('[data-flow-policy-sentence]')).not.toBeNull();

    // Results: its group, who sees them for the whole application and the three latest runs.
    const results = step(flow, 'results');
    expect(text(results.querySelector('[data-flow-groups]'))).toBe('Groups: Project management');
    expect(text(results.querySelector('[data-flow-view-policy]'))).toBe('Its results for the whole application are seen by: app.project.view.');
    expect(Array.from(results.querySelectorAll('[data-flow-run]')).map(item => item.getAttribute('data-flow-run'))).toEqual(['run-4', 'run-3', 'run-2']);
    expect(text(results.querySelector('[data-flow-run="run-4"] [data-flow-run-preview]'))).toBe('5 open projects; proposed completing PRJ-1.');
    expect(text(results)).toContain('And 1 earlier runs in its runs.');
  });

  it('labels the ID of a technical account whose name is not known', async () => {
    await configure();
    await configureFollowUp();
    // The names of accounts cannot be read, so the screens know none.
    spyOn(TestBed.inject(NhAssistantAdminApiService), 'getAccounts')
      .and.returnValue(throwError(() => new NhAssistantApiError(503, 'assistant-unavailable', 'nh-assistant.errors.assistant-unavailable')));
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const flow = await openFlow(fixture, element, 'follow-up');

    const account = step(flow, 'account');
    expectAccount(account, 'Account ID operations');
    expect(account.querySelector('[data-account-label-id]')).not.toBeNull();
    expect(account.querySelector('[data-account-copy-id]')).toBeNull();
    const summary = element.querySelector('[data-settings-summary] [data-account-name]') as HTMLElement;
    expect(text(summary)).toBe('Account ID operations');
    expect(element.querySelector('nh-assistant-agent-run-configuration [data-account-names-error]')).not.toBeNull();
  });

  it('shows problems at the step they concern with the entry point that fixes them', async () => {
    await configure();
    await configureFollowUp();
    // Another administrator narrows the follow-up to the whole application only: its event trigger on
    // projects no longer fits.
    const admin = TestBed.inject(NhAssistantAdminApiService);
    const current = (await firstValueFrom(admin.getAgents())).find(agent => agent.id === 'follow-up')!;
    const input: AdminAgentInput = { ...current, worksOnSubjectTypes: [], worksOnAllSubjects: false, worksOnApplication: true };
    await firstValueFrom(admin.updateAgent('follow-up', { ...input, expectedVersion: current.version }));
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const flow = await openFlow(fixture, element, 'follow-up');

    expect(text(element.querySelector('[data-problems]'))).toContain('Each shows at its step below');
    const when = step(flow, 'when');
    expect(when.classList).toContain('attention');
    expect(text(when.querySelector('[data-flow-attention]'))).toBe('1 to fix');
    const warning = when.querySelector('[data-flow-warning="problem:trigger:created:not-worked-on"]') as HTMLElement;
    expect(text(warning)).toContain('Trigger created: it concerns a kind of object, or the whole application, that the agent does not work on.');

    // The tools that are no longer offered and the MCP server without background use show at the skills.
    const skills = step(flow, 'skills');
    expect(text(skills.querySelector('[data-flow-warning="tool:projects.removed-tool"]')))
      .toContain('projects.removed-tool is no longer offered: no tool of the application matches it.');
    expect(text(skills.querySelector('[data-flow-warning="server:documents"]')))
      .toContain('MCP server Project documents is not used in runs that start on their own: its background use is off.');
    expect(step(flow, 'works-on').querySelector('[data-flow-warning]')).toBeNull();

    // The fix opens the trigger's form below; cancelling returns to the fix.
    const fix = button(warning, 'Change the trigger');
    fix.focus();
    fix.click();
    await settle(fixture);
    const form = element.querySelector('[data-form="trigger"]') as HTMLElement;
    expect(form).not.toBeNull();
    expect(form.contains(document.activeElement)).toBeTrue();
    button(form, 'Cancel').click();
    await settle(fixture);
    expect(element.querySelector('[data-form="trigger"]')).toBeNull();
    expect(document.activeElement).toBe(step(flowOf(element), 'when').querySelector('[data-flow-fix="edit-trigger"]'));
  });

  it('opens every form it summarizes from its steps and keeps the forms below reachable', async () => {
    await configure();
    await configureFollowUp();
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    let flow = await openFlow(fixture, element, 'follow-up');

    const opens = async (selector: string, form: string): Promise<void> => {
      (flow.querySelector(selector) as HTMLButtonElement).click();
      await settle(fixture);
      expect(element.querySelector(`[data-form="${form}"]`)).withContext(selector).not.toBeNull();
      button(element.querySelector(`[data-form="${form}"]`)!, 'Cancel').click();
      await settle(fixture);
      flow = flowOf(element);
    };
    await opens('[data-flow-entry="add-trigger"]', 'trigger');
    await opens('[data-flow-trigger="weekly"] button', 'trigger');
    await opens('[data-flow-entry="add-skill"]', 'skill');
    await opens('[data-flow-skill="complete-projects"] button', 'skill');
    await opens('[data-flow-entry="settings"]', 'settings');
    await opens('[data-flow-entry="approval"]', 'settings');

    // The sections below keep their own actions.
    const configuration = element.querySelector('nh-assistant-agent-run-configuration') as HTMLElement;
    expect(configuration.querySelector('[data-run-settings] [data-open-form="settings"]')).not.toBeNull();
    expect(configuration.querySelector('[data-skills] [data-open-form="skill"]')).not.toBeNull();
    expect(configuration.querySelector('[data-triggers] [data-open-form="trigger"]')).not.toBeNull();
    expect(configuration.querySelector('[data-data-trigger="weekly"]')).not.toBeNull();

    // The edit button of a trigger names it.
    expect(flow.querySelector('[data-flow-trigger="weekly"] button')?.getAttribute('aria-label')).toBe('Edit trigger weekly');

    // Run now opens the dialog in place.
    (flow.querySelector('[data-flow-entry="run-now"]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(element.querySelector('nh-assistant-agent-flow nh-assistant-run-now-dialog dialog')).not.toBeNull();
  });

  it('opens the editor and the runs from its steps and returns to the run configuration', async () => {
    await configure();
    await configureFollowUp();
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    let flow = await openFlow(fixture, element, 'follow-up');

    // What it works on opens the agent editor; cancelling returns to the flow.
    (flow.querySelector('[data-flow-entry="edit-agent"]') as HTMLButtonElement).click();
    await settle(fixture);
    const editor = element.querySelector('nh-assistant-admin-agent-editor') as HTMLElement;
    expect(editor).not.toBeNull();
    button(editor, 'Cancel').click();
    await settle(fixture);
    flow = flowOf(element);

    // Its groups open the editor too; saving returns to the flow with the change.
    (flow.querySelector('[data-flow-entry="groups"]') as HTMLButtonElement).click();
    await settle(fixture);
    const groups = element.querySelector('nh-assistant-admin-agent-editor') as HTMLElement;
    const member = groups.querySelector('[data-agent-group="project-management"] input') as HTMLInputElement;
    member.click();
    await settle(fixture);
    button(groups, 'Save').click();
    await settle(fixture);
    flow = flowOf(element);
    expect(text(step(flow, 'results').querySelector('[data-flow-groups]'))).toBe('Groups: In no group, so no board shows it.');

    // All runs opens the runs view, whose back button returns to the run configuration.
    (flow.querySelector('[data-flow-entry="runs"]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(element.querySelector('nh-assistant-agent-run-history')).not.toBeNull();
    button(element, 'Back to the run configuration').click();
    await settle(fixture);
    expect(flowOf(element)).not.toBeNull();
    expect(element.querySelector('nh-assistant-agent-run-history')).toBeNull();
  });

  it('shows how an agent in code runs, what its triggers run for and which MCP servers its runs cannot use', async () => {
    await configure();
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const flow = await openFlow(fixture, element, 'planner');

    // Set in code: no entry to change what it works on.
    expect(text(step(flow, 'works-on'))).toContain("Set in the application's code.");
    expect(step(flow, 'works-on').querySelector('[data-flow-entry]')).toBeNull();

    // Its triggers in code, with the type they name up front.
    const when = step(flow, 'when');
    expect(text(when.querySelector('[data-flow-trigger="daily-deadline-check"] [data-flow-target]'))).toBe('For the whole application');
    expect(text(when.querySelector('[data-flow-trigger="task-completed"] [data-flow-target]'))).toBe('For the object it names: Project');
    expect(text(when.querySelector('[data-flow-trigger="portfolio-import"] [data-flow-target]'))).toBe('For the object the event names');
    expect(when.querySelector('[data-flow-trigger] button')).toBeNull();

    // Fixed rules in code; its tools say where their proposals wait.
    const skills = step(flow, 'skills');
    expect(text(skills.querySelector('[data-flow-way]'))).toBe("Way of working: Fixed rules in the application's code");
    expect(text(skills.querySelector('[data-flow-tool="projects.note"] [data-flow-places]'))).toBe("A run's proposal waits in the run's own thread.");
    expect(skills.querySelector('[data-flow-tool="projects.list"] [data-flow-places]')).toBeNull();
    // A personal MCP server never serves a technical account.
    expect(text(skills.querySelector('[data-flow-warning="server:team-calendar"]')))
      .toContain('MCP server Team calendar is not used: it works with personal accounts and the agent answers to a technical account.');

    // It answers to its technical account, and its approval policy comes from its code.
    expectAccount(step(flow, 'account'), 'Operations');
    expect(text(step(flow, 'approval').querySelector('[data-flow-policy="agent"]'))).toContain('project-review');

    // Changing its technical account happens on its card in the list.
    (step(flow, 'account').querySelector('[data-flow-entry="account"]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(element.querySelector('nh-assistant-agent-run-configuration')).toBeNull();
    expect(element.querySelector('[data-account-form]')).not.toBeNull();
  });

  it('asks an agent created here without run settings to work on its own first', async () => {
    await configure();
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const flow = await openFlow(fixture, element, 'drafts');

    expect(text(step(flow, 'when').querySelector('[data-flow-run-now]'))).toBe('Run now: only once the agent has run settings.');
    expect(step(flow, 'when').querySelector('[data-flow-entry="run-now"]')).toBeNull();
    expect(step(flow, 'when').querySelector('[data-flow-entry="add-trigger"]')).toBeNull();
    const warning = step(flow, 'account').querySelector('[data-flow-warning="no-settings"]') as HTMLElement;
    expect(text(warning)).toContain('It has no run settings yet, so it cannot run on its own.');

    button(warning, 'Let this agent work on its own').click();
    await settle(fixture);
    expect(element.querySelector('nh-assistant-agent-work-setup')).not.toBeNull();
  });

  it('opens the MCP servers from a server that is not used in runs on their own', async () => {
    await configure();
    await configureFollowUp();
    const { fixture, element } = await render(NhAssistantAdminComponent);
    (Array.from(element.querySelectorAll<HTMLElement>('[role="tab"]')).find(tab => text(tab) === 'Agents')!).click();
    await settle(fixture);
    const flow = await openFlow(fixture, element, 'follow-up');

    button(step(flow, 'skills').querySelector('[data-flow-warning="server:documents"]')!, 'Open the MCP servers').click();
    await settle(fixture);

    const selected = element.querySelector('[role="tab"][aria-selected="true"]') as HTMLElement;
    expect(text(selected)).toBe('MCP servers');
    expect(document.activeElement).toBe(selected);
    expect(element.querySelector('nh-assistant-admin-mcp-servers')).not.toBeNull();
  });

  it('says why the latest runs could not be read and reads them again', async () => {
    await configure();
    const admin = TestBed.inject(NhAssistantAdminApiService);
    const failure = new NhAssistantApiError(503, 'assistant-disabled', 'nh-assistant.errors.assistant-disabled');
    const original = admin.getAgentRuns.bind(admin);
    const spy = spyOn(admin, 'getAgentRuns').and.returnValue(throwError(() => failure));
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const flow = await openFlow(fixture, element, 'follow-up');

    const error = step(flow, 'results').querySelector('[data-flow-runs-error]') as HTMLElement;
    expect(error.getAttribute('role')).toBe('alert');
    expect(text(error)).toContain('The assistant is switched off.');

    spy.and.callFake(original);
    button(error, 'Try again').click();
    await settle(fixture);
    expect(step(flowOf(element), 'results').querySelector('[data-flow-runs-error]')).toBeNull();
    expect(step(flowOf(element), 'results').querySelectorAll('[data-flow-run]').length).toBe(3);
  });

  it('fits a 375px wide page without scrolling sideways', async () => {
    await configure();
    await configureFollowUp();
    const fixture = TestBed.createComponent(NhAssistantAdminAgentsComponent);
    const host = fixture.nativeElement as HTMLElement;
    host.style.display = 'block';
    host.style.width = '375px';
    document.body.appendChild(host);
    try {
      const element = await settle(fixture);
      const flow = await openFlow(fixture, element, 'follow-up');

      expect(flow.getBoundingClientRect().width).toBeLessThanOrEqual(375);
      expect(host.scrollWidth).toBeLessThanOrEqual(host.clientWidth + 1);
      for (const item of Array.from(flow.querySelectorAll<HTMLElement>('.step-body, .message, button'))) {
        expect(item.getBoundingClientRect().right).withContext(text(item).slice(0, 40)).toBeLessThanOrEqual(host.getBoundingClientRect().right + 1);
      }
      // Every step's entry points are buttons in the tab order.
      const entries = Array.from(flow.querySelectorAll<HTMLButtonElement>('[data-flow-entry]'));
      expect(entries.length).toBeGreaterThan(5);
      expect(entries.every(entry => entry.tabIndex === 0 && !entry.disabled)).toBeTrue();
    } finally {
      host.remove();
    }
  });
});

describe('The mock API for the flow view', () => {
  it('names the object type of a tool, what code triggers run for and how an agent in code runs', async () => {
    await configure();

    const tools = (await call('GET', 'admin/tools')).body as ToolCatalogEntry[];
    expect(tools.find(tool => tool.id === 'projects.change-status')?.subjectType).toBe('project');
    expect(tools.find(tool => tool.id === 'projects.list')?.subjectType).toBeNull();

    const planner = (await call('GET', 'admin/agents/planner/configuration')).body as AgentRunConfiguration;
    expect(planner.codeTriggers.map(trigger => [trigger.triggerId, trigger.subjectType])).toEqual([
      ['daily-deadline-check', 'nh-system'],
      ['task-completed', 'project'],
      ['portfolio-import', null]
    ]);
    expect(planner.codeRunSettings).toEqual({
      accountability: 'service-account',
      delegateFromInitiator: false,
      approvalPolicy: 'project-review',
      usesRules: true,
      startsOff: false
    });

    const followUp = (await call('GET', 'admin/agents/follow-up/configuration')).body as AgentRunConfiguration;
    expect(followUp.codeRunSettings).toBeNull();
    // The scenario's run settings of an agent in code do not leak into the agent itself.
    const agents = (await call('GET', 'admin/agents')).body as Record<string, unknown>[];
    expect(Object.keys(agents.find(agent => agent['id'] === 'planner')!)).not.toContain('codeRunSettings');
  });

  it('starts an agent created here with the run configuration the scenario saves for it, so its flow shows', async () => {
    const script = scenario();
    script.admin!.agents!.find(agent => agent.id === 'follow-up')!.runConfiguration = followUpConfiguration();
    await configure(script);

    const configuration = (await call('GET', 'admin/agents/follow-up/configuration')).body as AgentRunConfiguration;
    expect(configuration.runSettings).toEqual(jasmine.objectContaining({
      version: 1,
      accountability: 'service-account',
      serviceAccountActorId: 'operations',
      approvalPolicy: 'project-review',
      ruleHandler: 'project-follow-up'
    }));
    expect(configuration.dataSkills.map(skill => [skill.skillId, skill.ruleHandler])).toEqual([['complete-projects', 'project-follow-up']]);
    // Saved triggers start in shadow unless the scenario puts them live.
    expect(configuration.dataTriggers.map(trigger => [trigger.triggerId, trigger.shadow])).toEqual([['weekly', false], ['created', true]]);
    expect(configuration.problems).toEqual([]);
    const agents = (await call('GET', 'admin/agents')).body as AdminAgent[];
    const followUp = agents.find(agent => agent.id === 'follow-up')!;
    expect(followUp.skills?.find(skill => skill.skillId === 'complete-projects')?.shadow?.shadow).toBeFalse();
    expect(Object.keys(followUp)).not.toContain('runConfiguration');

    // The administration shows the agent's flow instead of asking it to work on its own first.
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const flow = await openFlow(fixture, element, 'follow-up');
    expect(step(flow, 'account').querySelector('[data-flow-warning="no-settings"]')).toBeNull();
    expectAccount(step(flow, 'account'), 'Operations');
    expect(text(step(flow, 'when').querySelector('[data-flow-trigger="weekly"] [data-flow-target]'))).toBe('For the whole application');
    expect(step(flow, 'skills').querySelector('[data-flow-skill="complete-projects"]')).not.toBeNull();
    expect(element.querySelector('nh-assistant-agent-work-setup')).toBeNull();
  });

  it('refuses a scenario whose run configuration the mock would refuse to save', async () => {
    const unknownRules = scenario();
    unknownRules.admin!.agents!.find(agent => agent.id === 'follow-up')!.runConfiguration = {
      runSettings: { accountability: 'service-account', serviceAccountActorId: 'operations', ruleHandler: 'unknown-rules' }
    };
    await expectAsync(start(unknownRules)).toBeRejectedWithError(/run settings of agent 'follow-up'.*"ruleHandler":\["not-found"\]/);

    TestBed.resetTestingModule();
    const unknownSkill = scenario();
    unknownSkill.admin!.agents!.find(agent => agent.id === 'follow-up')!.runConfiguration = {
      ...followUpConfiguration(),
      triggers: [{ triggerId: 'weekly', source: 'schedule', skill: 'missing', cron: '0 7 * * 1', message: 'Count.' }]
    };
    await expectAsync(start(unknownSkill)).toBeRejectedWithError(/trigger 'weekly' of agent 'follow-up'.*"skill":\["not-found"\]/);

    TestBed.resetTestingModule();
    const inCode = scenario();
    inCode.admin!.agents!.find(agent => agent.id === 'planner')!.runConfiguration = followUpConfiguration();
    await expectAsync(start(inCode)).toBeRejectedWithError(/Mock agent 'planner' runs as its code says/);
  });
});

/** Starts the mock API of the scenario. */
async function start(script: NhAssistantMockScenario): Promise<void> {
  await configure(script);
  TestBed.inject(NhAssistantMockBackend);
}

/** The follow-up's run configuration as the sample's seeder saves it, as a scenario presets it. */
function followUpConfiguration(): NhAssistantMockRunConfigurationPreset {
  return {
    runSettings: {
      accountability: 'service-account',
      serviceAccountActorId: 'operations',
      approvalPolicy: 'project-review',
      ruleHandler: 'project-follow-up'
    },
    skills: [{
      skillId: 'complete-projects',
      description: 'Counts the open projects and proposes to complete the finished ones.',
      ruleHandler: 'project-follow-up',
      tools: ['projects.change-status'],
      shadow: false
    }],
    triggers: [
      {
        triggerId: 'weekly',
        source: 'schedule',
        skill: 'complete-projects',
        cron: '0 7 * * 1',
        timeZone: 'Europe/Amsterdam',
        message: 'Count the open projects.',
        shadow: false
      },
      { triggerId: 'created', source: 'event', event: 'project.created' }
    ]
  };
}
