import { Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAdminApiService } from '../services/nh-assistant-admin-api.service';
import { NhAssistantStore } from '../services/nh-assistant.store';
import { NhAssistantPanelService } from '../services/nh-assistant-panel.service';
import { NhAssistantAgentPickerComponent } from '../components/agent-picker/nh-assistant-agent-picker.component';
import {
  NhAssistantAdminAgentsComponent,
  NhAssistantAdminComponent,
  NhAssistantAdminContextComponent,
  NhAssistantAdminLocation,
  NhAssistantAdminMcpServersComponent,
  nhAssistantAgentTab
} from '../../../admin/src/public-api';
import { ownShadowModeKey, shadowActionKey, shadowExplainKey } from '../../../admin/src/agents/agent-operations';
import {
  adminAgentPage as agentPage,
  adminStatusOf as statusOf,
  chooseAdminMore as openMore,
  chooseAdminStatus as chooseStatus,
  openAdminAgent as openAgent,
  openAdminTab as openTab
} from './nh-assistant-admin-navigation';

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
  it('opens on the agents and shows the sections as an ARIA tablist that follows the arrow keys', async () => {
    await configure();
    const { fixture, element } = await render(NhAssistantAdminComponent);

    const tabs = () => Array.from(element.querySelectorAll('.admin > .tabs [role="tab"]')) as HTMLElement[];
    expect(tabs().map(tab => tab.textContent?.trim())).toEqual(['Agents', 'Tools & connections', 'Context']);
    expect(tabs()[0].getAttribute('aria-selected')).toBe('true');
    expect(element.querySelector('[role="tabpanel"] nh-assistant-admin-agents')).not.toBeNull();

    element.querySelector('.admin > .tabs')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
    await settle(fixture);

    expect(tabs()[1].getAttribute('aria-selected')).toBe('true');
    expect(tabs()[0].getAttribute('tabindex')).toBe('-1');
    expect(element.querySelector('[role="tabpanel"] nh-assistant-admin-mcp-servers')).not.toBeNull();
  });

  it('still opens on the section a host chooses', async () => {
    await configure();
    const fixture = TestBed.createComponent(NhAssistantAdminComponent);
    fixture.componentRef.setInput('initialTab', 'context');
    const element = await settle(fixture);

    expect(element.querySelector('[role="tabpanel"] nh-assistant-admin-context')).not.toBeNull();
  });

  it('opens the agent page and tab its location names and reports where the administrator goes', async () => {
    await configure();
    const fixture = TestBed.createComponent(NhAssistantAdminComponent);
    const reported: NhAssistantAdminLocation[] = [];
    fixture.componentInstance.locationChange.subscribe(location => reported.push(location));
    fixture.componentRef.setInput('location', { tab: 'agents', agentId: 'projects', agentTab: 'build' });
    const element = await settle(fixture);

    expect(element.querySelector('[data-agent-page="projects"]')).not.toBeNull();
    expect(element.querySelector('[data-agent-panel]')?.getAttribute('data-agent-panel')).toBe('build');
    expect(reported).toEqual([]);

    await openTab(fixture, element, 'activity');
    expect(reported.at(-1)).toEqual({ tab: 'agents', agentId: 'projects', agentTab: 'activity' });

    (element.querySelector('[data-back]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(reported.at(-1)).toEqual({ tab: 'agents', agentId: null, agentTab: null });
    expect(element.querySelector('[data-agent-list]')).not.toBeNull();

    // The host follows the browser's back button: the location opens the agent again.
    fixture.componentRef.setInput('location', { tab: 'agents', agentId: 'projects', agentTab: 'overview' });
    await settle(fixture);
    expect(element.querySelector('[data-agent-page="projects"]')).not.toBeNull();
    expect(element.querySelector('[data-agent-panel]')?.getAttribute('data-agent-panel')).toBe('overview');

    button(element, 'Tools & connections').click();
    await settle(fixture);
    expect(reported.at(-1)).toEqual({ tab: 'mcp-servers', agentId: null, agentTab: null });
    expect(nhAssistantAgentTab('rollout')).toBe('rollout');
    expect(nhAssistantAgentTab('elsewhere')).toBe('overview');
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
  it('lists code agents with one status and opens a page without a delete action', async () => {
    await configure();
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);

    const row = element.querySelector('[data-agent-row="projects"]') as HTMLElement;
    expect(row.textContent).toContain('Project assistant');
    expect(row.textContent).toContain('Code');
    expect(row.querySelectorAll('[data-status]').length).toBe(1);
    expect(row.querySelector('[data-status]')?.getAttribute('data-status-value')).toBe('live');
    expect(row.querySelector('button')).toBeNull();

    const page = await openAgent(fixture, element, 'projects');
    expect(document.activeElement).toBe(page.querySelector('h3'));
    await openMore(fixture, page);
    expect(page.querySelector('[data-export-package="projects"]')).not.toBeNull();
    expect(page.querySelector('[data-delete-agent]')).toBeNull();
  });

  it('finds agents by their translated name and id, filters them with counts and lists a shadow variant below its agent', async () => {
    const script = scenario();
    script.admin!.agents![0] = {
      ...script.admin!.agents![0],
      displayName: 'agents.projects.name',
      skills: [{ skillId: 'deadline-risk', description: 'Judges deadline risks.', shadow: true }]
    };
    script.admin!.agents!.push(
      {
        source: 'code', id: 'projects-v2', displayName: 'Project assistant v2', description: 'Projects', instructions: 'Other instructions.',
        toolSelectors: ['sample-api.project.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'execute', isEnabled: true,
        shadowOf: 'projects'
      },
      {
        source: 'code', id: 'helper', displayName: 'Chat helper', description: 'Helps in the chat.', instructions: 'Help.',
        toolSelectors: [], mcpServerIds: [], requiredPolicy: null, autonomy: 'explain', isEnabled: true, chatOnly: true, startsOff: true
      }
    );
    await configure(script);
    TestBed.inject(TranslateService).setTranslation('en', { agents: { projects: { name: 'Planning desk' } } }, true);
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const rows = () => Array.from(element.querySelectorAll<HTMLElement>('[data-agent]')).map(item => item.getAttribute('data-agent'));
    const count = (filter: string) => element.querySelector(`[data-agent-filter="${filter}"] .count`)?.textContent?.trim();
    const filter = async (name: string) => {
      (element.querySelector(`[data-agent-filter="${name}"]`) as HTMLButtonElement).click();
      await settle(fixture);
    };

    // The variant follows its agent, indented; each agent shows one status.
    expect(rows()).toEqual(['projects', 'projects-v2', 'helper']);
    expect(element.querySelector('li.variant')?.getAttribute('data-agent')).toBe('projects-v2');
    expect(element.querySelector('li.variant')?.textContent).toContain('Shadow variant of Planning desk');
    expect(element.querySelector('[data-agent-row="projects-v2"] [data-status]')?.getAttribute('data-status-value')).toBe('shadow');
    expect(element.querySelector('[data-agent-row="helper"] [data-status]')?.getAttribute('data-status-value')).toBe('off');
    expect(element.querySelector('[data-agent-row="helper"] [data-agent-work]')?.textContent?.trim()).toBe('Chat only');
    expect(element.querySelector('[data-agent-row="projects"] [data-agent-attention]')?.textContent).toContain('1 skill in shadow');
    expect([count('all'), count('attention'), count('live'), count('shadow'), count('held'), count('chat-only')]).toEqual(['3', '1', '1', '2', '1', '1']);

    // The search reads the name in the reader's language and the id.
    type(element, '[data-agent-search]', 'planning');
    await settle(fixture);
    expect(rows()).toEqual(['projects']);
    type(element, '[data-agent-search]', 'v2');
    await settle(fixture);
    // A matching variant brings its agent along, so it keeps its place.
    expect(rows()).toEqual(['projects', 'projects-v2']);
    type(element, '[data-agent-search]', '');
    await filter('chat-only');
    expect(rows()).toEqual(['helper']);
    expect(element.querySelector('[data-agent-filter="chat-only"]')?.getAttribute('aria-pressed')).toBe('true');
    type(element, '[data-agent-search]', 'nothing like this');
    await settle(fixture);
    expect(element.querySelector('[data-agent-list-empty]')?.textContent).toContain('No agent matches the search and filter.');
    button(element, 'Show all agents').click();
    await settle(fixture);
    expect(rows()).toEqual(['projects', 'projects-v2', 'helper']);
  });

  it('opens the feedback report of an agent in its activity and returns to the list at its row', async () => {
    await configure();
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);

    const page = await openAgent(fixture, element, 'projects', 'activity');
    (page.querySelector('[data-activity="feedback"]') as HTMLButtonElement).click();
    await settle(fixture);

    const report = element.querySelector('nh-assistant-agent-feedback-report') as HTMLElement;
    expect(report).not.toBeNull();
    expect(report.textContent).toContain('Feedback on proposals');
    expect(report.textContent).toContain('The agent made no proposals in this period.');
    expect(report.textContent).toContain('Nobody gave feedback in this period.');
    expect(button(report, 'Export (JSON Lines)').disabled).toBeTrue();
    const summary = TestBed.inject(NhAssistantMockBackend).requests.find(item => item.path === 'admin/agents/projects/feedback/summary');
    expect(summary?.method).toBe('GET');

    (element.querySelector('[data-back]') as HTMLButtonElement).click();
    await settle(fixture);

    expect(element.querySelector('nh-assistant-agent-feedback-report')).toBeNull();
    expect(document.activeElement).toBe(element.querySelector('[data-agent-row="projects"]'));
  });

  it('creates an agent with its profile first and gives it catalog selectors and an MCP server on its page', async () => {
    await configure();
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);

    (element.querySelector('[data-new-agent]') as HTMLButtonElement).click();
    await settle(fixture);
    // The first step asks what the agent is; its tools follow on its page.
    expect(element.querySelector('.catalog')).toBeNull();
    type(element, 'input[id$="-id"]', 'support-agent');
    type(element, 'input[id$="-name"]', 'Support agent');
    type(element, 'textarea', 'Help with support questions.');
    button(element, 'Save').click();
    await settle(fixture);

    expect(requestBodies('POST', 'admin/agents')).toEqual([jasmine.objectContaining({
      id: 'support-agent',
      displayName: 'Support agent',
      instructions: 'Help with support questions.',
      toolSelectors: [],
      mcpServerIds: [],
      autonomy: 'explain'
    })]);
    expect(Object.keys(requestBodies('POST', 'admin/agents')[0] as object)).not.toContain('isEnabled');
    const page = agentPage(element);
    expect(page.getAttribute('data-agent-page')).toBe('support-agent');
    expect(page.querySelector('[data-agent-source]')?.textContent).toContain('Created here');

    await openTab(fixture, element, 'build');
    (page.querySelector('[data-edit-section="tools"]') as HTMLButtonElement).click();
    await settle(fixture);
    const tools = page.querySelector('[data-build-section="tools"]') as HTMLElement;
    const catalog = Array.from(tools.querySelectorAll('.catalog input[type="checkbox"]')) as HTMLInputElement[];
    catalog[0].click();
    const server = Array.from(tools.querySelectorAll('fieldset .check input[type="checkbox"]')).pop() as HTMLInputElement;
    server.click();
    fixture.detectChanges();
    expect(tools.textContent).toContain('1 catalog tools match the selectors.');
    button(tools, 'Save').click();
    await settle(fixture);

    expect(requestBodies('PUT', 'admin/agents/support-agent')).toEqual([jasmine.objectContaining({ toolSelectors: ['sample-api.project.list'] })]);
    expect(requestBodies('PUT', 'admin/mcp-servers/files/agents/support-agent').length).toBe(1);
    expect(tools.querySelector('[data-tools-summary]')?.textContent).toContain('sample-api.project.list');
    expect(tools.querySelector('[data-agent-servers]')?.textContent).toContain('Files');
  });

  it('creates an agent that works with a model profile of the run catalog', async () => {
    await configure(runScenario());
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);

    (element.querySelector('[data-new-agent]') as HTMLButtonElement).click();
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

    (element.querySelector('[data-new-agent]') as HTMLButtonElement).click();
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
    (element.querySelector('[data-back]') as HTMLButtonElement).click();
    await settle(fixture);
    (element.querySelector('[data-new-agent]') as HTMLButtonElement).click();
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

    // The list says the agent needs attention; its page says that its model calls will be refused, and so does its editor.
    expect(element.querySelector('[data-agent-row="vault-desk"] [data-agent-attention]')?.textContent).toContain('1 warning');
    const page = await openAgent(fixture, element, 'vault-desk', 'build');
    expect(page.querySelector('[data-agent-warning="classification-not-permitted"]')?.textContent)
      .toContain("Neither the agent's model profile nor the profile's fallbacks permit its data classification.");
    (page.querySelector('[data-edit-section="profile"]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(page.querySelector('[data-agent-editor-warning="classification-not-permitted"]')).not.toBeNull();
    button(page.querySelector('[data-build-section="profile"]') as HTMLElement, 'Cancel').click();
    await settle(fixture);

    // A skill saved with a classification no profile permits is saved, with the warning next to it.
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

    (element.querySelector('[data-new-agent]') as HTMLButtonElement).click();
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

    expect(element.querySelector('[data-agent-row="projects"]')?.textContent).toContain('Code, changed');
    const page = await openAgent(fixture, element, 'projects');
    expect(page.querySelector('[data-agent-origin]')?.textContent).toContain('Defined in code and changed here.');
    await openMore(fixture, page);
    (page.querySelector('[data-reset-agent]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(page.querySelector('[data-confirming="reset"]')?.textContent).toContain('the agent stays active, paused or stopped as it is');
    button(page, 'Confirm reset').click();
    await settle(fixture);

    expect(page.querySelector('[data-agent-source]')?.textContent?.trim()).toBe('Code');
    expect(requestBodies('POST', 'admin/agents/projects/reset').length).toBe(1);
  });

  it('lists the skills of an agent in Build with their usage and stops one of them', async () => {
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
    const page = await openAgent(fixture, element, 'projects', 'build');
    const skill = () => page.querySelector('[data-code-skill="deadline-risk"]') as HTMLElement;

    expect(page.querySelectorAll('[data-skill-status]').length).toBe(2);
    expect(skill().textContent).toContain('Judges deadline risks.');
    expect(skill().textContent).toContain('Today 0 tool calls, 0 tokens');
    expect(statusOf(skill())).toBe('live');

    await chooseStatus(fixture, skill(), 'deadline-risk', '[data-state-action="stopped"]');
    expect(skill().textContent).toContain('The runs of the skill Deadline risk are cancelled');
    type(skill(), '.change-form input', 'Wrong risks');
    button(skill(), 'Confirm stop').click();
    await settle(fixture);

    expect(requestBodies('POST', 'admin/agents/projects/skills/deadline-risk/state')).toEqual([{ state: 'stopped', reason: 'Wrong risks' }]);
    expect(statusOf(skill())).toBe('stopped');
    expect(skill().textContent).toContain('Note: Wrong risks');
    expect(statusOf(page.querySelector('[data-code-skill="task-follow-up"]') as HTMLElement)).toBe('live');
    expect(statusOf(page.querySelector('.page-head') as HTMLElement)).toBe('live');
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
    const page = await openAgent(fixture, element, 'projects');
    const head = () => page.querySelector('.page-head') as HTMLElement;
    const skill = (id: string) => page.querySelector(`[data-code-skill="${id}"]`) as HTMLElement;

    // The overview names the technical account; the agent itself works live.
    expect(page.querySelector('[data-service-account]')?.textContent).toContain('Technical account: account ID service-1');
    expect(statusOf(head())).toBe('live');

    await chooseStatus(fixture, head(), 'agent', '[data-shadow-action="shadow"]');
    expect(head().textContent).toContain('New runs work in shadow threads that only administrators see');
    type(head(), '.change-form input', 'Evaluate the rules');
    button(head(), 'Confirm shadow').click();
    await settle(fixture);
    expect(requestBodies('POST', 'admin/agents/projects/shadow')).toEqual([{ mode: 'shadow', reason: 'Evaluate the rules' }]);
    expect(statusOf(head())).toBe('shadow');
    await openTab(fixture, element, 'rollout');
    expect(page.querySelector('[data-rollout-mode]')?.textContent).toContain('Note: Evaluate the rules');
    expect(page.querySelector('[data-rollout-agent-shadow]')).not.toBeNull();

    // In Build every skill of an agent in shadow works in shadow; one in shadow by code offers to go live.
    await openTab(fixture, element, 'build');
    expect(statusOf(skill('task-follow-up'))).toBe('shadow');
    expect(statusOf(skill('deadline-risk'))).toBe('shadow');
    await chooseStatus(fixture, skill('deadline-risk'), 'deadline-risk', '[data-shadow-action="live"]');
    expect(skill('deadline-risk').textContent).toContain('The skill Deadline risk works live again, unless its agent works in shadow.');
    button(skill('deadline-risk'), 'Confirm live').click();
    await settle(fixture);
    expect(requestBodies('POST', 'admin/agents/projects/skills/deadline-risk/shadow')).toEqual([{ mode: 'live', reason: null }]);
    (skill('deadline-risk').querySelector('[data-status-menu="deadline-risk"]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(skill('deadline-risk').querySelector('[data-shadow-action="code"]')).not.toBeNull();
    (skill('deadline-risk').querySelector('[data-status-menu="deadline-risk"]') as HTMLButtonElement).click();
    await settle(fixture);

    await chooseStatus(fixture, head(), 'agent', '[data-shadow-action="code"]');
    button(head(), 'Confirm').click();
    await settle(fixture);
    expect(requestBodies('POST', 'admin/agents/projects/shadow')[1]).toEqual({ mode: 'code', reason: null });
    expect(statusOf(head())).toBe('live');
    expect(statusOf(skill('deadline-risk'))).toBe('live');

    // In the guardrails an account that does not exist is refused; another one answers for new runs.
    const account = () => page.querySelector('[data-guardrails-account]') as HTMLElement;
    (account().querySelector('[data-account-action="set"]') as HTMLButtonElement).click();
    await settle(fixture);
    button(account(), 'Confirm account').click();
    await settle(fixture);
    // A missing account shows at the account field, which describes itself with it.
    const accountError = account().querySelector('[data-account-error]') as HTMLElement;
    expect(accountError.textContent).toContain('Required.');
    const accountField = account().querySelector('input[id$="-account"]') as HTMLInputElement;
    expect(document.activeElement).toBe(accountField);
    expect(accountField.getAttribute('aria-invalid')).toBe('true');
    expect(accountField.getAttribute('aria-describedby')).toContain(accountError.id);
    expect(account().querySelector('[data-agent-error]')).toBeNull();
    expect(requestBodies('POST', 'admin/agents/projects/service-account')).toEqual([]);
    type(account(), 'input[id$="-account"]', 'unknown-1');
    await settle(fixture);
    button(account(), 'Confirm account').click();
    await settle(fixture);
    // The server's refusal of the account shows at the field too, and the form stays open.
    expect(account().querySelector('[data-account-error]')?.textContent).toContain('No account that may sign in exists with this id.');
    expect(account().querySelector('[data-account-form]')).not.toBeNull();

    type(account(), 'input[id$="-account"]', 'service-2');
    type(account(), 'input[id$="-reason"]', 'Rotate');
    await settle(fixture);
    button(account(), 'Confirm account').click();
    await settle(fixture);
    expect(requestBodies('POST', 'admin/agents/projects/service-account').at(-1)).toEqual({ actorId: 'service-2', reason: 'Rotate' });
    expect(account().querySelector('[data-service-account]')?.textContent).toContain('Technical account: account ID service-2');
    expect(account().querySelector('[data-service-account]')?.textContent).toContain('In code: account ID service-1');

    (account().querySelector('[data-account-action="code"]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(account().textContent).toContain('New runs answer to the account in code again: account ID service-1.');
    button(account(), 'Confirm').click();
    await settle(fixture);
    expect(requestBodies('POST', 'admin/agents/projects/service-account').at(-1)).toEqual({ actorId: null, reason: null });
    expect(account().querySelector('[data-service-account]')?.textContent).toContain('Technical account: account ID service-1');
    expect(account().querySelector('[data-account-action="code"]')).toBeNull();
  });

  it('compares an agent with its shadow variant in its rollout, replays the period and offers evaluation only with a variant', async () => {
    const script = scenario();
    script.admin!.agents!.push({
      source: 'code', id: 'projects-v2', displayName: 'Project assistant v2', description: 'Projects', instructions: 'Other instructions.',
      toolSelectors: ['sample-api.project.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'execute', isEnabled: true,
      shadowOf: 'projects'
    });
    await configure(script);
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);

    // The variant itself has no variants, so its rollout offers no evaluation.
    let page = await openAgent(fixture, element, 'projects-v2', 'rollout');
    expect(page.querySelector('[data-agent-origin]')?.textContent).toContain('A shadow variant of Project assistant');
    expect(page.querySelector('[data-rollout-evaluation]')).toBeNull();
    (page.querySelector('[data-back]') as HTMLButtonElement).click();
    await settle(fixture);

    page = await openAgent(fixture, element, 'projects', 'rollout');
    const evaluation = page.querySelector('[data-rollout-evaluation]') as HTMLElement;
    expect(evaluation.textContent).toContain('Evaluation of shadow variants');
    expect(evaluation.querySelector('[data-paired]')?.textContent?.trim()).toBe('0');
    // The mock answers only for the agent's own variant, so the report names it.
    expect(evaluation.textContent).toContain('Small numbers mislead');
    expect(evaluation.querySelector('select[id$="-variant"]')?.textContent).toContain('projects-v2');

    button(evaluation, 'Replay the period').click();
    await settle(fixture);
    const [request] = requestBodies('POST', 'admin/agents/projects-v2/replay') as { sourceAgentId: string; maxRuns: number }[];
    expect(request.sourceAgentId).toBe('projects');
    expect(request.maxRuns).toBe(100);
    expect(evaluation.querySelector('[data-replay="completed"]')?.textContent).toContain('0 of at most 100 runs replayed');
  });

  it('pauses with a reason, stops and resumes an agent from the status of its page', async () => {
    await configure();
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const page = await openAgent(fixture, element, 'projects');
    const head = () => page.querySelector('.page-head') as HTMLElement;
    const overview = () => page.querySelector('[data-overview-status]') as HTMLElement;

    expect(statusOf(head())).toBe('live');
    await chooseStatus(fixture, head(), 'agent', '[data-state-action="paused"]');
    expect(head().textContent).toContain('Events wait and run after the resume');
    expect(document.activeElement).toBe(head().querySelector('.change-form input'));
    type(head(), '.change-form input', 'Nightly import');
    button(head(), 'Confirm pause').click();
    await settle(fixture);

    expect(requestBodies('POST', 'admin/agents/projects/state')).toEqual([{ state: 'paused', reason: 'Nightly import' }]);
    expect(statusOf(head())).toBe('paused');
    expect(overview().textContent).toContain('Note: Nightly import');
    expect(overview().textContent).toContain('0 events wait for the resume.');

    await chooseStatus(fixture, head(), 'agent', '[data-state-action="stopped"]');
    button(head(), 'Cancel').click();
    await settle(fixture);
    expect(requestBodies('POST', 'admin/agents/projects/state').length).toBe(1);
    expect(document.activeElement).toBe(head().querySelector('[data-status-menu="agent"]'));

    await chooseStatus(fixture, head(), 'agent', '[data-state-action="stopped"]');
    button(head(), 'Confirm stop').click();
    await settle(fixture);
    expect(statusOf(head())).toBe('stopped');
    (head().querySelector('[data-status-menu="agent"]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(head().querySelector('[data-state-action="stopped"]')).toBeNull();
    (head().querySelector('[data-status-menu="agent"]') as HTMLButtonElement).click();
    await settle(fixture);
    // It was not off when it stopped, so releasing the kill switch makes it work again.
    const stoppedHint = overview().querySelector('[data-stopped-hint]');
    expect(stoppedHint?.getAttribute('data-off-before-stop')).toBe('false');
    expect(stoppedHint?.textContent).toContain('releasing the kill switch makes it work again, as Resume does.');

    await chooseStatus(fixture, head(), 'agent', '[data-state-action="active"]');
    button(head(), 'Confirm resume').click();
    await settle(fixture);

    expect(requestBodies('POST', 'admin/agents/projects/state').at(-1)).toEqual({ state: 'active', reason: null });
    expect(statusOf(head())).toBe('live');
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
    const page = await openAgent(fixture, element, 'projects');
    const head = () => page.querySelector('.page-head') as HTMLElement;
    const overview = () => page.querySelector('[data-overview-status]') as HTMLElement;

    // Off in code: the agent says what that means, and offers to switch it on instead of a resume.
    expect(statusOf(head())).toBe('off');
    expect(overview().querySelector('[data-off-hint]')?.textContent).toContain('It started off, as its code says.');
    (head().querySelector('[data-status-menu="agent"]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(buttons(head(), 'Resume').length).toBe(0);
    (head().querySelector('[data-state-action="active"]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(head().textContent).toContain('Nothing that happened while it was off runs.');
    button(head(), 'Confirm switch on').click();
    await settle(fixture);
    expect(requestBodies('POST', 'admin/agents/projects/state')).toEqual([{ state: 'active', reason: null }]);
    expect(statusOf(head())).toBe('live');

    await openTab(fixture, element, 'build');
    const skill = () => page.querySelector('[data-code-skill="deadline-risk"]') as HTMLElement;
    await chooseStatus(fixture, skill(), 'deadline-risk', '[data-state-action="active"]');
    expect(skill().textContent).toContain('The skill Deadline risk takes work again.');
    button(skill(), 'Confirm switch on').click();
    await settle(fixture);
    expect(requestBodies('POST', 'admin/agents/projects/skills/deadline-risk/state')).toEqual([{ state: 'active', reason: null }]);

    await openTab(fixture, element, 'overview');
    await chooseStatus(fixture, head(), 'agent', '[data-state-action="off"]');
    expect(head().textContent).toContain('Events, schedules, webhooks, hand-offs and follow-ups are ignored');
    type(head(), '.change-form input', 'Not yet');
    button(head(), 'Confirm switch off').click();
    await settle(fixture);
    expect(requestBodies('POST', 'admin/agents/projects/state').at(-1)).toEqual({ state: 'off', reason: 'Not yet' });
    expect(overview().querySelector('[data-off-hint]')?.textContent).toContain('People can still chat with it and start it by hand.');
    expect(overview().textContent).toContain('Note: Not yet');

    // Stopped while it was off, the page says that releasing the kill switch switches it back off,
    // while resuming switches it on; once it works again the hint is gone.
    expect(overview().querySelector('[data-stopped-hint]')).toBeNull();
    await chooseStatus(fixture, head(), 'agent', '[data-state-action="stopped"]');
    button(head(), 'Confirm stop').click();
    await settle(fixture);
    const stoppedHint = overview().querySelector('[data-stopped-hint]');
    expect(stoppedHint?.getAttribute('data-off-before-stop')).toBe('true');
    expect(stoppedHint?.textContent).toContain('It was off when it stopped: releasing the kill switch switches it back off. Resume switches it on.');
    expect(await firstValueFrom(TestBed.inject(NhAssistantAdminApiService).getAgentState('projects')))
      .toEqual(jasmine.objectContaining({ runState: 'stopped', offBeforeStop: true }));
    await chooseStatus(fixture, head(), 'agent', '[data-state-action="active"]');
    button(head(), 'Confirm resume').click();
    await settle(fixture);
    expect(statusOf(head())).toBe('live');
    expect(overview().querySelector('[data-stopped-hint]')).toBeNull();

    // A new agent starts off when the administrator leaves it switched off.
    (page.querySelector('[data-back]') as HTMLButtonElement).click();
    await settle(fixture);
    (element.querySelector('[data-new-agent]') as HTMLButtonElement).click();
    await settle(fixture);
    type(element, 'input[id$="-id"]', 'quiet-agent');
    type(element, 'input[id$="-name"]', 'Quiet agent');
    type(element, 'textarea', 'Help quietly.');
    (element.querySelector('[data-switch-on-now] input') as HTMLInputElement).click();
    fixture.detectChanges();
    button(element, 'Save').click();
    await settle(fixture);
    expect(requestBodies('POST', 'admin/agents')).toEqual([jasmine.objectContaining({ id: 'quiet-agent', startsOff: true })]);
    expect(statusOf(agentPage(element).querySelector('.page-head') as HTMLElement)).toBe('off');
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
    await openAgent(fixture, element, 'follower', 'build');
    const view = () => element.querySelector('nh-assistant-agent-run-configuration') as HTMLElement;
    const form = () => view().querySelector('[data-form="settings"]') as HTMLElement;

    // It only chats until it has run settings; Build offers the guided setup first.
    expect(view().querySelector('[data-build-section="work"] [data-setup-offer]')?.textContent).toContain('This agent only chats.');
    expect(view().querySelector('[data-guardrails-no-settings]')).not.toBeNull();
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
    // With run settings the setup is gone and the guardrails show them.
    expect(view().querySelector('[data-build-section="work"]')).toBeNull();
    const summary = view().querySelector('[data-build-section="guardrails"] [data-settings-summary]') as HTMLElement;
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
    expect(view().querySelector('[data-run-settings] [role="alert"]')?.textContent).toContain('Skills, triggers, consultations or hand-offs still belong to this agent.');

    // Back in the list, the new skill in shadow needs attention.
    (element.querySelector('[data-back]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(element.querySelector('[data-agent-row="follower"] [data-agent-attention]')?.textContent).toContain('1 skill in shadow');
    expect(document.activeElement).toBe(element.querySelector('[data-agent-row="follower"]'));
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
    await openAgent(fixture, element, 'reader', 'build');
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

  it('creates an agent that works on its own, out of the chat, gives it tools and sets it up through the guided setup', async () => {
    const script = runScenario();
    script.admin!.accounts = [{ actorId: 'service-1', displayName: 'Operations account', detail: 'operations@example.test' }];
    await configure(script);
    const store = TestBed.inject(NhAssistantStore);
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);

    (element.querySelector('[data-new-agent]') as HTMLButtonElement).click();
    await settle(fixture);
    type(element, 'input[id$="-id"]', 'starter');
    type(element, 'input[id$="-name"]', 'Project starter');
    check(element, '[data-agent-use] input[value="background"]');
    await settle(fixture);
    expect(element.textContent).toContain('After saving, a short setup asks when the agent starts');
    button(element, 'Save').click();
    await settle(fixture);
    expect(requestBodies('POST', 'admin/agents').at(-1)).toEqual(jasmine.objectContaining({ id: 'starter', offeredInChat: false }));
    await store.reloadStatus();
    expect(store.agents().map(agent => agent.id)).not.toContain('starter');

    // Its page opens at Build with the guided setup; the administrator first gives it tools.
    const page = agentPage(element);
    expect(page.querySelector('[data-agent-panel]')?.getAttribute('data-agent-panel')).toBe('build');
    const setup = () => element.querySelector('[data-work-setup]') as HTMLElement;
    expect(setup()).not.toBeNull();
    (page.querySelector('[data-edit-section="tools"]') as HTMLButtonElement).click();
    await settle(fixture);
    const tools = page.querySelector('[data-build-section="tools"]') as HTMLElement;
    type(tools, 'input.mono[aria-label]', 'sample-api.project.*');
    await settle(fixture);
    (tools.querySelector('.inline button') as HTMLButtonElement).click();
    await settle(fixture);
    button(tools, 'Save').click();
    await settle(fixture);
    expect(requestBodies('PUT', 'admin/agents/starter').at(-1)).toEqual(jasmine.objectContaining({ toolSelectors: ['sample-api.project.*'] }));
    // The run configuration read the agent again, so the setup offers its new tools.
    if (!setup()) {
      button(page, 'Let this agent work on its own').click();
      await settle(fixture);
    }

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

    (element.querySelector('[data-back]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(element.querySelector('[data-agent-row="starter"] [data-agent-work]')?.textContent?.trim()).toBe('On its own');
  }, formFlowTimeout);

  it('evaluates a changed skill of a code agent as a candidate and promotes it; skills and triggers in code stay read only', async () => {
    const script = runScenario();
    script.admin!.agents![0] = { ...script.admin!.agents![0], skills: [{ skillId: 'task-follow-up', description: 'Follows up tasks by rule.' }] };
    script.admin!.codeTriggers = [{ agentId: 'projects', triggerId: 'task-completed', source: 'event', skillId: 'task-follow-up' }];
    await configure(script);
    const admin = TestBed.inject(NhAssistantAdminApiService);
    await firstValueFrom(admin.saveDataSkill('projects', 'progress-note', { description: 'Notes progress.', ruleHandler: 'project-progress-note' }));
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    await openAgent(fixture, element, 'projects', 'build');
    const view = () => element.querySelector('nh-assistant-agent-run-configuration') as HTMLElement;
    const skill = () => view().querySelector('[data-data-skill="progress-note"]') as HTMLElement;

    // A skill in code offers its status, never an edit or a removal.
    const codeSkill = view().querySelector('[data-code-skill="task-follow-up"]') as HTMLElement;
    expect(codeSkill.textContent).toContain('Code');
    expect(buttons(codeSkill, 'Edit').length).toBe(0);
    expect(codeSkill.querySelector('[data-remove-skill]')).toBeNull();
    expect(view().querySelector('[data-code-skill="progress-note"]')).toBeNull();
    expect((view().querySelector('[data-code-trigger="task-completed"]') as HTMLElement).textContent).toContain('Skill: Task follow up');
    expect(view().querySelector('[data-run-settings]')).toBeNull();

    // A change is evaluated in shadow by default.
    button(skill(), 'Edit').click();
    await settle(fixture);
    expect((view().querySelector('[data-apply] input[value="shadow"]') as HTMLInputElement).checked).toBeTrue();
    type(view(), 'input[id$="-skill-description"]', 'Notes progress as a percentage.');
    button(view().querySelector('[data-form="skill"]') as HTMLElement, 'Save').click();
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

  it('evaluates a changed trigger as a candidate next to the live version, promotes it and shows its history', async () => {
    const script = runScenario();
    script.admin!.runCatalog!.triggerEvents = [{
      name: 'project.created',
      description: 'A project was created.',
      filters: [{ name: 'not-own-change', description: 'Skips changes the agent made itself.' }]
    }];
    await configure(script);
    const admin = TestBed.inject(NhAssistantAdminApiService);
    await firstValueFrom(admin.saveDataTrigger('projects', 'project-created', { source: 'event', event: 'project.created' }));
    await firstValueFrom(admin.setDataTriggerShadow('projects', 'project-created', { mode: 'live' }));
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    await openAgent(fixture, element, 'projects', 'build');
    const view = () => element.querySelector('nh-assistant-agent-run-configuration') as HTMLElement;
    const trigger = () => view().querySelector('[data-data-trigger="project-created"]') as HTMLElement;
    const form = () => view().querySelector('[data-form="trigger"]') as HTMLElement;

    // A change applies at once by default, as before; the administrator chooses to evaluate it first.
    button(trigger(), 'Edit').click();
    await settle(fixture);
    expect((form().querySelector('[data-apply] input[value="live"]') as HTMLInputElement).checked).toBeTrue();
    check(form(), '[data-filter="not-own-change"] input');
    check(form(), '[data-apply] input[value="shadow"]');
    type(form(), 'input[id$="-trigger-reason"]', 'Skip own changes');
    button(form(), 'Save').click();
    await settle(fixture);
    expect(requestBodies('PUT', 'admin/agents/projects/triggers/project-created').at(-1)).toEqual(jasmine.objectContaining({
      filters: ['not-own-change'], expectedVersion: 1, apply: 'shadow', reason: 'Skip own changes'
    }));
    expect(view().textContent).toContain('The new version fires in shadow next to the live version, which keeps working.');

    // The live version keeps working live; the candidate says what it changes.
    expect(trigger().querySelector('[data-trigger-candidate-badge]')?.textContent).toContain('Candidate v2');
    expect(trigger().querySelector('[data-shadow-badge]')).toBeNull();
    expect(trigger().textContent).toContain('Version 1');
    expect(trigger().querySelector('[data-trigger-change="filters"]')?.textContent).toContain('Filters');
    expect(trigger().querySelector('[data-trigger-changes]')?.textContent).toContain('not-own-change (live: none)');

    button(trigger(), 'Compare').click();
    await settle(fixture);
    const comparison = trigger().querySelector('[data-trigger-comparison]') as HTMLElement;
    expect(comparison.textContent).toContain('Live (v1)');
    expect(comparison.textContent).toContain('Candidate (v2)');
    expect(trigger().querySelector('[data-trigger-paired]')?.textContent).toContain('both started a run on 0 of the same events');

    (trigger().querySelector('[data-promote-trigger]') as HTMLButtonElement).click();
    await settle(fixture);
    button(trigger(), 'Confirm promote').click();
    await settle(fixture);
    expect(requestBodies('POST', 'admin/agents/projects/triggers/project-created/candidate/promote')).toEqual([{ version: 2, reason: null }]);
    expect(view().textContent).toContain('The candidate is the live version of the trigger now.');
    expect(trigger().querySelector('[data-trigger-candidate-badge]')).toBeNull();
    expect(trigger().querySelector('[data-shadow-badge]')).toBeNull();
    expect(trigger().textContent).toContain('Version 2');
    expect(trigger().querySelector('[data-trigger-filters]')?.textContent).toContain('Filters: not-own-change');

    (trigger().querySelector('[data-trigger-versions]') as HTMLButtonElement).click();
    await settle(fixture);
    const history = trigger().querySelector('[data-trigger-history]') as HTMLElement;
    expect(history.querySelector('[data-trigger-version="2"]')?.textContent).toContain('Live');
    expect(history.querySelector('[data-trigger-version="2"]')?.textContent).toContain('project.created (not-own-change)');
    expect(history.querySelector('[data-trigger-version="2"]')?.textContent).toContain('Note: Skip own changes');
    expect(history.querySelector('[data-trigger-version="1"]')?.textContent).toContain('Earlier');
  }, formFlowTimeout);

  it('discards a trigger candidate, and refuses a candidate of a new trigger as the server does', async () => {
    await configure(runScenario());
    const admin = TestBed.inject(NhAssistantAdminApiService);
    const backend = TestBed.inject(NhAssistantMockBackend);
    await firstValueFrom(admin.saveDataTrigger('projects', 'project-created', { source: 'event', event: 'project.created' }));
    const refused = await backend.fetch('/api/assistant/admin/agents/projects/triggers/other-trigger', {
      method: 'PUT',
      body: JSON.stringify({ source: 'event', event: 'project.created', apply: 'shadow' })
    });
    expect([refused.status, (await refused.json()).errors]).toEqual([400, { apply: ['invalid'] }]);
    await firstValueFrom(admin.saveDataTrigger('projects', 'project-created', {
      source: 'event', event: 'project.created', debounceSeconds: 30, expectedVersion: 1, apply: 'shadow'
    }));
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    await openAgent(fixture, element, 'projects', 'build');
    const view = () => element.querySelector('nh-assistant-agent-run-configuration') as HTMLElement;
    const trigger = () => view().querySelector('[data-data-trigger="project-created"]') as HTMLElement;
    expect(trigger().querySelector('[data-trigger-change="debounce"]')).not.toBeNull();
    expect(trigger().querySelector('[data-trigger-changes]')?.textContent).toContain('30 (live: 120)');

    (trigger().querySelector('[data-discard-trigger]') as HTMLButtonElement).click();
    await settle(fixture);
    button(trigger(), 'Confirm discard').click();
    await settle(fixture);
    expect(requestBodies('POST', 'admin/agents/projects/triggers/project-created/candidate/discard')).toEqual([{ version: 2, reason: null }]);
    expect(view().textContent).toContain('The candidate is discarded; it stays as history.');
    expect(trigger().querySelector('[data-trigger-candidate]')).toBeNull();
    const versions = await firstValueFrom(admin.getDataTriggerVersions('projects', 'project-created'));
    expect(versions.map(version => [version.version, version.status])).toEqual([[2, 'discarded'], [1, 'live']]);
  });

  it('lists in its rollout what of an agent is not live yet and opens the comparison of a candidate in Build', async () => {
    const script = runScenario();
    script.admin!.agents![0] = { ...script.admin!.agents![0], skills: [{ skillId: 'deadline-risk', description: 'Judges deadline risks.', shadow: true }] };
    await configure(script);
    const admin = TestBed.inject(NhAssistantAdminApiService);
    await firstValueFrom(admin.saveDataTrigger('projects', 'project-created', { source: 'event', event: 'project.created' }));
    await firstValueFrom(admin.saveDataSkill('projects', 'progress-note', { description: 'Notes progress.', ruleHandler: 'project-progress-note' }));
    await firstValueFrom(admin.saveDataSkill('projects', 'progress-note', {
      description: 'Notes progress as a percentage.', ruleHandler: 'project-progress-note', expectedVersion: 1, apply: 'shadow'
    }));
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const page = await openAgent(fixture, element, 'projects');

    // The tab counts the skills in shadow, the new data skill among them; the rollout lists every part that is not live.
    expect(page.querySelector('[data-agent-tab="rollout"] .count')?.textContent?.trim()).toBe('2');
    await openTab(fixture, element, 'rollout');
    const rollout = () => page.querySelector('[data-rollout-not-live]') as HTMLElement;
    expect(rollout().querySelector('[data-rollout-skill="deadline-risk"]')?.textContent).toContain('Skill Deadline risk works in shadow');
    expect(rollout().querySelector('[data-rollout-skill="progress-note"]')).not.toBeNull();
    expect(rollout().querySelector('[data-rollout-trigger="project-created"]')?.textContent).toContain('Trigger project-created fires in shadow');
    expect(rollout().querySelector('[data-rollout-skill-candidate="progress-note"]')?.textContent)
      .toContain('Version 2 of Progress note runs in shadow next to version 1');

    // A trigger goes live here at once.
    (rollout().querySelector('[data-rollout-trigger-live="project-created"]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(requestBodies('POST', 'admin/agents/projects/triggers/project-created/shadow')).toEqual([{ mode: 'live', reason: null }]);
    expect(rollout().querySelector('[data-rollout-trigger="project-created"]')).toBeNull();

    // A skill in shadow goes live with its own status.
    await chooseStatus(fixture, rollout().querySelector('[data-rollout-skill="deadline-risk"]') as HTMLElement, 'deadline-risk', '[data-shadow-action="live"]');
    button(rollout(), 'Confirm live').click();
    await settle(fixture);
    expect(requestBodies('POST', 'admin/agents/projects/skills/deadline-risk/shadow')).toEqual([{ mode: 'live', reason: null }]);
    expect(rollout().querySelector('[data-rollout-skill="deadline-risk"]')).toBeNull();

    // A candidate is compared and decided in Build, where its comparison opens.
    (rollout().querySelector('[data-rollout-review="skill:progress-note"]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(page.querySelector('[data-agent-panel]')?.getAttribute('data-agent-panel')).toBe('build');
    expect(page.querySelector('[data-data-skill="progress-note"] [data-comparison]')?.textContent).toContain('Candidate (v2)');
  });

  it('shows in the guardrails what the code of an agent in code sets for its runs, read only', async () => {
    const script = runScenario();
    script.admin!.agents![0] = {
      ...script.admin!.agents![0],
      codeRunSettings: {
        accountability: 'subject-responsible', approvalPolicy: 'project-review', usesRules: false, skillsInChat: true, allowQuestions: true,
        triageProfile: 'project-triage', maxConcurrentRuns: 2, maxRunsPerSubjectPerDay: 20, dailyToolCalls: 100, dailyTokens: 300_000,
        maxTokensPerRun: 40_000, allowGatewayReads: true, usesMemory: true
      }
    };
    await configure(script);
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const page = await openAgent(fixture, element, 'projects', 'build');
    const fact = (name: string) => page.querySelector(`[data-build-section="guardrails"] [data-code-setting="${name}"]`)?.textContent?.replace(/\s+/g, ' ').trim();

    expect(page.querySelector('[data-code-run-settings]')?.textContent).toContain('Set in code');
    expect(fact('accountability')).toBe('The person responsible for the subject');
    expect(fact('way-of-working')).toContain('Triage with project-triage');
    expect(fact('approval-policy')).toBe('project-review');
    expect(fact('retries')).toBe('3');
    expect(fact('max-concurrent')).toBe('2');
    expect(fact('max-per-subject')).toBe('20');
    expect(fact('max-tokens')).toBe('40,000');
    expect(fact('skills-in-chat')).toBe('Yes');
    expect(fact('questions')).toBe('Yes');
    expect(fact('memory')).toBe('Yes');
    expect(fact('gateway-reads')).toBe('Yes');
    expect(fact('files')).toBe('No');
    // Nothing that code sets has an input here.
    expect(page.querySelector('[data-code-run-settings] input, [data-code-run-settings] select')).toBeNull();
  });

  it('opens the chat with the agent for a test, and says so when the administrator cannot use it there', async () => {
    await configure();
    await firstValueFrom(TestBed.inject(NhAssistantAdminApiService).createAgent({
      id: 'hidden-desk', displayName: 'Hidden desk', description: '', instructions: 'Help.', toolSelectors: [], mcpServerIds: [],
      requiredPolicy: null, autonomy: 'observe'
    }));
    const store = TestBed.inject(NhAssistantStore);
    const panel = TestBed.inject(NhAssistantPanelService);
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);

    let page = await openAgent(fixture, element, 'projects');
    (page.querySelector('[data-test-in-chat]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(panel.isOpen()).toBeTrue();
    expect(store.selectedAgentId()).toBe('projects');
    expect(store.activeConversation()).toBeNull();
    panel.close();

    (page.querySelector('[data-back]') as HTMLButtonElement).click();
    await settle(fixture);
    page = await openAgent(fixture, element, 'hidden-desk');
    if (store.agents().some(agent => agent.id === 'hidden-desk')) {
      // The mock offers agents created here in the chat; then the test opens it the same way.
      (page.querySelector('[data-test-in-chat]') as HTMLButtonElement).click();
      await settle(fixture);
      expect(store.selectedAgentId()).toBe('hidden-desk');
    } else {
      (page.querySelector('[data-test-in-chat]') as HTMLButtonElement).click();
      await settle(fixture);
      expect(page.querySelector('[data-test-unavailable]')?.textContent).toContain('You cannot use this agent in the chat');
    }
  });

  it('creates an agent that works on its own and starts in shadow once it has run settings', async () => {
    await configure(runScenario());
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);

    (element.querySelector('[data-new-agent]') as HTMLButtonElement).click();
    await settle(fixture);
    // Only an agent that works on its own may start in shadow.
    expect(element.querySelector('[data-starts-in-shadow]')).toBeNull();
    type(element, 'input[id$="-id"]', 'shadow-starter');
    type(element, 'input[id$="-name"]', 'Shadow starter');
    check(element, '[data-agent-use] input[value="background"]');
    await settle(fixture);
    expect(element.querySelector('[data-starts-in-shadow]')?.textContent).toContain('Start in shadow');
    check(element, '[data-starts-in-shadow] input');
    button(element, 'Save').click();
    await settle(fixture);
    expect(requestBodies('POST', 'admin/agents').at(-1)).toEqual(jasmine.objectContaining({ id: 'shadow-starter', startsInShadow: true }));

    const created = (await firstValueFrom(TestBed.inject(NhAssistantAdminApiService).getAgents())).find(agent => agent.id === 'shadow-starter')!;
    expect(created.shadow).toEqual(jasmine.objectContaining({ shadow: true, override: true }));
  }, formFlowTimeout);

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
    const row = element.querySelector('[data-agent-row="projects"]') as HTMLElement;

    expect(row.textContent).toContain('Project assistant');
    expect(row.textContent).toContain('Answers questions about projects.');
    expect(row.textContent).not.toContain('agents.projects');
    const page = await openAgent(fixture, element, 'projects', 'build');
    expect(page.querySelector('h3')?.textContent?.trim()).toBe('Project assistant');
    expect(page.querySelector('[data-required-policy]')?.textContent?.replace(/\s+/g, ' ').trim()).toBe('viewing projects');
    expect(page.querySelector('[data-tools-summary]')?.textContent).toContain('sample-api.project.*');
    const skill = page.querySelector('[data-code-skill="task-follow-up"]')!;
    expect(skill.querySelector('strong')?.textContent?.trim()).toBe('Task follow up');
    expect(skill.textContent).toContain('Follows up completed tasks.');

    // The editor shows the translations and sends the keys back while they are kept.
    const profile = () => page.querySelector('[data-build-section="profile"]') as HTMLElement;
    (page.querySelector('[data-edit-section="profile"]') as HTMLButtonElement).click();
    await settle(fixture);
    expect((profile().querySelector('input[id$="-name"]') as HTMLInputElement).value).toBe('Project assistant');
    expect((profile().querySelector('input[id$="-description"]') as HTMLInputElement).value).toBe('Answers questions about projects.');
    button(profile(), 'Save').click();
    await settle(fixture);
    expect(requestBodies('PUT', 'admin/agents/projects')).toEqual([jasmine.objectContaining({
      displayName: 'agents.projects.name',
      description: 'agents.projects.description'
    })]);

    // A changed text is the administrator's own.
    (page.querySelector('[data-edit-section="profile"]') as HTMLButtonElement).click();
    await settle(fixture);
    type(profile(), 'input[id$="-name"]', 'Projects helper');
    button(profile(), 'Save').click();
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
    const page = await openAgent(fixture, element, 'projects', 'build');

    expect(page.querySelector('[data-required-policy] code')?.textContent).toBe('app.project.view');
    const head = page.querySelector('.page-head') as HTMLElement;
    await chooseStatus(fixture, head, 'agent', '[data-state-action="paused"]');
    const note = head.querySelector('.change-form input') as HTMLInputElement;
    expect(head.querySelector(`label[for="${note.id}"]`)?.textContent).toContain('Note for other administrators (optional)');
    expect(head.querySelector(`#${note.getAttribute('aria-describedby')}`)?.textContent).toContain('The agent does not read it.');
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

    expect(translate.instant(shadowActionKey(codeAgent, 'code'))).toBe('Follow the code');
    expect(translate.instant(shadowActionKey(created, 'code'))).toBe('Back to its own setting');
    expect(translate.instant(shadowExplainKey(created, 'code', true), {
      skill: 'Kickoff',
      mode: translate.instant(ownShadowModeKey(created, 'kickoff'))
    })).toBe('The own setting of the skill Kickoff decides again: in shadow.');
    expect(translate.instant(shadowExplainKey(created, 'live', false))).toContain('New runs work live');
  });

  it('deletes an admin agent only after confirmation', async () => {
    await configure();
    await firstValueFrom(TestBed.inject(NhAssistantAdminApiService).createAgent({
      id: 'temporary', displayName: 'Temporary', description: '', instructions: '', toolSelectors: [], mcpServerIds: [],
      requiredPolicy: null, autonomy: 'observe'
    }));
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const page = await openAgent(fixture, element, 'temporary');

    await openMore(fixture, page);
    (page.querySelector('[data-delete-agent]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(page.querySelector('[data-confirming="delete"]')).not.toBeNull();
    expect(requestBodies('DELETE', 'admin/agents/temporary')).toEqual([]);

    button(page, 'Confirm delete').click();
    await settle(fixture);

    expect(requestBodies('DELETE', 'admin/agents/temporary').length).toBe(1);
    expect(element.querySelector('[data-agent-page]')).toBeNull();
    expect(element.querySelector('[data-agent-row="temporary"]')).toBeNull();
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

  it('asks a user name for basic authentication, the password again for the new mode and keeps it write-only', async () => {
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
    // The stored secret never follows the server to another authentication mode: the server asks for
    // the password, which the administrator enters.
    const [refused] = requestBodies('PUT', 'admin/mcp-servers/files') as Record<string, unknown>[];
    expect(Object.keys(refused)).not.toContain('secret');
    button(element, 'Replace').click();
    fixture.detectChanges();
    type(element, 'input[type="password"]', 'planner-password');
    button(element, 'Save').click();
    await settle(fixture);

    const bodies = requestBodies('PUT', 'admin/mcp-servers/files') as Record<string, unknown>[];
    expect(bodies.length).toBe(2);
    expect([bodies[1]['authMode'], bodies[1]['userName'], bodies[1]['secret']]).toEqual(['basic', 'planner', 'planner-password']);
    expect(element.textContent).toContain('User name and password');
    expect(element.textContent).toContain('Secret set');
    expect(element.innerHTML).not.toContain('planner-password');
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
