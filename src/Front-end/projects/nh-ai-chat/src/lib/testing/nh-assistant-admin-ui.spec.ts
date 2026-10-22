import { Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAdminApiService } from '../services/nh-assistant-admin-api.service';
import { NhAssistantStore } from '../services/nh-assistant.store';
import { NhAssistantAgentPickerComponent } from '../components/agent-picker/nh-assistant-agent-picker.component';
import {
  NhAssistantAdminAgentsComponent,
  NhAssistantAdminComponent,
  NhAssistantAdminContextComponent,
  NhAssistantAdminMcpServersComponent
} from '../../../admin/src/public-api';

const secretValue = 'do-not-show-this-secret';

/**
 * The time of a spec that walks a whole form flow: each searchable picker waits for its search, so such
 * a flow takes several seconds and needs more than Jasmine's default of five.
 */
const formFlowTimeout = 20_000;

function scenario(): NhAssistantMockScenario {
  return {
    agents: [{ id: 'projects', version: 1, displayNameKey: 'agents.projects', descriptionKey: 'agents.projects', canMutate: true }],
    turns: [],
    admin: {
      context: 'Initial context.',
      tools: [
        { id: 'sample-api.project.list', source: 'bridge', effect: 'read-only', description: 'List projects' },
        { id: 'sample-api.project.update-status', source: 'bridge', effect: 'mutation', description: 'Update status' }
      ],
      agents: [{
        source: 'code', id: 'projects', displayName: 'Project assistant', description: 'Projects', instructions: 'Code instructions.',
        toolSelectors: ['sample-api.project.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'execute', isEnabled: true
      }],
      mcpServers: [{
        id: 'files', displayName: 'Files', url: 'https://files.example.com/mcp', authMode: 'bearer', headerName: null,
        secret: secretValue, requiredPolicy: null, isEnabled: true,
        remoteTools: [
          { remoteName: 'readFile', description: 'Reads a file', inputSchemaHash: 'a1', readOnlyHint: true },
          { remoteName: 'deleteFile', description: 'Deletes a file', inputSchemaHash: 'b1', readOnlyHint: false }
        ]
      }]
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

function buttons(element: HTMLElement, text: string): HTMLButtonElement[] {
  return Array.from(element.querySelectorAll('button')).filter(item => item.textContent?.trim() === text);
}

function button(element: HTMLElement, text: string, index = 0): HTMLButtonElement {
  const match = buttons(element, text)[index];
  if (!match) {
    throw new Error(`Button "${text}" not found.`);
  }
  return match;
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

function check(element: HTMLElement, selector: string): void {
  const input = element.querySelector(selector) as HTMLInputElement;
  input.click();
}

/** Chooses an option of a searchable picker: opens it, types the query and clicks the option. */
async function pick(fixture: ComponentFixture<unknown>, scope: HTMLElement, selector: string, query: string, label: string): Promise<void> {
  const input = scope.querySelector(selector) as HTMLInputElement;
  input.click();
  input.value = query;
  input.dispatchEvent(new Event('input'));
  await settle(fixture);
  // A server search waits a moment after the last key.
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

/** A scenario with the building blocks in code that run configuration as data names. */
function runScenario(): NhAssistantMockScenario {
  const script = scenario();
  script.admin!.runCatalog = {
    triggerEvents: [{ name: 'project.created', description: 'A project was created.' }],
    subjectSets: [{ name: 'active-projects', description: 'Active projects.' }],
    ruleHandlers: [
      { name: 'project-kickoff', description: 'Starts draft projects.' },
      { name: 'project-progress-note', description: 'Notes progress.' }
    ],
    approvalPolicies: ['project-review'],
    profiles: ['project-chat']
  };
  return script;
}

function requestBodies(method: string, path: string): unknown[] {
  return TestBed.inject(NhAssistantMockBackend).requests
    .filter(item => item.method === method && item.path === path)
    .map(item => item.body);
}

describe('NhAssistantAdminComponent', () => {
  it('shows the tabs as an ARIA tablist that follows the arrow keys', async () => {
    await configure();
    const { fixture, element } = await render(NhAssistantAdminComponent);

    const tabs = () => Array.from(element.querySelectorAll('[role="tab"]')) as HTMLElement[];
    expect(tabs().map(tab => tab.textContent?.trim())).toEqual(['Context', 'Agents', 'MCP servers']);
    expect(tabs()[0].getAttribute('aria-selected')).toBe('true');
    expect(element.querySelector('[role="tabpanel"] nh-assistant-admin-context')).not.toBeNull();

    element.querySelector('[role="tablist"]')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
    await settle(fixture);

    expect(tabs()[1].getAttribute('aria-selected')).toBe('true');
    expect(tabs()[0].getAttribute('tabindex')).toBe('-1');
    expect(element.querySelector('[role="tabpanel"] nh-assistant-admin-agents')).not.toBeNull();
  });

  it('shows a no-access state without canAdminister', async () => {
    const script = scenario();
    script.admin!.canAdminister = false;
    await configure(script);

    const { element } = await render(NhAssistantAdminComponent);

    expect(element.textContent).toContain('No access');
    expect(element.querySelector('[role="tablist"]')).toBeNull();
  });

  it('shows the unavailable state when the assistant is off', async () => {
    await configure({ ...scenario(), enabled: false });

    const { element } = await render(NhAssistantAdminComponent);

    expect(element.textContent).toContain('The assistant is not available');
  });
});

describe('NhAssistantAdminContextComponent', () => {
  it('saves a new version with the expected version and lists the history', async () => {
    await configure();
    const { fixture, element } = await render(NhAssistantAdminContextComponent);

    expect((element.querySelector('textarea') as HTMLTextAreaElement).value).toBe('Initial context.');
    expect(element.textContent).toContain('Version 1');

    type(element, 'textarea', 'Updated context.');
    fixture.detectChanges();
    button(element, 'Save').click();
    await settle(fixture);

    expect(requestBodies('PUT', 'admin/context')).toEqual([{ text: 'Updated context.', expectedVersion: 1 }]);
    expect(element.textContent).toContain('Version 2');
    expect(element.querySelector('[role="status"]')?.textContent).toContain('Saved.');
    expect(element.querySelector('.history')?.textContent).toContain('Version 1');
  });

  it('reports a version conflict and keeps the draft when loading the latest version', async () => {
    await configure();
    const { fixture, element } = await render(NhAssistantAdminContextComponent);
    await firstValueFrom(TestBed.inject(NhAssistantAdminApiService).updateContext({ text: 'Someone else.', expectedVersion: 1 }));

    type(element, 'textarea', 'My draft.');
    fixture.detectChanges();
    button(element, 'Save').click();
    await settle(fixture);

    expect(element.querySelector('[role="alert"]')?.textContent).toContain('Someone saved a newer version.');

    button(element, 'Load latest version').click();
    await settle(fixture);

    expect((element.querySelector('textarea') as HTMLTextAreaElement).value).toBe('My draft.');
    expect(element.textContent).toContain('Version 2');
    expect(element.querySelector('[role="alert"]')).toBeNull();
  });

  it('blocks a context above 20,000 characters', async () => {
    await configure();
    const { fixture, element } = await render(NhAssistantAdminContextComponent);

    type(element, 'textarea', 'x'.repeat(20_001));
    fixture.detectChanges();

    expect(element.textContent).toContain('The text is too long.');
    expect(button(element, 'Save').disabled).toBeTrue();
  });
});

describe('NhAssistantAdminAgentsComponent', () => {
  it('lists code agents without a delete action', async () => {
    await configure();
    const { element } = await render(NhAssistantAdminAgentsComponent);

    const card = element.querySelector('.card') as HTMLElement;
    expect(card.textContent).toContain('Project assistant');
    expect(card.textContent).toContain('Code');
    expect(buttons(card, 'Delete').length).toBe(0);
  });

  it('opens the feedback report of an agent from its card and returns to the list', async () => {
    await configure();
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);

    button(element.querySelector('.card') as HTMLElement, 'Feedback').click();
    await settle(fixture);

    const report = element.querySelector('nh-assistant-agent-feedback-report') as HTMLElement;
    expect(report).not.toBeNull();
    expect(report.textContent).toContain('Feedback on proposals');
    expect(report.textContent).toContain('The agent made no proposals in this period.');
    expect(report.textContent).toContain('Nobody gave feedback in this period.');
    expect(button(report, 'Export (JSON Lines)').disabled).toBeTrue();
    expect(document.activeElement).toBe(button(element, 'Back to agents'));
    const summary = TestBed.inject(NhAssistantMockBackend).requests.find(item => item.path === 'admin/agents/projects/feedback/summary');
    expect(summary?.method).toBe('GET');

    button(element, 'Back to agents').click();
    await settle(fixture);

    expect(element.querySelector('nh-assistant-agent-feedback-report')).toBeNull();
    expect(document.activeElement).toBe(button(element.querySelector('.card') as HTMLElement, 'Feedback'));
  });

  it('creates an agent with catalog selectors and an MCP server', async () => {
    await configure();
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);

    button(element, 'New agent').click();
    await settle(fixture);
    type(element, 'input[id$="-id"]', 'support-agent');
    type(element, 'input[id$="-name"]', 'Support agent');
    type(element, 'textarea', 'Help with support questions.');
    const catalog = Array.from(element.querySelectorAll('.catalog input[type="checkbox"]')) as HTMLInputElement[];
    catalog[0].click();
    const server = Array.from(element.querySelectorAll('fieldset .check input[type="checkbox"]')).pop() as HTMLInputElement;
    server.click();
    fixture.detectChanges();

    expect(element.textContent).toContain('1 catalog tools match the selectors.');

    button(element, 'Save').click();
    await settle(fixture);

    expect(requestBodies('POST', 'admin/agents')).toEqual([jasmine.objectContaining({
      id: 'support-agent',
      displayName: 'Support agent',
      instructions: 'Help with support questions.',
      toolSelectors: ['sample-api.project.list'],
      mcpServerIds: ['files'],
      autonomy: 'explain'
    })]);
    expect(Object.keys(requestBodies('POST', 'admin/agents')[0] as object)).not.toContain('isEnabled');
    expect(element.textContent).toContain('Support agent');
    expect(element.textContent).toContain('Created here');
  });

  it('creates an agent that works with a model profile of the run catalog', async () => {
    await configure(runScenario());
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);

    button(element, 'New agent').click();
    await settle(fixture);
    type(element, 'input[id$="-id"]', 'profiled-agent');
    type(element, 'input[id$="-name"]', 'Profiled agent');
    type(element, 'textarea', 'Help with the chosen model.');
    const select = element.querySelector('[data-agent-profile] select') as HTMLSelectElement;
    expect(Array.from(select.options).map(option => option.textContent?.trim())).toEqual(["The application's chat profile", 'project-chat']);
    choose(element, '[data-agent-profile] select', 'project-chat');
    fixture.detectChanges();
    button(element, 'Save').click();
    await settle(fixture);

    expect(requestBodies('POST', 'admin/agents')).toEqual([jasmine.objectContaining({ id: 'profiled-agent', profile: 'project-chat' })]);
  });

  it('creates an agent with a data classification and sends it only when it changes', async () => {
    await configure(runScenario());
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);

    button(element, 'New agent').click();
    await settle(fixture);
    const select = element.querySelector('[data-agent-classification] select') as HTMLSelectElement;
    expect(Array.from(select.options).map(option => option.textContent?.trim())).toEqual(['Public', 'Internal', 'Confidential', 'Restricted']);
    expect(select.value).toBe('internal');
    expect(select.getAttribute('aria-describedby')).toContain('-classification-hint');
    type(element, 'input[id$="-id"]', 'figures-desk');
    type(element, 'input[id$="-name"]', 'Figures desk');
    type(element, 'textarea', 'Answer about the figures.');
    choose(element, '[data-agent-classification] select', 'confidential');
    fixture.detectChanges();
    button(element, 'Save').click();
    await settle(fixture);
    button(element, 'New agent').click();
    await settle(fixture);
    type(element, 'input[id$="-id"]', 'notes-desk');
    type(element, 'input[id$="-name"]', 'Notes desk');
    type(element, 'textarea', 'Write notes.');
    button(element, 'Save').click();
    await settle(fixture);

    const [confidential, standard] = requestBodies('POST', 'admin/agents') as Record<string, unknown>[];
    expect(confidential).toEqual(jasmine.objectContaining({ id: 'figures-desk', dataClassification: 'confidential' }));
    expect(Object.keys(standard)).not.toContain('dataClassification');
    const agents = await firstValueFrom(TestBed.inject(NhAssistantAdminApiService).getAgents());
    expect(agents.find(agent => agent.id === 'figures-desk')?.dataClassification).toBe('confidential');
    expect(agents.find(agent => agent.id === 'notes-desk')?.dataClassification).toBeNull();
  });

  it('shows the warning of an agent and a skill whose classification no profile permits, while their saves succeed', async () => {
    const script = runScenario();
    // The chat profile and its fallbacks permit internal data only.
    script.admin!.profileClassifications = { 'chat': ['public', 'internal'], 'project-chat': ['public', 'internal'] };
    await configure(script);
    const admin = TestBed.inject(NhAssistantAdminApiService);
    await firstValueFrom(admin.createAgent({
      id: 'vault-desk', displayName: 'Vault desk', description: 'Reads the vault.', instructions: 'Read the vault.',
      toolSelectors: ['sample-api.project.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'explain',
      dataClassification: 'restricted'
    }));
    await firstValueFrom(admin.saveRunSettings('vault-desk', { accountability: 'agent' }));
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);

    // The agent's card says that its model calls will be refused; so does its editor.
    const card = () => Array.from(element.querySelectorAll('.card')).find(item => item.textContent?.includes('Vault desk')) as HTMLElement;
    expect(card().querySelector('[data-agent-warning="classification-not-permitted"]')?.textContent)
      .toContain("Neither the agent's model profile nor the profile's fallbacks permit its data classification.");
    button(card(), 'Edit').click();
    await settle(fixture);
    expect(element.querySelector('[data-agent-editor-warning="classification-not-permitted"]')).not.toBeNull();
    button(element, 'Cancel').click();
    await settle(fixture);

    // A skill saved with a classification no profile permits is saved, with the warning next to it.
    (element.querySelector('[data-run-config-agent="vault-desk"]') as HTMLButtonElement).click();
    await settle(fixture);
    const view = () => element.querySelector('nh-assistant-agent-run-configuration') as HTMLElement;
    button(view(), 'Add skill').click();
    await settle(fixture);
    const skillForm = () => view().querySelector('[data-form="skill"]') as HTMLElement;
    type(skillForm(), 'input[id$="-skill-id"]', 'audit');
    type(skillForm(), 'input[id$="-skill-description"]', 'Audits the project.');
    await pick(fixture, skillForm(), 'input[id$="-skill-classification"]', 'conf', 'Confidential');
    button(skillForm(), 'Save').click();
    await settle(fixture);

    expect(view().querySelector('[data-saved]')?.textContent).toContain('The skill is added');
    expect(view().querySelector('[data-saved-warning="classification-not-permitted"]')?.textContent)
      .toContain("Neither the skill's model profile nor the profile's fallbacks permit its data classification.");
    const skill = view().querySelector('[data-data-skill="audit"]') as HTMLElement;
    expect(skill.querySelector('[data-skill-warning="classification-not-permitted"]')).not.toBeNull();
    button(skill, 'Edit').click();
    await settle(fixture);
    expect(skillForm().querySelector('[data-skill-form-warning="classification-not-permitted"]')).not.toBeNull();
  });

  it('refuses an invalid id without calling the server', async () => {
    await configure();
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);

    button(element, 'New agent').click();
    await settle(fixture);
    type(element, 'input[id$="-id"]', 'Not Valid');
    type(element, 'input[id$="-name"]', 'Name');
    button(element, 'Save').click();
    await settle(fixture);

    expect(element.textContent).toContain('Use lowercase letters, digits and single dashes.');
    expect(requestBodies('POST', 'admin/agents')).toEqual([]);
  });

  it('overrides and resets a code agent after confirmation, keeping its state', async () => {
    await configure();
    const admin = TestBed.inject(NhAssistantAdminApiService);
    const [agent] = await firstValueFrom(admin.getAgents());
    await firstValueFrom(admin.updateAgent(agent.id, { ...agent, instructions: 'Override.', expectedVersion: agent.version }));
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);

    expect(element.textContent).toContain('Overridden');
    button(element, 'Reset to code').click();
    fixture.detectChanges();
    expect(element.textContent).toContain('the agent stays active, paused or stopped as it is');
    button(element, 'Confirm reset').click();
    await settle(fixture);

    expect(element.textContent).not.toContain('Overridden');
    expect(requestBodies('POST', 'admin/agents/projects/reset').length).toBe(1);
  });

  it('lists the skills of an agent with their usage and stops one of them', async () => {
    const script = scenario();
    script.admin!.agents![0] = {
      ...script.admin!.agents![0],
      skills: [
        { skillId: 'task-follow-up', description: 'Follows up tasks by rule.' },
        { skillId: 'deadline-risk', description: 'Judges deadline risks.' }
      ]
    };
    await configure(script);
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const skill = () => element.querySelector('[data-skill="deadline-risk"]')!;

    expect(element.querySelectorAll('li.skill').length).toBe(2);
    expect(skill().textContent).toContain('Judges deadline risks.');
    expect(skill().textContent).toContain('Today 0 tool calls, 0 tokens');
    expect(skill().textContent).toContain('Active');

    (skill().querySelector('button.danger') as HTMLButtonElement).click();
    await settle(fixture);
    expect(element.textContent).toContain('The runs of the skill Deadline risk are cancelled');
    type(element, 'input[id$="-reason"]', 'Wrong risks');
    button(element, 'Confirm stop').click();
    await settle(fixture);

    expect(requestBodies('POST', 'admin/agents/projects/skills/deadline-risk/state')).toEqual([{ state: 'stopped', reason: 'Wrong risks' }]);
    expect(skill().textContent).toContain('Stopped');
    expect(skill().textContent).toContain('Note: Wrong risks');
    expect(element.querySelector('[data-skill="task-follow-up"]')?.textContent).toContain('Active');
    expect(element.querySelector('.card .card-header')?.textContent).toContain('Active');
  });

  it('puts an agent and a skill in shadow or live and changes the technical account, each with a reason', async () => {
    const script = scenario();
    script.admin!.agents![0] = {
      ...script.admin!.agents![0],
      serviceAccount: 'service-1',
      skills: [
        { skillId: 'task-follow-up', description: 'Follows up tasks by rule.' },
        { skillId: 'deadline-risk', description: 'Judges deadline risks.', shadow: true }
      ]
    };
    await configure(script);
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const card = () => element.querySelector('li.card')!;
    const skill = (id: string) => element.querySelector(`[data-skill="${id}"]`)!;
    const header = () => card().querySelector('.card-header')!;

    // A skill in shadow by code shows it and offers to go live; the agent offers shadow.
    expect(header().querySelector('[data-shadow-badge]')).toBeNull();
    expect(skill('deadline-risk').querySelector('[data-shadow-badge]')).not.toBeNull();
    expect(skill('deadline-risk').querySelector('[data-shadow-action="live"]')).not.toBeNull();
    expect(card().querySelector('[data-service-account]')?.textContent).toContain('Technical account: account ID service-1');

    (card().querySelector('[data-shadow-action="shadow"]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(element.textContent).toContain('New runs work in shadow threads that only administrators see');
    type(element, 'input[id$="-shadow-reason"]', 'Evaluate the rules');
    button(element, 'Confirm shadow').click();
    await settle(fixture);
    expect(requestBodies('POST', 'admin/agents/projects/shadow')).toEqual([{ mode: 'shadow', reason: 'Evaluate the rules' }]);
    expect(header().querySelector('[data-shadow-badge]')).not.toBeNull();
    expect(card().textContent).toContain('Note: Evaluate the rules');
    // Every skill of an agent in shadow works in shadow.
    expect(skill('task-follow-up').querySelector('[data-shadow-badge]')).not.toBeNull();

    (skill('deadline-risk').querySelector('[data-shadow-action="live"]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(element.textContent).toContain('The skill Deadline risk works live again, unless its agent works in shadow.');
    button(element, 'Confirm live').click();
    await settle(fixture);
    expect(requestBodies('POST', 'admin/agents/projects/skills/deadline-risk/shadow')).toEqual([{ mode: 'live', reason: null }]);
    expect(skill('deadline-risk').querySelector('[data-shadow-action="code"]')).not.toBeNull();

    (card().querySelector('[data-shadow-action="code"]') as HTMLButtonElement).click();
    await settle(fixture);
    button(element, 'Confirm').click();
    await settle(fixture);
    expect(requestBodies('POST', 'admin/agents/projects/shadow')[1]).toEqual({ mode: 'code', reason: null });
    expect(header().querySelector('[data-shadow-badge]')).toBeNull();
    expect(skill('deadline-risk').querySelector('[data-shadow-badge]')).toBeNull();

    // An account that does not exist is refused; another one answers for new runs.
    (card().querySelector('[data-account-action="set"]') as HTMLButtonElement).click();
    await settle(fixture);
    button(element, 'Confirm account').click();
    await settle(fixture);
    // A missing account shows at the account field, which describes itself with it.
    const accountError = card().querySelector('[data-account-error]') as HTMLElement;
    expect(accountError.textContent).toContain('Required.');
    const accountField = element.querySelector('input[id$="-account"]') as HTMLInputElement;
    expect(accountField.getAttribute('aria-invalid')).toBe('true');
    expect(accountField.getAttribute('aria-describedby')).toContain(accountError.id);
    expect(card().querySelector('[data-agent-error]')).toBeNull();
    expect(requestBodies('POST', 'admin/agents/projects/service-account')).toEqual([]);
    type(element, 'input[id$="-account"]', 'unknown-1');
    await settle(fixture);
    button(element, 'Confirm account').click();
    await settle(fixture);
    // The server's refusal of the account shows at the field too, and the form stays open.
    expect(card().querySelector('[data-account-error]')?.textContent).toContain('No account that may sign in exists with this id.');
    expect(card().querySelector('[data-account-form]')).not.toBeNull();

    (card().querySelector('[data-account-action="set"]') as HTMLButtonElement).click();
    await settle(fixture);
    type(element, 'input[id$="-account"]', 'service-2');
    type(element, 'input[id$="-account-reason"]', 'Rotate');
    await settle(fixture);
    button(element, 'Confirm account').click();
    await settle(fixture);
    expect(requestBodies('POST', 'admin/agents/projects/service-account').at(-1)).toEqual({ actorId: 'service-2', reason: 'Rotate' });
    expect(card().querySelector('[data-service-account]')?.textContent).toContain('Technical account: account ID service-2');
    expect(card().querySelector('[data-service-account]')?.textContent).toContain('In code: account ID service-1');

    (card().querySelector('[data-account-action="code"]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(element.textContent).toContain('New runs answer to the account in code again: account ID service-1.');
    button(element, 'Confirm').click();
    await settle(fixture);
    expect(requestBodies('POST', 'admin/agents/projects/service-account').at(-1)).toEqual({ actorId: null, reason: null });
    expect(card().querySelector('[data-service-account]')?.textContent).toContain('Technical account: account ID service-1');
    expect(card().querySelector('[data-account-action="code"]')).toBeNull();
  });

  it('compares an agent with its shadow variant, replays the period and offers evaluation only with a variant', async () => {
    const script = scenario();
    script.admin!.agents!.push({
      source: 'code', id: 'projects-v2', displayName: 'Project assistant v2', description: 'Projects', instructions: 'Other instructions.',
      toolSelectors: ['sample-api.project.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'execute', isEnabled: true,
      shadowOf: 'projects'
    });
    await configure(script);
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);

    // Only the live agent offers the evaluation; the variant itself has no variants.
    const actions = Array.from(element.querySelectorAll<HTMLButtonElement>('[data-evaluation-agent]'));
    expect(actions.map(action => action.dataset['evaluationAgent'])).toEqual(['projects']);
    actions[0].click();
    await settle(fixture);

    expect(element.textContent).toContain('Evaluation of shadow variants');
    expect(element.querySelector('[data-paired]')?.textContent?.trim()).toBe('0');
    // The mock answers only for the agent's own variant, so the report names it.
    expect(element.textContent).toContain('Small numbers mislead');
    expect(element.querySelector('select[id$="-variant"]')?.textContent).toContain('projects-v2');

    button(element, 'Replay the period').click();
    await settle(fixture);
    const [request] = requestBodies('POST', 'admin/agents/projects-v2/replay') as { sourceAgentId: string; maxRuns: number }[];
    expect(request.sourceAgentId).toBe('projects');
    expect(request.maxRuns).toBe(100);
    expect(element.querySelector('[data-replay="completed"]')?.textContent).toContain('0 of at most 100 runs replayed');

    button(element, 'Back to the agents').click();
    await settle(fixture);
    expect(document.activeElement).toBe(element.querySelector('[data-evaluation-agent="projects"]'));
  });

  it('pauses with a reason, stops and resumes an agent from the list', async () => {
    await configure();
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const card = () => element.querySelector('.card')!;

    expect(card().textContent).toContain('Active');
    button(element, 'Pause').click();
    await settle(fixture);
    expect(card().textContent).toContain('Events wait and run after the resume');
    expect(document.activeElement).toBe(element.querySelector('input[id$="-reason"]'));
    type(element, 'input[id$="-reason"]', 'Nightly import');
    button(element, 'Confirm pause').click();
    await settle(fixture);

    expect(requestBodies('POST', 'admin/agents/projects/state')).toEqual([{ state: 'paused', reason: 'Nightly import' }]);
    expect(card().textContent).toContain('Paused');
    expect(card().textContent).toContain('Note: Nightly import');
    expect(card().textContent).toContain('0 events wait for the resume.');

    button(element, 'Stop').click();
    await settle(fixture);
    button(element, 'Cancel').click();
    await settle(fixture);
    expect(requestBodies('POST', 'admin/agents/projects/state').length).toBe(1);

    button(element, 'Stop').click();
    await settle(fixture);
    button(element, 'Confirm stop').click();
    await settle(fixture);
    expect(card().textContent).toContain('Stopped');
    expect(buttons(element, 'Stop').length).toBe(0);
    // It was not off when it stopped, so releasing the kill switch makes it work again.
    const stoppedHint = card().querySelector('[data-stopped-hint]');
    expect(stoppedHint?.getAttribute('data-off-before-stop')).toBe('false');
    expect(stoppedHint?.textContent).toContain('releasing the kill switch makes it work again, as Resume does.');

    button(element, 'Resume').click();
    await settle(fixture);
    button(element, 'Confirm resume').click();
    await settle(fixture);

    expect(requestBodies('POST', 'admin/agents/projects/state').at(-1)).toEqual({ state: 'active', reason: null });
    expect(card().textContent).toContain('Active');
  });

  it('switches an agent and a skill off and on again, and creates an agent that starts off', async () => {
    const script = scenario();
    script.admin!.agents![0] = {
      ...script.admin!.agents![0],
      startsOff: true,
      skills: [{ skillId: 'deadline-risk', description: 'Judges deadline risks.', startsOff: true }]
    };
    await configure(script);
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const card = () => element.querySelector('.card')!;
    const skill = () => element.querySelector('[data-skill="deadline-risk"]')!;

    // Off in code: the agent says what that means, and offers to switch it on instead of a resume.
    expect(card().querySelector('.card-header')?.textContent).toContain('Off');
    expect(card().querySelector('[data-off-hint]')?.textContent).toContain('It started off, as its code says.');
    expect(buttons(element, 'Resume').length).toBe(0);
    (card().querySelector('.card-actions [data-state-action="active"]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(card().textContent).toContain('Nothing that happened while it was off runs.');
    button(element, 'Confirm switch on').click();
    await settle(fixture);
    expect(requestBodies('POST', 'admin/agents/projects/state')).toEqual([{ state: 'active', reason: null }]);
    expect(card().querySelector('.card-header')?.textContent).toContain('Active');

    (skill().querySelector('[data-state-action="active"]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(card().textContent).toContain('The skill Deadline risk takes work again.');
    button(element, 'Confirm switch on').click();
    await settle(fixture);
    expect(requestBodies('POST', 'admin/agents/projects/skills/deadline-risk/state')).toEqual([{ state: 'active', reason: null }]);

    (card().querySelector('.card-actions [data-state-action="off"]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(card().textContent).toContain('Events, schedules, webhooks, hand-offs and follow-ups are ignored');
    type(element, 'input[id$="-reason"]', 'Not yet');
    button(element, 'Confirm switch off').click();
    await settle(fixture);
    expect(requestBodies('POST', 'admin/agents/projects/state').at(-1)).toEqual({ state: 'off', reason: 'Not yet' });
    expect(card().querySelector('[data-off-hint]')?.textContent).toContain('People can still chat with it and start it by hand.');
    expect(card().textContent).toContain('Note: Not yet');

    // Stopped while it was off, the card says that releasing the kill switch switches it back off,
    // while resuming switches it on; once it works again the hint is gone.
    expect(card().querySelector('[data-stopped-hint]')).toBeNull();
    (card().querySelector('.card-actions [data-state-action="stopped"]') as HTMLButtonElement).click();
    await settle(fixture);
    button(element, 'Confirm stop').click();
    await settle(fixture);
    const stoppedHint = card().querySelector('[data-stopped-hint]');
    expect(stoppedHint?.getAttribute('data-off-before-stop')).toBe('true');
    expect(stoppedHint?.textContent).toContain('It was off when it stopped: releasing the kill switch switches it back off. Resume switches it on.');
    expect(await firstValueFrom(TestBed.inject(NhAssistantAdminApiService).getAgentState('projects')))
      .toEqual(jasmine.objectContaining({ runState: 'stopped', offBeforeStop: true }));
    (card().querySelector('.card-actions [data-state-action="active"]') as HTMLButtonElement).click();
    await settle(fixture);
    button(element, 'Confirm resume').click();
    await settle(fixture);
    expect(card().querySelector('.card-header')?.textContent).toContain('Active');
    expect(card().querySelector('[data-stopped-hint]')).toBeNull();

    // A new agent starts off when the administrator leaves it switched off.
    button(element, 'New agent').click();
    await settle(fixture);
    type(element, 'input[id$="-id"]', 'quiet-agent');
    type(element, 'input[id$="-name"]', 'Quiet agent');
    type(element, 'textarea', 'Help quietly.');
    (element.querySelector('[data-switch-on-now] input') as HTMLInputElement).click();
    fixture.detectChanges();
    button(element, 'Save').click();
    await settle(fixture);
    expect(requestBodies('POST', 'admin/agents')).toEqual([jasmine.objectContaining({ id: 'quiet-agent', startsOff: true })]);
    const created = Array.from(element.querySelectorAll('.card')).find(item => item.textContent?.includes('quiet-agent'))!;
    expect(created.querySelector('.card-header')?.textContent).toContain('Off');
  });

  it('gives an administration agent run settings with errors at their fields, a skill that starts in shadow and a trigger it puts live', async () => {
    const script = runScenario();
    script.admin!.accounts = [
      { actorId: 'service-1', displayName: 'Operations account', detail: 'operations@example.test' },
      { actorId: 'service-2', displayName: 'Finance account', detail: 'finance@example.test' }
    ];
    await configure(script);
    await firstValueFrom(TestBed.inject(NhAssistantAdminApiService).createAgent({
      id: 'follower', displayName: 'Project follower', description: 'Follows up new projects.', instructions: 'Follow up.',
      toolSelectors: ['sample-api.project.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'execute'
    }));
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    (element.querySelector('[data-run-config-agent="follower"]') as HTMLButtonElement).click();
    await settle(fixture);
    const view = () => element.querySelector('nh-assistant-agent-run-configuration') as HTMLElement;
    const form = () => view().querySelector('[data-form="settings"]') as HTMLElement;

    // It only chats until it has run settings; the guided setup is offered first.
    expect(document.activeElement).toBe(button(element, 'Back to agents'));
    expect(view().querySelector('[data-setup-offer]')?.textContent).toContain('This agent only chats.');
    expect(button(view(), 'Add skill').disabled).toBeTrue();
    expect(button(view(), 'Add trigger').disabled).toBeTrue();

    // Errors are checked before sending and shown at their fields and next to the actions.
    button(view(), 'Add run settings').click();
    await settle(fixture);
    type(form(), 'input[id$="-daily-calls"]', '100');
    button(form(), 'Save').click();
    await settle(fixture);
    expect(requestBodies('PUT', 'admin/agents/follower/run-settings')).toEqual([]);
    expect(form().querySelector('input[id$="-service-account"]')?.getAttribute('aria-invalid')).toBe('true');
    expect(form().textContent).toContain('Required.');
    expect(form().textContent).toContain('Fill in both daily limits, or neither.');
    expect(form().querySelector('[data-form-error]')?.textContent).toContain('Check the marked fields.');
    expect(document.activeElement).toBe(form().querySelector('input[id$="-service-account"]'));

    // The account is found by name; the policy explains itself.
    await pick(fixture, form(), 'input[id$="-service-account"]', 'oper', 'Operations account');
    type(form(), 'input[id$="-daily-tokens"]', '20000');
    await pick(fixture, form(), 'input[id$="-policy"]', 'review', 'project-review');
    expect(form().textContent).toContain('Approved by an approver of the subject.');
    await pick(fixture, form(), 'input[id$="-rule-handler"]', 'kick', 'project-kickoff');
    // A new agent accepts no files until the administrator switches them on.
    expect((form().querySelector('[data-accepts-files] input') as HTMLInputElement).checked).toBeFalse();
    expect(form().querySelector('[data-accepts-files]')?.textContent).toContain('Accepts files');
    check(form(), '[data-accepts-files] input');
    button(form(), 'Save').click();
    await settle(fixture);
    expect(requestBodies('PUT', 'admin/agents/follower/run-settings').at(-1)).toEqual(jasmine.objectContaining({
      accountability: 'service-account', serviceAccountActorId: 'service-1', ruleHandler: 'project-kickoff',
      approvalPolicy: 'project-review', dailyToolCalls: 100, dailyTokens: 20000, expectedVersion: null, acceptsFiles: true
    }));
    expect(view().textContent).toContain('The run settings are saved.');
    const summary = view().querySelector('[data-settings-summary]') as HTMLElement;
    expect(summary.querySelector('[data-account-name]')?.textContent).toContain('Operations account');
    // The name only: the account's ID is its tooltip, not text next to it.
    expect(summary.querySelector('[data-account-name]')?.textContent).not.toContain('service-1');
    expect(summary.querySelector('[data-account-label-name]')?.getAttribute('title')).toBe('Account ID: service-1');
    expect(summary.textContent).toContain('A proposal waits 7 days.');

    // Advanced options stay folded while they keep their defaults.
    button(view(), 'Change run settings').click();
    await settle(fixture);
    expect(form().querySelector('[data-advanced]')?.hasAttribute('open')).toBeFalse();
    button(form(), 'Cancel').click();
    await settle(fixture);

    // A new skill starts in shadow; the way of working is chosen from the rules in code.
    button(view(), 'Add skill').click();
    await settle(fixture);
    const skillForm = () => view().querySelector('[data-form="skill"]') as HTMLElement;
    expect(skillForm().textContent).toContain('A new skill starts in shadow.');
    // The agent's tool pattern says how many tools it offers now.
    expect(skillForm().textContent).toContain('All tools that match: 2 now.');
    type(skillForm(), 'input[id$="-skill-id"]', 'Kick off');
    button(skillForm(), 'Save').click();
    await settle(fixture);
    expect(skillForm().textContent).toContain('Use lowercase letters, digits and dashes, starting with a letter.');
    expect(skillForm().textContent).toContain('Required.');
    type(skillForm(), 'input[id$="-skill-id"]', 'kickoff');
    type(skillForm(), 'input[id$="-skill-description"]', 'Starts projects that were created as drafts.');
    check(skillForm(), 'input[type="checkbox"]');
    await pick(fixture, skillForm(), 'input[id$="-skill-rules"]', 'kick', 'project-kickoff');
    // The skill reads more sensitive data than the agent: its model calls carry its own classification.
    await pick(fixture, skillForm(), 'input[id$="-skill-classification"]', 'conf', 'Confidential');
    // The skill reads the documents its agent accepts.
    check(skillForm(), '[data-accepts-files] input');
    button(skillForm(), 'Save').click();
    await settle(fixture);
    expect(requestBodies('PUT', 'admin/agents/follower/skills/kickoff')).toEqual([jasmine.objectContaining({
      description: 'Starts projects that were created as drafts.', tools: ['sample-api.project.*'], ruleHandler: 'project-kickoff',
      dataClassification: 'confidential', acceptsFiles: true, expectedVersion: null
    })]);
    expect(view().querySelector('[data-data-skill="kickoff"]')?.textContent).toContain('Version 1');

    // A trigger on the application's event starts in shadow until it is put live.
    button(view(), 'Add trigger').click();
    await settle(fixture);
    const triggerForm = () => view().querySelector('[data-form="trigger"]') as HTMLElement;
    type(triggerForm(), 'input[id$="-trigger-id"]', 'project-created');
    button(triggerForm(), 'Save').click();
    await settle(fixture);
    expect(triggerForm().querySelector('input[id$="-trigger-event"]')?.getAttribute('aria-invalid')).toBe('true');
    await pick(fixture, triggerForm(), 'input[id$="-trigger-event"]', 'created', 'project.created');
    await pick(fixture, triggerForm(), 'input[id$="-trigger-skill"]', 'kick', 'Kickoff');
    button(triggerForm(), 'Save').click();
    await settle(fixture);
    expect(requestBodies('PUT', 'admin/agents/follower/triggers/project-created')).toEqual([jasmine.objectContaining({
      source: 'event', event: 'project.created', skill: 'kickoff', conflict: 'coalesce', debounceSeconds: 120, expectedVersion: null
    })]);
    const trigger = () => view().querySelector('[data-data-trigger="project-created"]') as HTMLElement;
    expect(trigger().querySelector('[data-shadow-badge]')).not.toBeNull();
    (trigger().querySelector('[data-trigger-shadow="live"]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(requestBodies('POST', 'admin/agents/follower/triggers/project-created/shadow')).toEqual([{ mode: 'live', reason: null }]);
    expect(trigger().querySelector('[data-shadow-badge]')).toBeNull();

    // The run settings stay while skills and triggers belong to the agent; the error shows there.
    button(view(), 'Remove run settings').click();
    await settle(fixture);
    button(view(), 'Confirm delete').click();
    await settle(fixture);
    expect(view().querySelector('[data-run-settings] [role="alert"]')?.textContent).toContain('Skills or triggers still belong to this agent.');

    // Back in the list, the skill shows among the agent's skills, in shadow.
    button(element, 'Back to agents').click();
    await settle(fixture);
    const skill = element.querySelector('[data-skill="kickoff"]') as HTMLElement;
    expect(skill.querySelector('[data-shadow-badge]')).not.toBeNull();
    expect(document.activeElement).toBe(element.querySelector('[data-run-config-agent="follower"]'));
  }, formFlowTimeout);

  it('lowers the file limits of an administration agent and its skill within the application\'s, with errors at their fields', async () => {
    const script = runScenario();
    script.admin!.runCatalog!.fileLimits = { maxFileBytes: 10 * 1024 * 1024, maxFilePages: 50, maxFilesPerTurn: 3, maxFileTokensPerTurn: 50_000 };
    await configure(script);
    await firstValueFrom(TestBed.inject(NhAssistantAdminApiService).createAgent({
      id: 'reader', displayName: 'Document reader', description: 'Reads documents.', instructions: 'Read documents.',
      toolSelectors: ['sample-api.project.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'execute'
    }));
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    (element.querySelector('[data-run-config-agent="reader"]') as HTMLButtonElement).click();
    await settle(fixture);
    const view = () => element.querySelector('nh-assistant-agent-run-configuration') as HTMLElement;
    const form = () => view().querySelector('[data-form="settings"]') as HTMLElement;
    button(view(), 'Add run settings').click();
    await settle(fixture);
    check(form(), 'input[type="radio"][value="agent"]');

    // The limits appear once the agent accepts files, each naming the application's limit.
    expect(form().querySelector('[data-file-limits]')).toBeNull();
    check(form(), '[data-accepts-files] input');
    await settle(fixture);
    const limits = form().querySelector('[data-file-limits]') as HTMLElement;
    expect(limits.textContent).toContain('Lower file limits');
    expect(limits.querySelector('[data-file-limit="maxFileBytes"]')?.textContent).toContain('The application allows at most 10 MB.');
    expect(limits.querySelector('[data-file-limit="maxFilesPerTurn"]')?.textContent).toContain('The application allows at most 3.');

    // A value above the application's, or not positive, is refused at its field before sending.
    type(form(), 'input[id$="-file-size"]', '30');
    type(form(), 'input[id$="-file-pages"]', '0');
    button(form(), 'Save').click();
    await settle(fixture);
    expect(requestBodies('PUT', 'admin/agents/reader/run-settings')).toEqual([]);
    expect(form().querySelector('input[id$="-file-size"]')?.getAttribute('aria-invalid')).toBe('true');
    expect(form().textContent).toContain('Enter a size above 0 and at most 10 MB.');
    expect(form().textContent).toContain('Enter a whole number from 1 to 50.');
    expect(document.activeElement).toBe(form().querySelector('input[id$="-file-size"]'));

    type(form(), 'input[id$="-file-size"]', '2.5');
    type(form(), 'input[id$="-file-pages"]', '10');
    type(form(), 'input[id$="-files-per-turn"]', '2');
    button(form(), 'Save').click();
    await settle(fixture);
    expect(requestBodies('PUT', 'admin/agents/reader/run-settings').at(-1)).toEqual(jasmine.objectContaining({
      accountability: 'agent', acceptsFiles: true,
      maxFileBytes: 2.5 * 1024 * 1024, maxFilePages: 10, maxFilesPerTurn: 2, maxFileTokensPerTurn: null
    }));

    // The saved limits come back into the form; without files the agent sends none.
    button(view(), 'Change run settings').click();
    await settle(fixture);
    expect((form().querySelector('input[id$="-file-size"]') as HTMLInputElement).value).toBe('2.5');
    expect((form().querySelector('input[id$="-files-per-turn"]') as HTMLInputElement).value).toBe('2');
    check(form(), '[data-accepts-files] input');
    await settle(fixture);
    expect(form().querySelector('[data-file-limits]')).toBeNull();
    button(form(), 'Save').click();
    await settle(fixture);
    expect(requestBodies('PUT', 'admin/agents/reader/run-settings').at(-1)).toEqual(jasmine.objectContaining({
      acceptsFiles: false, maxFileBytes: null, maxFilePages: null, maxFilesPerTurn: null, maxFileTokensPerTurn: null
    }));

    // A skill lowers the agent's limits the same way.
    button(view(), 'Add skill').click();
    await settle(fixture);
    const skillForm = () => view().querySelector('[data-form="skill"]') as HTMLElement;
    type(skillForm(), 'input[id$="-skill-id"]', 'audit');
    type(skillForm(), 'input[id$="-skill-description"]', 'Audits the project documents.');
    check(skillForm(), '[data-accepts-files] input');
    await settle(fixture);
    expect(skillForm().querySelector('[data-file-limits]')?.textContent).toContain('keep the agent\'s limit');
    type(skillForm(), 'input[id$="-files-per-turn"]', '4');
    button(skillForm(), 'Save').click();
    await settle(fixture);
    expect(skillForm().textContent).toContain('Enter a whole number from 1 to 3.');
    type(skillForm(), 'input[id$="-files-per-turn"]', '1');
    button(skillForm(), 'Save').click();
    await settle(fixture);
    expect(requestBodies('PUT', 'admin/agents/reader/skills/audit')).toEqual([jasmine.objectContaining({
      acceptsFiles: true, maxFilesPerTurn: 1, maxFileBytes: null
    })]);
  });

  it('creates an agent that works on its own, out of the chat, through the guided setup', async () => {
    const script = runScenario();
    script.admin!.accounts = [{ actorId: 'service-1', displayName: 'Operations account', detail: 'operations@example.test' }];
    await configure(script);
    const store = TestBed.inject(NhAssistantStore);
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);

    button(element, 'New agent').click();
    await settle(fixture);
    type(element, 'input[id$="-id"]', 'starter');
    type(element, 'input[id$="-name"]', 'Project starter');
    check(element, '[data-agent-use] input[value="background"]');
    await settle(fixture);
    expect(element.textContent).toContain('After saving, a short setup asks when the agent starts');
    type(element, 'input.mono[aria-label]', 'sample-api.project.*');
    await settle(fixture);
    (element.querySelector('.inline button') as HTMLButtonElement).click();
    await settle(fixture);
    button(element, 'Save').click();
    await settle(fixture);
    expect(requestBodies('POST', 'admin/agents').at(-1)).toEqual(jasmine.objectContaining({ id: 'starter', offeredInChat: false }));
    await store.reloadStatus();
    expect(store.agents().map(agent => agent.id)).not.toContain('starter');

    // The run configuration opens with the guided setup.
    const setup = () => element.querySelector('[data-work-setup]') as HTMLElement;
    expect(setup()).not.toBeNull();
    expect(setup().querySelector('[aria-current="step"]')?.textContent).toContain('When');
    button(setup(), 'Next').click();
    await settle(fixture);
    expect(setup().querySelector('input[id$="-event"]')?.getAttribute('aria-invalid')).toBe('true');
    expect(setup().querySelector('[data-form-error]')?.textContent).toContain('Check the marked fields.');
    await pick(fixture, setup(), 'input[id$="-event"]', 'created', 'project.created');
    button(setup(), 'Next').click();
    await settle(fixture);

    type(setup(), 'input[id$="-name"]', 'Start new projects');
    type(setup(), 'input[id$="-description"]', 'Starts projects that were created as drafts.');
    await settle(fixture);
    expect(setup().textContent).toContain('Its id becomes start-new-projects.');
    button(setup(), 'Next').click();
    await settle(fixture);
    // Without fixed rules the model needs instructions.
    expect(setup().textContent).toContain('Without fixed rules the model works from these instructions');
    await pick(fixture, setup(), 'input[id$="-rules"]', 'kick', 'project-kickoff');
    button(setup(), 'Next').click();
    await settle(fixture);

    await pick(fixture, setup(), 'input[id$="-account"]', 'oper', 'Operations account');
    button(setup(), 'Next').click();
    await settle(fixture);
    await pick(fixture, setup(), 'input[id$="-policy"]', 'review', 'project-review');
    button(setup(), 'Next').click();
    await settle(fixture);
    expect(setup().querySelector('[data-step="review"]')?.textContent).toContain('When project.created happens');
    button(setup(), 'Save in shadow').click();
    await settle(fixture);

    expect(requestBodies('PUT', 'admin/agents/starter/run-settings').at(-1)).toEqual(jasmine.objectContaining({
      accountability: 'service-account', serviceAccountActorId: 'service-1', approvalPolicy: 'project-review'
    }));
    expect(requestBodies('PUT', 'admin/agents/starter/skills/start-new-projects').at(-1)).toEqual(jasmine.objectContaining({
      description: 'Starts projects that were created as drafts.', ruleHandler: 'project-kickoff', tools: ['sample-api.project.*']
    }));
    expect(requestBodies('PUT', 'admin/agents/starter/triggers/start-new-projects-project-created').at(-1)).toEqual(jasmine.objectContaining({
      source: 'event', event: 'project.created', skill: 'start-new-projects'
    }));
    const view = element.querySelector('nh-assistant-agent-run-configuration') as HTMLElement;
    expect(view.textContent).toContain('The agent works in shadow now');
    expect(view.querySelector('[data-data-trigger="start-new-projects-project-created"] [data-shadow-badge]')).not.toBeNull();

    button(element, 'Back to agents').click();
    await settle(fixture);
    expect(element.querySelector('[data-hidden-from-chat]')?.textContent).toContain('Not in the chat');
  });

  it('evaluates a changed skill of a code agent as a candidate and promotes it; skills and triggers in code stay read only', async () => {
    const script = runScenario();
    script.admin!.agents![0] = { ...script.admin!.agents![0], skills: [{ skillId: 'task-follow-up', description: 'Follows up tasks by rule.' }] };
    script.admin!.codeTriggers = [{ agentId: 'projects', triggerId: 'task-completed', source: 'event', skillId: 'task-follow-up' }];
    await configure(script);
    const admin = TestBed.inject(NhAssistantAdminApiService);
    await firstValueFrom(admin.saveDataSkill('projects', 'progress-note', { description: 'Notes progress.', ruleHandler: 'project-progress-note' }));
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    (element.querySelector('[data-run-config-agent="projects"]') as HTMLButtonElement).click();
    await settle(fixture);
    const view = () => element.querySelector('nh-assistant-agent-run-configuration') as HTMLElement;
    const skill = () => view().querySelector('[data-data-skill="progress-note"]') as HTMLElement;

    const codeSkill = view().querySelector('[data-code-skill="task-follow-up"]') as HTMLElement;
    expect(codeSkill.textContent).toContain('Code');
    expect(codeSkill.querySelector('button')).toBeNull();
    expect(view().querySelector('[data-code-skill="progress-note"]')).toBeNull();
    expect((view().querySelector('[data-code-trigger="task-completed"]') as HTMLElement).textContent).toContain('Skill: Task follow up');
    expect(view().querySelector('[data-run-settings]')).toBeNull();

    // A change is evaluated in shadow by default.
    button(skill(), 'Edit').click();
    await settle(fixture);
    expect((view().querySelector('[data-apply] input[value="shadow"]') as HTMLInputElement).checked).toBeTrue();
    type(view(), 'input[id$="-skill-description"]', 'Notes progress as a percentage.');
    button(view(), 'Save').click();
    await settle(fixture);
    expect(requestBodies('PUT', 'admin/agents/projects/skills/progress-note').at(-1)).toEqual(jasmine.objectContaining({
      description: 'Notes progress as a percentage.', expectedVersion: 1, apply: 'shadow'
    }));
    expect(view().textContent).toContain('The new version runs in shadow next to the live version.');
    expect(skill().querySelector('[data-candidate-badge]')?.textContent).toContain('Candidate v2');
    expect(skill().textContent).toContain('Version 1');

    button(skill(), 'Compare').click();
    await settle(fixture);
    const comparison = skill().querySelector('[data-comparison]') as HTMLElement;
    expect(comparison.textContent).toContain('Live (v1)');
    expect(comparison.textContent).toContain('Candidate (v2)');

    button(skill(), 'Promote').click();
    await settle(fixture);
    button(skill(), 'Confirm promote').click();
    await settle(fixture);
    expect(requestBodies('POST', 'admin/agents/projects/skills/progress-note/candidate/promote')).toEqual([{ version: 2, reason: null }]);
    expect(view().textContent).toContain('The candidate is the live version now.');
    expect(skill().querySelector('[data-candidate-badge]')).toBeNull();
    expect(skill().textContent).toContain('Version 2');

    button(skill(), 'Versions').click();
    await settle(fixture);
    const versions = skill().querySelector('[data-versions]') as HTMLElement;
    expect(versions.textContent).toContain('Live');
    expect(versions.textContent).toContain('Earlier');
  });

  it('names agents in code, their skills and policies in the language of the user and keeps the keys of an agent in code', async () => {
    const script = scenario();
    script.admin!.agents![0] = {
      ...script.admin!.agents![0],
      displayName: 'agents.projects.name',
      description: 'agents.projects.description',
      requiredPolicy: 'app.project.view',
      skills: [{ skillId: 'task-follow-up', description: 'Follows up tasks by rule.' }]
    };
    await configure(script);
    TestBed.inject(TranslateService).setTranslation('en', {
      agents: { projects: { name: 'Project assistant', description: 'Answers questions about projects.' } },
      'nh-assistant': {
        agents: { projects: { skills: { 'task-follow-up': { description: 'Follows up completed tasks.' } } } },
        policies: { 'app.project.view': 'viewing projects' }
      }
    }, true);
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const card = () => element.querySelector('.card')!;

    expect(card().textContent).toContain('Project assistant');
    expect(card().textContent).toContain('Answers questions about projects.');
    expect(card().textContent).not.toContain('agents.projects');
    expect(card().querySelector('[data-required-policy]')?.textContent?.replace(/\s+/g, ' ').trim()).toBe('Requires: viewing projects');
    expect(card().textContent).toContain('1 tool selector');
    expect(card().textContent).not.toContain('1 tool selectors');
    const skill = card().querySelector('[data-skill="task-follow-up"]')!;
    expect(skill.querySelector('strong')?.textContent?.trim()).toBe('Task follow up');
    expect(skill.textContent).toContain('Follows up completed tasks.');

    // The editor shows the translations and sends the keys back while they are kept.
    button(element, 'Edit').click();
    await settle(fixture);
    expect((element.querySelector('input[id$="-name"]') as HTMLInputElement).value).toBe('Project assistant');
    expect((element.querySelector('input[id$="-description"]') as HTMLInputElement).value).toBe('Answers questions about projects.');
    button(element, 'Save').click();
    await settle(fixture);
    expect(requestBodies('PUT', 'admin/agents/projects')).toEqual([jasmine.objectContaining({
      displayName: 'agents.projects.name',
      description: 'agents.projects.description'
    })]);

    // A changed text is the administrator's own.
    button(element, 'Edit').click();
    await settle(fixture);
    type(element, 'input[id$="-name"]', 'Projects helper');
    button(element, 'Save').click();
    await settle(fixture);
    expect(requestBodies('PUT', 'admin/agents/projects')[1]).toEqual(jasmine.objectContaining({
      displayName: 'Projects helper',
      description: 'agents.projects.description'
    }));
  });

  it('shows a policy without a translation as it is called in code and explains notes', async () => {
    const script = scenario();
    script.admin!.agents![0] = { ...script.admin!.agents![0], requiredPolicy: 'app.project.view' };
    await configure(script);
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);

    expect(element.querySelector('[data-required-policy] code')?.textContent).toBe('app.project.view');
    button(element, 'Pause').click();
    await settle(fixture);
    const note = element.querySelector('input[id$="-reason"]') as HTMLInputElement;
    expect(element.querySelector(`label[for="${note.id}"]`)?.textContent).toContain('Note for other administrators (optional)');
    expect(element.querySelector(`#${note.getAttribute('aria-describedby')}`)?.textContent).toContain('The agent does not read it.');
  });

  it('offers an agent created here its own setting instead of the code to return to', async () => {
    await configure();
    const { fixture } = await render(NhAssistantAdminAgentsComponent);
    const list = fixture.componentInstance;
    const shadow = (configured: boolean) => ({ shadow: false, configured, override: false, locked: false, changedAt: null, changedBy: null, reason: null });
    const [codeAgent] = list.agents();
    const created = {
      ...codeAgent,
      source: 'admin' as const,
      shadow: shadow(true),
      skills: [{ ...(codeAgent.skills?.[0] ?? {}), skillId: 'kickoff', description: 'Starts projects.', shadow: shadow(true) }]
    } as typeof codeAgent;
    const translate = TestBed.inject(TranslateService);

    expect(translate.instant(list.shadowActionKey(codeAgent, 'code'))).toBe('Follow the code');
    expect(translate.instant(list.shadowActionKey(created, 'code'))).toBe('Back to its own setting');
    expect(translate.instant(list.shadowExplainKey(created, 'code', true), {
      skill: 'Kickoff',
      mode: translate.instant(list.ownShadowModeKey(created, 'kickoff'))
    })).toBe('The own setting of the skill Kickoff decides again: in shadow.');
    expect(translate.instant(list.shadowExplainKey(created, 'live', false))).toContain('New runs work live');
  });

  it('deletes an admin agent only after confirmation', async () => {
    await configure();
    await firstValueFrom(TestBed.inject(NhAssistantAdminApiService).createAgent({
      id: 'temporary', displayName: 'Temporary', description: '', instructions: '', toolSelectors: [], mcpServerIds: [],
      requiredPolicy: null, autonomy: 'observe'
    }));
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);

    button(element, 'Delete').click();
    fixture.detectChanges();
    expect(requestBodies('DELETE', 'admin/agents/temporary')).toEqual([]);

    button(element, 'Confirm delete').click();
    await settle(fixture);

    expect(requestBodies('DELETE', 'admin/agents/temporary').length).toBe(1);
    expect(element.textContent).not.toContain('Temporary');
  });
});

describe('NhAssistantAdminMcpServersComponent', () => {
  it('never renders the stored secret and keeps it on a plain save', async () => {
    await configure();
    const { fixture, element } = await render(NhAssistantAdminMcpServersComponent);

    expect(element.textContent).toContain('Secret set');
    button(element, 'Edit').click();
    await settle(fixture);

    const values = Array.from(element.querySelectorAll('input')).map(input => (input as HTMLInputElement).value);
    expect(values).not.toContain(secretValue);
    expect(element.innerHTML).not.toContain(secretValue);
    expect(element.textContent).toContain('A secret is set.');
    expect(element.querySelector('input[type="password"]')).toBeNull();

    button(element, 'Save').click();
    await settle(fixture);

    const [body] = requestBodies('PUT', 'admin/mcp-servers/files') as object[];
    expect(Object.keys(body)).not.toContain('secret');
  });

  it('replaces the secret explicitly and removes it only with a mode that needs none', async () => {
    await configure();
    const { fixture, element } = await render(NhAssistantAdminMcpServersComponent);

    button(element, 'Edit').click();
    await settle(fixture);
    // A bearer token always needs a secret, so there is nothing to clear.
    expect(buttons(element, 'Clear')).toEqual([]);
    button(element, 'Replace').click();
    fixture.detectChanges();
    const password = element.querySelector('input[type="password"]') as HTMLInputElement;
    expect(password.value).toBe('');
    expect(password.getAttribute('autocomplete')).toBe('new-password');
    type(element, 'input[type="password"]', 'new-secret');
    button(element, 'Save').click();
    await settle(fixture);

    button(element, 'Edit').click();
    await settle(fixture);
    choose(element, 'select[id$="-auth"]', 'none');
    fixture.detectChanges();
    button(element, 'Save').click();
    await settle(fixture);

    const bodies = requestBodies('PUT', 'admin/mcp-servers/files') as { secret?: string }[];
    expect(bodies.map(body => body.secret)).toEqual(['new-secret', '']);
    expect(element.textContent).toContain('No authentication');
    expect(element.textContent).not.toContain('Secret set');
  });

  it('tests the connection and shows the result', async () => {
    await configure();
    const { fixture, element } = await render(NhAssistantAdminMcpServersComponent);
    expect(element.textContent).toContain('Not connected yet');

    button(element, 'Test connection').click();
    await settle(fixture);

    expect(element.querySelector('[role="status"]')?.textContent).toContain('The connection works: 2 tools.');
    expect(element.textContent).toContain('Last connection');
    expect(element.textContent).not.toContain('Not connected yet');
  });

  it('offers background runs with the organization credential, or with the person\'s own account for a forwarded token', async () => {
    await configure();
    const { fixture, element } = await render(NhAssistantAdminMcpServersComponent);
    expect(element.textContent).toContain('Not in background runs');

    button(element, 'Edit').click();
    await settle(fixture);
    const headless = element.querySelector('select[id$="-headless"]') as HTMLSelectElement;
    expect(headless.disabled).toBeFalse();
    expect(headless.value).toBe('never');
    headless.value = 'organization';
    headless.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    button(element, 'Save').click();
    await settle(fixture);

    expect(element.textContent).toContain('Background runs with this credential');

    button(element, 'Edit').click();
    await settle(fixture);
    const auth = element.querySelector('select[id$="-auth"]') as HTMLSelectElement;
    auth.value = 'forward-user-token';
    auth.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    const personal = element.querySelector('select[id$="-headless"]') as HTMLSelectElement;
    // The organization's credential does not exist for a forwarded token.
    expect(Array.from(personal.options).map(option => option.value)).toEqual(['never', 'personal']);
    expect(personal.value).toBe('never');
    expect(element.textContent).toContain('only after that person allowed it in their preferences');
    button(element, 'Save').click();
    await settle(fixture);

    button(element, 'Edit').click();
    await settle(fixture);
    choose(element, 'select[id$="-headless"]', 'personal');
    fixture.detectChanges();
    button(element, 'Save').click();
    await settle(fixture);

    const bodies = requestBodies('PUT', 'admin/mcp-servers/files') as { headlessUse?: string }[];
    expect(bodies.map(body => body.headlessUse)).toEqual(['organization', 'never', 'personal']);
    expect(element.textContent).toContain("Background runs with the person's own account, after consent");
  });

  it('asks a user name for basic authentication and keeps the password write-only', async () => {
    await configure();
    const { fixture, element } = await render(NhAssistantAdminMcpServersComponent);

    button(element, 'Edit').click();
    await settle(fixture);
    choose(element, 'select[id$="-auth"]', 'basic');
    fixture.detectChanges();
    button(element, 'Save').click();
    await settle(fixture);
    expect(element.textContent).toContain('Enter a user name without a colon');
    expect(requestBodies('PUT', 'admin/mcp-servers/files')).toEqual([]);

    type(element, 'input[id$="-user"]', 'planner');
    button(element, 'Save').click();
    await settle(fixture);

    const [body] = requestBodies('PUT', 'admin/mcp-servers/files') as Record<string, unknown>[];
    expect(body['authMode']).toBe('basic');
    expect(body['userName']).toBe('planner');
    expect(Object.keys(body)).not.toContain('secret');
    expect(element.textContent).toContain('User name and password');
    expect(element.textContent).toContain('Secret set');
    expect(element.innerHTML).not.toContain(secretValue);
  });

  it('refers to a secret of the application instead of storing one', async () => {
    const script = scenario();
    script.admin!.secretReferences = ['config:files-token'];
    await configure(script);
    const { fixture, element } = await render(NhAssistantAdminMcpServersComponent);

    button(element, 'Edit').click();
    await settle(fixture);
    check(element, 'input[type="radio"][value="reference"]');
    fixture.detectChanges();
    expect(element.querySelector('input[type="password"]')).toBeNull();
    type(element, 'input[id$="-secret-reference"]', 'config:../files');
    button(element, 'Save').click();
    await settle(fixture);
    expect(element.textContent).toContain('Enter a reference such as config:planning-token.');

    type(element, 'input[id$="-secret-reference"]', 'vault:files');
    button(element, 'Save').click();
    await settle(fixture);
    // A reference the application cannot resolve shows at its field, which takes the focus.
    const reference = element.querySelector('input[id$="-secret-reference"]') as HTMLInputElement;
    expect(reference.getAttribute('aria-invalid')).toBe('true');
    expect(element.querySelector(`#${reference.getAttribute('aria-describedby')!.split(' ').at(-1)}`)?.textContent)
      .toContain('The application does not know this name.');
    expect(document.activeElement).toBe(reference);

    type(element, 'input[id$="-secret-reference"]', 'config:files-token');
    button(element, 'Save').click();
    await settle(fixture);

    const bodies = requestBodies('PUT', 'admin/mcp-servers/files') as Record<string, unknown>[];
    expect(bodies.map(body => body['secretReference'])).toEqual(['vault:files', 'config:files-token']);
    expect(bodies.every(body => !('secret' in body))).toBeTrue();
    expect(element.textContent).toContain('Secret from config:files-token');
  });

  it('names a client certificate and chooses the HTTP+SSE fallback', async () => {
    const script = scenario();
    script.admin!.clientCertificates = ['files-client'];
    await configure(script);
    const { fixture, element } = await render(NhAssistantAdminMcpServersComponent);

    button(element, 'Edit').click();
    await settle(fixture);
    type(element, 'input[id$="-certificate"]', 'unknown-client');
    button(element, 'Save').click();
    await settle(fixture);
    const certificate = element.querySelector('input[id$="-certificate"]') as HTMLInputElement;
    expect(certificate.getAttribute('aria-invalid')).toBe('true');
    expect(certificate.closest('.field')?.querySelector('.field-error')?.textContent).toContain('The application does not know this name.');

    type(element, 'input[id$="-certificate"]', 'files-client');
    choose(element, 'select[id$="-transport"]', 'auto');
    button(element, 'Save').click();
    await settle(fixture);

    const bodies = requestBodies('PUT', 'admin/mcp-servers/files') as Record<string, unknown>[];
    expect(bodies[bodies.length - 1]).toEqual(jasmine.objectContaining({ clientCertificateReference: 'files-client', transport: 'auto' }));
    expect(element.textContent).toContain('Client certificate files-client');
    expect(element.textContent).toContain('HTTP+SSE fallback');
  });

  it('syncs tools disabled as changes, shows hints as hints and enables a tool as read-only', async () => {
    await configure();
    const { fixture, element } = await render(NhAssistantAdminMcpServersComponent);

    button(element, 'Tools').click();
    await settle(fixture);
    button(element, 'Sync tools').click();
    await settle(fixture);

    const cards = Array.from(element.querySelectorAll('.card')) as HTMLElement[];
    const selects = Array.from(element.querySelectorAll('.card select')) as HTMLSelectElement[];
    const toggles = Array.from(element.querySelectorAll('.card input[type="checkbox"]')) as HTMLInputElement[];
    expect(cards.length).toBe(2);
    expect(toggles.map(toggle => toggle.checked)).toEqual([false, false]);
    expect(selects.map(select => select.value)).toEqual(['mutation', 'mutation']);
    expect(cards[0].textContent).toContain('Server hint: read only');

    toggles[0].click();
    selects[0].value = 'read-only';
    selects[0].dispatchEvent(new Event('change'));
    fixture.detectChanges();
    button(cards[0], 'Save tool').click();
    await settle(fixture);

    expect(requestBodies('PUT', 'admin/mcp-servers/files/tools/readFile')).toEqual([
      { isEnabled: true, effect: 'read-only', descriptionOverride: null }
    ]);
  });

  it('warns when a remote schema change disabled a tool', async () => {
    await configure();
    const admin = TestBed.inject(NhAssistantAdminApiService);
    await firstValueFrom(admin.syncMcpServer('files'));
    await firstValueFrom(admin.updateMcpTool('files', 'readFile', { isEnabled: true, effect: 'read-only', descriptionOverride: null }));
    TestBed.inject(NhAssistantMockBackend).setRemoteTools('files', [
      { remoteName: 'readFile', description: 'Reads a file', inputSchemaHash: 'a2', readOnlyHint: true }
    ]);
    const { fixture, element } = await render(NhAssistantAdminMcpServersComponent);

    button(element, 'Tools').click();
    await settle(fixture);
    button(element, 'Sync tools').click();
    await settle(fixture);

    const card = element.querySelector('.card[data-status="schema-changed"]') as HTMLElement;
    expect(card.querySelector('[role="alert"]')?.textContent).toContain("The tool's input changed on the server");
    expect((card.querySelector('input[type="checkbox"]') as HTMLInputElement).checked).toBeFalse();
    expect(element.querySelector('.card[data-status="missing"]')?.textContent).toContain('The server no longer lists this tool.');
  });

  it('adds an OAuth server only after the administrator read and confirmed its authorization server', async () => {
    await configure();
    const { fixture, element } = await render(NhAssistantAdminMcpServersComponent);

    button(element, 'New server').click();
    await settle(fixture);
    type(element, 'input[id$="-id"]', 'crm');
    type(element, 'input[id$="-name"]', 'CRM');
    type(element, 'input[id$="-url"]', 'https://crm.example.com/mcp');
    choose(element, 'select[id$="-auth"]', 'oauth');
    fixture.detectChanges();
    expect(element.textContent).toContain('every user of the server\'s agents acts as the connected account');
    button(element, 'Save').click();
    await settle(fixture);
    expect(element.textContent).toContain('Name the account that will be connected.');
    expect(element.textContent).toContain('Read the authorization and confirm it.');
    expect(requestBodies('POST', 'admin/mcp-servers')).toEqual([]);

    type(element, 'input[id$="-account-label"]', 'CRM service account');
    button(element, 'Read authorization').click();
    await settle(fixture);
    expect(element.querySelector('.discovery')?.textContent).toContain('https://auth.crm.example.com');
    expect((element.querySelector('input[id$="-scopes"]') as HTMLInputElement).value).toBe('mcp:read');
    check(element, '.discovery input[type="checkbox"]');
    type(element, 'input[id$="-client-id"]', 'assistant-crm');
    fixture.detectChanges();
    type(element, 'input[id$="-oauth-secret"]', 'test-crm-client-secret');
    button(element, 'Save').click();
    await settle(fixture);

    expect(requestBodies('POST', 'admin/mcp-servers')).toEqual([jasmine.objectContaining({
      authMode: 'oauth',
      connectionScope: 'organization',
      oauth: {
        clientId: 'assistant-crm',
        clientSecret: 'test-crm-client-secret',
        scopes: 'mcp:read',
        organizationAccountLabel: 'CRM service account',
        confirmAuthorization: true
      }
    })]);
    expect(element.textContent).toContain('Not connected');
    expect(element.textContent).toContain('Everyone acts as CRM service account');
    expect(element.innerHTML).not.toContain('test-crm-client-secret');
  });

  it('connects the administrator\'s own account to a server people connect to and revokes everyone\'s after confirmation', async () => {
    const script = scenario();
    script.admin!.mcpServers!.push({
      id: 'calendar', displayName: 'Calendar', url: 'https://calendar.example.com/mcp', authMode: 'oauth', headerName: null, requiredPolicy: null,
      isEnabled: true, connectionScope: 'user', oauth: { clientId: 'assistant-calendar', scopes: 'calendar:read' }
    });
    await configure(script);
    const { fixture, element } = await render(NhAssistantAdminMcpServersComponent);
    const card = (): HTMLElement => Array.from(element.querySelectorAll<HTMLElement>('.card')).find(item => item.textContent?.includes('Calendar'))!;

    expect(card().textContent).toContain('Each person connects');
    expect(card().textContent).toContain('Personal connections: 0');
    expect(buttons(card(), 'Revoke all personal connections').length).toBe(0);
    button(card(), 'Connect my account').click();
    await settle(fixture);
    expect(card().textContent).toContain('Your own account is connected; tests and syncs use it.');
    expect(card().textContent).toContain('Personal connections: 1');

    button(card(), 'Revoke all personal connections').click();
    fixture.detectChanges();
    button(card(), 'Revoke for everyone').click();
    await settle(fixture);
    expect(card().textContent).toContain('Every personal connection was revoked.');
    expect(requestBodies('DELETE', 'admin/mcp-servers/calendar/connections').length).toBe(1);
  });

  it('connects the organization through the authorization window and disconnects after confirmation', async () => {
    const script = scenario();
    script.admin!.mcpServers!.push({
      id: 'crm', displayName: 'CRM', url: 'https://crm.example.com/mcp', authMode: 'oauth', headerName: null, requiredPolicy: null,
      isEnabled: true, connectionScope: 'organization',
      oauth: { clientId: 'assistant-crm', scopes: 'mcp:read', organizationAccountLabel: 'CRM service account' }
    });
    await configure(script);
    const backend = TestBed.inject(NhAssistantMockBackend);
    const { fixture, element } = await render(NhAssistantAdminMcpServersComponent);
    const card = (): HTMLElement => Array.from(element.querySelectorAll<HTMLElement>('.card')).find(item => item.textContent?.includes('CRM'))!;

    backend.setMcpAuthorizationOutcome('deny');
    button(card(), 'Connect').click();
    await settle(fixture);
    expect(element.querySelector('[role="alert"]')?.textContent).toContain('The authorization was not given.');

    backend.setMcpAuthorizationOutcome('approve');
    button(card(), 'Connect').click();
    await settle(fixture);
    expect(backend.authorizationWindow.shown.length).toBe(2);
    expect(card().textContent).toContain('The organization\'s account is connected.');
    expect(card().textContent).toContain('Connected by current-user');
    expect(requestBodies('POST', 'mcp-connections/complete').length).toBe(1);

    button(card(), 'Disconnect').click();
    fixture.detectChanges();
    button(card(), 'Disconnect and revoke').click();
    await settle(fixture);
    expect(card().textContent).toContain('Not connected');
    expect(card().textContent).toContain('The connection was removed and its tokens revoked.');
  });
});

describe('NhAssistantAgentPickerComponent', () => {
  it('shows the literal name of an admin agent without a translation', async () => {
    await configure();
    const fixture = TestBed.createComponent(NhAssistantAgentPickerComponent);
    fixture.componentRef.setInput('agents', [
      { id: 'support-agent', version: 1, displayNameKey: 'Support agent', descriptionKey: 'Helps with support', canMutate: false }
    ]);
    fixture.componentRef.setInput('selectedAgentId', 'support-agent');
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('option')?.textContent?.trim()).toBe('Support agent');
    expect(element.querySelector('select')?.getAttribute('title')).toBe('Helps with support');
  });
});
