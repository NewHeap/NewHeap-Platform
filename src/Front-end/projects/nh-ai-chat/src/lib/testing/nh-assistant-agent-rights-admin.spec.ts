import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { readMayChangeAgentRights } from '../../../admin/src/agents/agent-rights';
import { NhAssistantAgentShadowVariantsComponent } from '../../../admin/src/agents/run-config/nh-assistant-agent-shadow-variants.component';
import { NhAssistantAgentWorkSetupComponent } from '../../../admin/src/agents/run-config/nh-assistant-agent-work-setup.component';
import { NhAssistantConsultationFormComponent } from '../../../admin/src/agents/run-config/nh-assistant-consultation-form.component';
import { NhAssistantRunSettingsFormComponent } from '../../../admin/src/agents/run-config/nh-assistant-run-settings-form.component';
import { NhAssistantSkillFormComponent } from '../../../admin/src/agents/run-config/nh-assistant-skill-form.component';
import { NhAssistantAdminTranslationMerger } from '../../../admin/src/i18n/nh-assistant-admin-translations';
import {
  NhAssistantAdminAgentEditorComponent,
  NhAssistantAdminAgentsComponent,
  NhAssistantAdminMcpServersComponent,
  NhAssistantAdminMcpToolsComponent,
  NhAssistantWithoutApprovalActionComponent
} from '../../../admin/src/public-api';
import { AdminAgent, AgentRunConfiguration, McpServer, RoutineCandidate, RunCatalog } from '../models/assistant-admin.models';
import { AgentPackage } from '../models/assistant-agent-package.models';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAdminApiService } from '../services/nh-assistant-admin-api.service';
import { openAdminAgent } from './nh-assistant-admin-navigation';

/**
 * `UseAgentRightsPolicy` in the administration: a person with the administration policy but without this one
 * builds and operates agents, but cannot change whose rights an agent uses, who reaches it, who decides its
 * proposals or what runs without approval. The administration shows those controls disabled with their values
 * and one hint, keeps everything else open and shows a refusal of the server at its field; with the flag true or
 * missing nothing changes.
 */

/** A form flow renders several times; one generous timeout covers it. */
const formFlowTimeout = 20_000;

const refusedAtField = 'Changing this takes permission to change agent rights.';

function scenario(mayChangeAgentRights?: boolean): NhAssistantMockScenario {
  return {
    agents: [{ id: 'planner', version: 1, displayNameKey: 'Planner', descriptionKey: 'Plans projects.', canMutate: true }],
    turns: [],
    subjectTypes: [
      { id: 'project', names: { en: 'Project' }, subjects: [] },
      { id: 'task', names: { en: 'Task' }, subjects: [] }
    ],
    admin: {
      tools: [
        { id: 'projects.complete-task', source: 'local', effect: 'idempotent-mutation', description: 'Complete a task.', allowWithoutApproval: true },
        { id: 'tasks.list', source: 'local', effect: 'read-only', description: 'Lists tasks.' }
      ],
      shadowVariantsAsData: { of: ['desk', 'plain'], maxPerAgent: 1 },
      consultationsAsData: {
        routes: ['asker-decides'],
        allowRequests: true,
        allowMcp: true,
        maxToolCalls: 8,
        maxTimeoutSeconds: 120,
        maxAnswerChars: 8_000,
        maxHistoryMessages: 40
      },
      agents: [
        {
          source: 'code', id: 'planner', displayName: 'Planner', description: 'Plans projects.', instructions: 'Plan.',
          toolSelectors: ['projects.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'propose', isEnabled: true,
          worksOnSubjectTypes: ['project', 'task'], worksOnAllSubjects: false,
          serviceAccount: 'operations',
          consultations: { 'asker-decides': ['task'] }
        },
        {
          source: 'admin', id: 'desk', displayName: 'Intake desk', description: 'Takes in projects.', instructions: 'Review.',
          toolSelectors: ['projects.*'], mcpServerIds: ['files'], requiredPolicy: 'app.use', autonomy: 'propose', isEnabled: true,
          worksOnSubjectTypes: ['project'], worksOnAllSubjects: false, worksOnApplication: true, runPolicy: 'app.manage', viewPolicy: 'app.manage',
          runConfiguration: {
            runSettings: { accountability: 'service-account', serviceAccountActorId: 'operations', approvalPolicy: 'review', allowGatewayReads: true },
            skills: [{ skillId: 'triage', description: 'Triages an intake.', instructions: 'Triage it.', approvalPolicy: 'review' }]
          },
          dataConsultations: [{ subjectType: 'project', description: 'Says whether a project can start.', minimumRole: 'participant', routes: ['asker-decides'], proposalPolicy: 'review', exposeToMcp: true }]
        },
        {
          source: 'admin', id: 'plain', displayName: 'Plain desk', description: 'Answers to itself.', instructions: 'Help.',
          toolSelectors: ['projects.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'propose', isEnabled: true,
          worksOnSubjectTypes: ['project'], worksOnAllSubjects: false
        }
      ],
      mcpServers: [
        {
          id: 'files', displayName: 'Files', url: 'https://files.example.com/mcp', authMode: 'none', headerName: null, requiredPolicy: null, isEnabled: true,
          remoteTools: [
            { remoteName: 'readFile', description: 'Reads a file', inputSchemaHash: 'a1', readOnlyHint: true },
            { remoteName: 'writeFile', description: 'Writes a file', inputSchemaHash: 'a2', readOnlyHint: false }
          ]
        },
        { id: 'calendar', displayName: 'Calendar', url: 'https://calendar.example.com/mcp', authMode: 'none', headerName: null, requiredPolicy: null, isEnabled: true },
        {
          id: 'crm', displayName: 'CRM', url: 'https://crm.example.com/mcp', authMode: 'oauth', connectionScope: 'organization', headerName: null,
          requiredPolicy: null, isEnabled: true, oauth: { organizationAccountLabel: 'crm-service', confirmAuthorization: true }
        },
        {
          id: 'mail', displayName: 'Mail', url: 'https://mail.example.com/mcp', authMode: 'oauth', connectionScope: 'user', headerName: null,
          requiredPolicy: null, isEnabled: true, oauth: { confirmAuthorization: true }
        }
      ],
      accounts: [{ actorId: 'operations', displayName: 'Operations', detail: null }],
      runCatalog: {
        triggerEvents: [],
        subjectSets: [],
        ruleHandlers: [],
        approvalPolicies: ['review'],
        profiles: [],
        agentPolicies: [{ name: 'app.manage', description: 'Managers.' }],
        accountSearch: false,
        ...(mayChangeAgentRights === undefined ? {} : { mayChangeAgentRights })
      }
    }
  };
}

function flush(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve));
}

async function configure(mayChangeAgentRights?: boolean): Promise<NhAssistantMockBackend> {
  TestBed.configureTestingModule({
    providers: [
      provideTranslateService({ fallbackLang: 'en' }),
      provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
      provideNhAssistantMockApi(scenario(mayChangeAgentRights))
    ]
  });
  // The forms come without the administration around them, which would start its texts.
  TestBed.inject(NhAssistantAdminTranslationMerger).start();
  await firstValueFrom(TestBed.inject(TranslateService).use('en'));
  return TestBed.inject(NhAssistantMockBackend);
}

async function settle(fixture: ComponentFixture<unknown>): Promise<HTMLElement> {
  for (let round = 0; round < 10; round++) {
    fixture.detectChanges();
    await flush();
  }
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

function text(element: Element | null | undefined): string {
  return element?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

function type(scope: Element, selector: string, value: string): void {
  const input = scope.querySelector(selector) as HTMLInputElement | HTMLTextAreaElement;
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

/** Whether a control does not change here, by itself or through a fieldset around it. */
function disabled(scope: Element, selector: string): boolean[] {
  const controls = Array.from(scope.querySelectorAll(selector));
  expect(controls.length).withContext(selector).toBeGreaterThan(0);
  return controls.map(control => control.matches(':disabled'));
}

function submit(scope: Element): void {
  (scope.querySelector('button[type="submit"]') as HTMLButtonElement).click();
}

function requestBodies(method: string, path: string): any[] {
  return TestBed.inject(NhAssistantMockBackend).requests
    .filter(item => item.method === method && item.path === path)
    .map(item => item.body);
}

function writes(): string[] {
  return TestBed.inject(NhAssistantMockBackend).requests
    .filter(item => item.method !== 'GET')
    .map(item => `${item.method} ${item.path}`);
}

function admin(): NhAssistantAdminApiService {
  return TestBed.inject(NhAssistantAdminApiService);
}

async function catalog(): Promise<RunCatalog> {
  return firstValueFrom(admin().getRunCatalog());
}

async function configuration(agentId: string): Promise<AgentRunConfiguration> {
  return firstValueFrom(admin().getAgentConfiguration(agentId));
}

async function agent(agentId: string): Promise<AdminAgent> {
  return (await firstValueFrom(admin().getAgents())).find(item => item.id === agentId)!;
}

async function servers(): Promise<McpServer[]> {
  return firstValueFrom(admin().getMcpServers());
}

async function runSettingsForm(agentId: string): Promise<{ fixture: ComponentFixture<NhAssistantRunSettingsFormComponent>; form: HTMLElement }> {
  const fixture = TestBed.createComponent(NhAssistantRunSettingsFormComponent);
  fixture.componentRef.setInput('agentId', agentId);
  fixture.componentRef.setInput('catalog', await catalog());
  fixture.componentRef.setInput('settings', (await configuration(agentId)).runSettings);
  return { fixture, form: await settle(fixture) };
}

async function consultationForm(): Promise<{ fixture: ComponentFixture<NhAssistantConsultationFormComponent>; form: HTMLElement }> {
  const fixture = TestBed.createComponent(NhAssistantConsultationFormComponent);
  const config = await configuration('desk');
  fixture.componentRef.setInput('agentId', 'desk');
  fixture.componentRef.setInput('catalog', await catalog());
  fixture.componentRef.setInput('ceiling', config.consultationCeiling);
  fixture.componentRef.setInput('accountability', 'service-account');
  fixture.componentRef.setInput('toolSelectors', config.toolSelectors);
  fixture.componentRef.setInput('consultation', config.dataConsultations![0]);
  return { fixture, form: await settle(fixture) };
}

async function agentEditor(agentId: string | null): Promise<{ fixture: ComponentFixture<NhAssistantAdminAgentEditorComponent>; form: HTMLElement; saved: AdminAgent[] }> {
  const fixture = TestBed.createComponent(NhAssistantAdminAgentEditorComponent);
  const saved: AdminAgent[] = [];
  fixture.componentInstance.saved.subscribe(item => saved.push(item));
  fixture.componentRef.setInput('agent', agentId ? await agent(agentId) : null);
  fixture.componentRef.setInput('catalog', await catalog());
  fixture.componentRef.setInput('servers', await servers());
  return { fixture, form: await settle(fixture), saved };
}

/** The checkbox of an MCP server in the agent editor. */
function serverBox(form: HTMLElement, serverId: string): HTMLInputElement {
  const label = Array.from(form.querySelectorAll('[data-agent-mcp-servers] label.check'))
    .find(item => item.querySelector('.mono')?.textContent?.trim() === serverId);
  return label!.querySelector('input') as HTMLInputElement;
}

function candidate(toolId: string): RoutineCandidate {
  return { agentId: 'planner', agentDisplayName: 'Planner', agentSource: 'code', toolId, candidate: true, unmetThresholds: [] } as unknown as RoutineCandidate;
}

async function chooseSwitch(fixture: ComponentFixture<unknown>, element: HTMLElement, mode: string): Promise<void> {
  (element.querySelector(`[data-without-approval-action="${mode}"]`) as HTMLButtonElement).click();
  await settle(fixture);
  (element.querySelector('[data-without-approval-confirm]') as HTMLButtonElement).click();
  await settle(fixture);
}

function deskPackage(runSettings: Record<string, unknown>): AgentPackage {
  return {
    format: 'newheap.assistant.agent-package',
    formatVersion: 1,
    package: { id: 'desks', version: 1, description: 'A desk.' },
    agents: [{
      id: 'imported-desk',
      source: 'admin',
      definition: { displayName: 'Imported desk', description: 'A desk.', instructions: 'Review.', toolSelectors: ['projects.*'], autonomy: 'propose', worksOnSubjectTypes: ['project'], worksOnAllSubjects: false, worksOnApplication: false },
      runSettings
    }]
  } as unknown as AgentPackage;
}

async function checkPackage(fixture: ComponentFixture<unknown>, element: HTMLElement, agentPackage: AgentPackage): Promise<HTMLElement> {
  (element.querySelector('[data-import-package]') as HTMLButtonElement).click();
  await settle(fixture);
  type(element, '[data-package-text]', JSON.stringify(agentPackage));
  await settle(fixture);
  (element.querySelector('[data-import-preview-action]') as HTMLButtonElement).click();
  await settle(fixture);
  return element.querySelector('nh-assistant-agent-package-import-dialog') as HTMLElement;
}

describe('A person who may not change an agent\'s rights', () => {
  let backend: NhAssistantMockBackend;

  beforeEach(async () => {
    backend = await configure(false);
  });

  it('sees who the agent answers to and who approves, disabled, and still saves the other run settings', async () => {
    const { fixture, form } = await runSettingsForm('desk');

    expect(disabled(form, 'input[name$="-accountability"]')).toEqual([true, true, true]);
    expect((form.querySelector('input[name$="-accountability"]:checked') as HTMLInputElement).value).toBe('service-account');
    const account = form.querySelector('[id$="-service-account"]') as HTMLInputElement;
    expect(account.matches(':disabled')).toBeTrue();
    expect(account.value).toBe('operations');
    expect(disabled(form, '[id$="-policy"]')).toEqual([true]);
    expect(disabled(form, 'input[name$="-elevated"]')).toEqual([true, true]);
    expect(disabled(form, '[data-advanced] label.check input[type="checkbox"]')).toEqual([true, true]);
    expect((form.querySelector('[data-advanced] label.check input[type="checkbox"]') as HTMLInputElement).checked).toBeTrue();
    // What is not about rights stays open.
    expect(disabled(form, '[id$="-run-tokens"], [id$="-retries"]')).toEqual([false, false]);

    // One calm hint in each group, which the group describes itself with.
    const hint = form.querySelector('[data-agent-rights-hint]') as HTMLElement;
    expect(text(hint)).toBe('Someone with permission to change agent rights can change what is disabled here.');
    expect(form.querySelector('fieldset.group')?.getAttribute('aria-describedby')).toContain(hint.id);
    expect(form.querySelectorAll('[data-agent-rights-hint]').length).toBe(3);

    type(form, '[id$="-run-tokens"]', '5000');
    submit(form);
    await settle(fixture);

    expect(form.querySelector('[data-form-error]')).toBeNull();
    expect(requestBodies('PUT', 'admin/agents/desk/run-settings')).toEqual([jasmine.objectContaining({
      accountability: 'service-account',
      serviceAccountActorId: 'operations',
      approvalPolicy: 'review',
      allowGatewayReads: true,
      maxTokensPerRun: 5000
    })]);
    expect((await configuration('desk')).runSettings?.maxTokensPerRun).toBe(5000);
  }, formFlowTimeout);

  it('gives a new agent plain run settings that answer to the agent itself', async () => {
    const { fixture, form } = await runSettingsForm('plain');

    expect((form.querySelector('input[name$="-accountability"]:checked') as HTMLInputElement).value).toBe('agent');
    expect(disabled(form, 'input[name$="-accountability"]')).toEqual([true, true, true]);
    expect(form.querySelector('[id$="-service-account"]')).toBeNull();

    submit(form);
    await settle(fixture);

    expect(form.querySelector('[data-form-error]')).toBeNull();
    expect((await configuration('plain')).runSettings?.accountability).toBe('agent');
  }, formFlowTimeout);

  it('sets up an agent that answers to itself in the guided setup', async () => {
    const fixture = TestBed.createComponent(NhAssistantAgentWorkSetupComponent);
    fixture.componentRef.setInput('agentId', 'plain');
    fixture.componentRef.setInput('catalog', await catalog());
    const setup = await settle(fixture);
    fixture.componentInstance.step.set('who');
    await settle(fixture);

    expect((setup.querySelector('input[name$="-accountability"]:checked') as HTMLInputElement).value).toBe('agent');
    expect(disabled(setup, 'input[name$="-accountability"]')).toEqual([true, true, true]);
    expect(setup.querySelector('[data-agent-rights-hint]')).not.toBeNull();

    fixture.componentInstance.step.set('limits');
    await settle(fixture);
    expect(disabled(setup, '[id$="-policy"]')).toEqual([true]);
  }, formFlowTimeout);

  it('sees the approval policy of a skill, disabled, and still changes the skill', async () => {
    const fixture = TestBed.createComponent(NhAssistantSkillFormComponent);
    const config = await configuration('desk');
    fixture.componentRef.setInput('agentId', 'desk');
    fixture.componentRef.setInput('catalog', await catalog());
    fixture.componentRef.setInput('toolSelectors', config.toolSelectors);
    fixture.componentRef.setInput('skill', config.dataSkills[0]);
    const form = await settle(fixture);

    expect(disabled(form, '[id$="-skill-policy"]')).toEqual([true]);
    expect(form.querySelector('[data-agent-rights-hint]')).not.toBeNull();

    type(form, 'textarea', 'Triages an intake quickly.');
    submit(form);
    await settle(fixture);

    expect(form.querySelector('[data-form-error]')).toBeNull();
    expect(requestBodies('PUT', 'admin/agents/desk/skills/triage')).toEqual([jasmine.objectContaining({ approvalPolicy: 'review' })]);
  }, formFlowTimeout);

  it('sees who may ask a consultation and who decides, disabled, and still changes what it is about', async () => {
    const { fixture, form } = await consultationForm();
    fixture.componentInstance.advancedOpen.set(true);
    await settle(fixture);

    expect(disabled(form, 'input[name$="-minimum-role"]')).toEqual([true, true, true]);
    expect((form.querySelector('input[name$="-minimum-role"]:checked') as HTMLInputElement).value).toBe('participant');
    expect(disabled(form, '[id$="-asker-policy"]')).toEqual([true]);
    expect(disabled(form, 'input[name$="-mode"]')).toEqual([true, true]);
    expect(disabled(form, '[data-consultation-routes] input')).toEqual([true, true, true]);
    expect(disabled(form, '[id$="-proposal-role"], [id$="-proposal-policy"]')).toEqual([true, true]);
    expect(disabled(form, '[data-consultation-mcp] input')).toEqual([true]);
    expect(disabled(form, '[id$="-description"], [data-consultation-limits] input')).not.toContain(true);
    expect(form.querySelectorAll('[data-agent-rights-hint]').length).toBe(3);

    type(form, '[id$="-description"]', 'Says whether a draft project can start.');
    submit(form);
    await settle(fixture);

    expect(form.querySelector('[data-form-error]')).toBeNull();
    expect(requestBodies('PUT', 'admin/agents/desk/consultations/project')).toEqual([jasmine.objectContaining({
      description: 'Says whether a draft project can start.',
      minimumRole: 'participant',
      routes: ['asker-decides'],
      proposalPolicy: 'review',
      exposeToMcp: true
    })]);
  }, formFlowTimeout);

  it('sees an agent\'s policies, disabled, adds no MCP server and still removes one', async () => {
    const { fixture, form, saved } = await agentEditor('desk');

    expect(disabled(form, 'input[id$="-policy"], [id$="-run-policy"], [id$="-view-policy"]')).toEqual([true, true, true]);
    expect((form.querySelector('input[id$="-policy"]') as HTMLInputElement).value).toBe('app.use');
    expect(serverBox(form, 'calendar').disabled).toBeTrue();
    // Tools and autonomy only take the policy when they widen an agent that lends rights, which the server knows.
    expect(disabled(form, '[id$="-autonomy"], [data-agent-tool-selectors] input[type="search"]')).toEqual([false, false]);
    expect(form.querySelector('[data-agent-mcp-servers] [data-agent-rights-hint]')).not.toBeNull();

    // Removing an assignment only takes rights away.
    expect(serverBox(form, 'files').disabled).toBeFalse();
    serverBox(form, 'files').click();
    fixture.detectChanges();
    expect(serverBox(form, 'files').disabled).toBeFalse();
    submit(form);
    await settle(fixture);

    expect(form.querySelector('.message.error')).toBeNull();
    expect(writes()).toEqual(['PUT admin/agents/desk', 'DELETE admin/mcp-servers/files/agents/desk']);
    expect(saved.map(item => item.mcpServerIds)).toEqual([[]]);
  }, formFlowTimeout);

  it('still names a required policy for a new agent, which only narrows who reaches it', async () => {
    const { fixture, form } = await agentEditor(null);

    expect(disabled(form, 'input[id$="-policy"]')).toEqual([false]);
    expect(disabled(form, '[id$="-run-policy"], [id$="-view-policy"]')).toEqual([true, true]);
    expect(serverBox(form, 'files').disabled).toBeTrue();

    type(form, '[id$="-id"]', 'new-desk');
    type(form, '[id$="-name"]', 'New desk');
    type(form, 'input[id$="-policy"]', 'app.use');
    fixture.detectChanges();
    submit(form);
    await settle(fixture);

    expect(form.querySelector('.message.error')).toBeNull();
    expect((await agent('new-desk')).requiredPolicy).toBe('app.use');
  }, formFlowTimeout);

  it('reads the server\'s refusal of more tools and autonomy for an agent that lends rights at their fields', async () => {
    const { fixture, form } = await agentEditor('desk');

    const autonomy = form.querySelector('[id$="-autonomy"]') as HTMLSelectElement;
    autonomy.value = 'execute';
    autonomy.dispatchEvent(new Event('change'));
    type(form, '[data-agent-tool-selectors] .inline input', 'tasks.*');
    fixture.detectChanges();
    (form.querySelector('[data-agent-tool-selectors] .inline button') as HTMLButtonElement).click();
    fixture.detectChanges();
    submit(form);
    await settle(fixture);

    expect(text(form.querySelector('[id$="-autonomy-error"]'))).toBe(refusedAtField);
    expect(text(form.querySelector('[id$="-tool-selectors-error"]'))).toBe(refusedAtField);
    expect(autonomy.getAttribute('aria-invalid')).toBe('true');
    expect(text(form.querySelector('.message.error'))).toContain('takes a permission you do not have');
  }, formFlowTimeout);

  it('sees the technical account and the routes of an agent, disabled, on its page', async () => {
    const fixture = TestBed.createComponent(NhAssistantAdminAgentsComponent);
    const element = await settle(fixture);
    const page = await openAdminAgent(fixture, element, 'planner', 'build');
    await settle(fixture);

    const routes = page.querySelector('nh-assistant-agent-consultation-routes') as HTMLElement;
    expect(disabled(routes, '[data-route-action]')).not.toContain(false);
    const action = routes.querySelector('[data-route-action]') as HTMLButtonElement;
    expect(routes.querySelector(`[id="${action.getAttribute('aria-describedby')}"]`)?.hasAttribute('data-agent-rights-hint')).toBeTrue();

    const control = page.querySelector('[data-service-account-control]') as HTMLElement;
    expect(text(control.querySelector('[data-service-account]'))).toContain('Operations');
    expect(disabled(control, '[data-account-action]')).toEqual([true]);
    expect(control.querySelector('[data-agent-rights-hint]')).not.toBeNull();
  }, formFlowTimeout);

  it('creates a shadow variant only of an agent whose MCP servers and policies it need not copy', async () => {
    const fixture = TestBed.createComponent(NhAssistantAgentShadowVariantsComponent);
    fixture.componentRef.setInput('agent', await agent('desk'));
    const element = await settle(fixture);
    const add = () => element.querySelector('[data-open-form="shadow-variant"]') as HTMLButtonElement;

    expect(add().disabled).toBeTrue();
    expect(add().getAttribute('aria-describedby')).toBe(element.querySelector('[data-agent-rights-hint]')?.id ?? null);

    await firstValueFrom(admin().saveRunSettings('plain', { accountability: 'agent' }));
    fixture.componentRef.setInput('agent', await agent('plain'));
    await settle(fixture);
    expect(add().disabled).toBeFalse();
    expect(element.querySelector('[data-agent-rights-hint]')).toBeNull();
    add().click();
    await settle(fixture);
    (element.querySelector('[data-form="shadow-variant"] button[type="submit"]') as HTMLButtonElement).click();
    await settle(fixture);

    expect(element.querySelector('[data-form-error]')).toBeNull();
    expect((await agent('plain-variant')).shadowOf).toBe('plain');
  }, formFlowTimeout);

  it('takes a tool to shadow and off without approval, but not live', async () => {
    const fixture = TestBed.createComponent(NhAssistantWithoutApprovalActionComponent);
    fixture.componentRef.setInput('candidate', candidate('projects.complete-task'));
    const element = await settle(fixture);
    const modes = () => Array.from(element.querySelectorAll<HTMLButtonElement>('[data-without-approval-action]')).map(item => [item.getAttribute('data-without-approval-action'), item.disabled]);

    expect(modes()).toEqual([['shadow', false]]);
    expect(element.querySelector('[data-agent-rights-hint]')).toBeNull();

    await chooseSwitch(fixture, element, 'shadow');
    expect(element.querySelector('[data-without-approval-switch]')?.getAttribute('data-without-approval-switch-mode')).toBe('shadow');
    expect(modes()).toEqual([['live', true], ['off', false]]);
    const live = element.querySelector('[data-without-approval-action="live"]') as HTMLButtonElement;
    const hint = element.querySelector('[data-agent-rights-hint]') as HTMLElement;
    expect(live.getAttribute('aria-describedby')).toBe(hint.id);

    await chooseSwitch(fixture, element, 'off');
    expect(element.querySelector('[data-without-approval-switch]')?.getAttribute('data-without-approval-switch-mode')).toBe('off');
    expect(requestBodies('PUT', 'admin/agents/planner/tools/projects.complete-task/without-approval').map(body => body.mode)).toEqual(['shadow', 'off']);
  }, formFlowTimeout);

  it('is read once for the rows of a page that ask at the same time, and again later', async () => {
    const read = () => TestBed.runInInjectionContext(() => readMayChangeAgentRights());
    const reads = () => TestBed.inject(NhAssistantMockBackend).requests.filter(item => item.path === 'admin/run-catalog').length;

    const first = read();
    const second = read();
    // Until the answer is there the server decides.
    expect([first(), second()]).toEqual([true, true]);
    await new Promise(resolve => setTimeout(resolve, 20));

    expect(reads()).toBe(1);
    expect([first(), second()]).toEqual([false, false]);

    backend.setMayChangeAgentRights(true);
    read();
    await new Promise(resolve => setTimeout(resolve, 20));
    expect(reads()).toBe(2);
    expect(first()).toBeTrue();
  }, formFlowTimeout);

  it('adds and connects no MCP server and sees its settings read-only, but still tests, syncs and deletes', async () => {
    const fixture = TestBed.createComponent(NhAssistantAdminMcpServersComponent);
    const element = await settle(fixture);
    const card = (name: string) => Array.from(element.querySelectorAll('.card')).find(item => text(item.querySelector('strong')) === name) as HTMLElement;
    const named = (scope: Element, label: string) => Array.from(scope.querySelectorAll('button')).find(item => text(item) === label) as HTMLButtonElement;

    expect((element.querySelector('[data-mcp-new]') as HTMLButtonElement).disabled).toBeTrue();
    expect((card('CRM').querySelector('[data-mcp-connect]') as HTMLButtonElement).disabled).toBeTrue();
    // A person's own connection lends nobody's rights, so it stays open.
    expect((card('Mail').querySelector('[data-mcp-connect]') as HTMLButtonElement).disabled).toBeFalse();
    expect(element.querySelector('[data-agent-rights-hint]')).not.toBeNull();
    expect(named(card('Files'), 'Test connection').disabled).toBeFalse();
    expect(named(card('Files'), 'Tools').disabled).toBeFalse();

    (card('Files').querySelector('[data-mcp-edit]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(text(element.querySelector('h3'))).toContain('Files');
    const fields = element.querySelector('[data-server-fields]') as HTMLFieldSetElement;
    expect(fields.disabled).toBeTrue();
    expect(disabled(fields, 'input, select, button')).not.toContain(false);
    expect((fields.querySelector('[id$="-url"]') as HTMLInputElement).value).toBe('https://files.example.com/mcp');
    expect(disabled(element, 'button[type="submit"]')).toEqual([true]);
    expect(element.querySelector('[data-agent-rights-hint]')).not.toBeNull();
    named(element, 'Cancel').click();
    await settle(fixture);

    named(card('Calendar'), 'Delete').click();
    await settle(fixture);
    named(card('Calendar'), 'Confirm delete').click();
    await settle(fixture);
    expect(writes()).toEqual(['DELETE admin/mcp-servers/calendar']);
    expect(card('Calendar')).toBeUndefined();
  }, formFlowTimeout);

  it('disables a tool and changes a disabled one, but enables none', async () => {
    backend.setMayChangeAgentRights(true);
    await firstValueFrom(admin().syncMcpServer('files'));
    await firstValueFrom(admin().updateMcpTool('files', 'readFile', { isEnabled: true, effect: 'read-only', descriptionOverride: null }));
    backend.setMayChangeAgentRights(false);

    const fixture = TestBed.createComponent(NhAssistantAdminMcpToolsComponent);
    fixture.componentRef.setInput('server', (await servers()).find(server => server.id === 'files'));
    const element = await settle(fixture);
    const [enabled, off] = Array.from(element.querySelectorAll<HTMLElement>('.card'));
    const box = (card: HTMLElement) => card.querySelector('input[type="checkbox"]') as HTMLInputElement;
    const save = (card: HTMLElement) => card.querySelector('.card-actions button') as HTMLButtonElement;

    // A disabled tool stays disabled, and its settings still change: it is offered to nobody.
    expect(box(off).disabled).toBeTrue();
    expect(disabled(off, 'select, input[type="text"]')).toEqual([false, false]);
    // An enabled tool can only be disabled: a save that leaves it enabled would be refused.
    expect(box(enabled).disabled).toBeFalse();
    expect(disabled(enabled, 'select, input[type="text"]')).toEqual([true, true]);
    expect(element.querySelector('[data-agent-rights-hint]')).not.toBeNull();
    // Syncing only reads what the server lists.
    expect(disabled(element, '.section-header .primary')).toEqual([false]);

    box(enabled).click();
    await settle(fixture);
    expect(disabled(enabled, 'select, input[type="text"]')).toEqual([false, false]);
    save(enabled).click();
    await settle(fixture);

    expect(element.querySelector('.message.error')).toBeNull();
    expect(requestBodies('PUT', 'admin/mcp-servers/files/tools/readFile').at(-1)).toEqual(jasmine.objectContaining({ isEnabled: false }));
    expect((await firstValueFrom(admin().getMcpTools('files'))).find(tool => tool.remoteName === 'readFile')?.isEnabled).toBeFalse();
    expect(box(enabled).disabled).toBeTrue();
  }, formFlowTimeout);

  it('reads which parts of a package change rights and that its import takes that permission', async () => {
    const fixture = TestBed.createComponent(NhAssistantAdminAgentsComponent);
    const element = await settle(fixture);
    const dialog = await checkPackage(fixture, element, deskPackage({ accountability: 'agent', approvalPolicy: 'review' }));

    expect(text(dialog.querySelector('[data-import-part="runSettings"] [data-part-rights]'))).toBe('Changes agent rights: Approval policy.');
    expect(dialog.querySelector('[data-import-part="agent"] [data-part-rights]')).toBeNull();
    expect(text(dialog.querySelector('[data-import-rights-required]'))).toContain('Run settings');
    expect(text(dialog.querySelector('[data-import-rights-required]'))).toContain('takes permission to change them');
    const action = dialog.querySelector('[data-import-apply-action]') as HTMLButtonElement;
    expect(action.disabled).toBeTrue();
    expect(action.getAttribute('aria-describedby')).toBe(dialog.querySelector('[data-import-rights-required]')?.id ?? null);
  }, formFlowTimeout);

  it('still imports a package that changes no rights', async () => {
    const fixture = TestBed.createComponent(NhAssistantAdminAgentsComponent);
    const element = await settle(fixture);
    const dialog = await checkPackage(fixture, element, deskPackage({ accountability: 'agent' }));

    expect(dialog.querySelector('[data-part-rights]')).toBeNull();
    expect(dialog.querySelector('[data-import-rights-required]')).toBeNull();
    (dialog.querySelector('[data-import-apply-action]') as HTMLButtonElement).click();
    await settle(fixture);

    expect(dialog.querySelector('[data-import-done]')).not.toBeNull();
    expect((await agent('imported-desk')).displayName).toBe('Imported desk');
  }, formFlowTimeout);
});

describe('A refusal of a change of an agent\'s rights', () => {
  it('shows at the field when the catalog still said yes', async () => {
    const backend = await configure(true);
    const { fixture, form } = await runSettingsForm('desk');

    // The person's permission went while the form was open.
    backend.setMayChangeAgentRights(false);
    const itself = form.querySelector('input[name$="-accountability"][value="agent"]') as HTMLInputElement;
    expect(itself.disabled).toBeFalse();
    itself.click();
    await settle(fixture);
    // An agent that answers to itself reads through no gateway.
    (form.querySelector('[data-advanced] label.check input[type="checkbox"]') as HTMLInputElement).click();
    await settle(fixture);
    submit(form);
    await settle(fixture);

    expect(text(form.querySelector('[id$="-accountability-error"]'))).toBe(refusedAtField);
    expect(text(form.querySelector('[data-form-error]'))).toContain('takes a permission you do not have');
    expect((await configuration('desk')).runSettings?.accountability).toBe('service-account');
  }, formFlowTimeout);

  it('shows at the account of an agent in code when the page still said yes', async () => {
    const backend = await configure(true);
    const fixture = TestBed.createComponent(NhAssistantAdminAgentsComponent);
    const element = await settle(fixture);
    const page = await openAdminAgent(fixture, element, 'planner', 'build');
    await settle(fixture);
    (page.querySelector('[data-account-action="set"]') as HTMLButtonElement).click();
    await settle(fixture);

    backend.setMayChangeAgentRights(false);
    type(page, '[data-account-form] input', 'another-account');
    await settle(fixture);
    submit(page.querySelector('[data-account-form]') as HTMLElement);
    await settle(fixture);

    expect(text(page.querySelector('[data-account-error]'))).toBe(refusedAtField);
  }, formFlowTimeout);
});

for (const [name, flag] of [['true', true], ['missing', undefined]] as const) {
  describe(`With the flag ${name}`, () => {
    beforeEach(async () => {
      const backend = await configure(flag);
      if (flag === undefined) {
        // A server from before the policy does not say.
        const original = backend.fetch;
        spyOn(backend, 'fetch').and.callFake(async (input: string, init: RequestInit) => {
          const response = await original(input, init);
          if (!input.endsWith('/admin/run-catalog')) {
            return response;
          }
          const { mayChangeAgentRights: _, ...body } = await response.json() as RunCatalog;
          return new Response(JSON.stringify(body), { status: response.status, headers: { 'Content-Type': 'application/json' } });
        });
      }
    });

    it('offers every control as before', async () => {
      if (flag === undefined) {
        expect((await catalog()).mayChangeAgentRights).toBeUndefined();
      }

      const settings = await runSettingsForm('desk');
      expect(disabled(settings.form, 'input[name$="-accountability"], [id$="-service-account"], [id$="-policy"], input[name$="-elevated"], [data-advanced] label.check input[type="checkbox"]')).not.toContain(true);
      expect(settings.form.querySelector('[data-agent-rights-hint]')).toBeNull();

      const consultation = await consultationForm();
      consultation.fixture.componentInstance.advancedOpen.set(true);
      await settle(consultation.fixture);
      expect(disabled(consultation.form, 'input[name$="-minimum-role"], [id$="-asker-policy"], input[name$="-mode"], [id$="-proposal-role"], [id$="-proposal-policy"], [data-consultation-mcp] input')).not.toContain(true);
      expect(consultation.form.querySelector('[data-agent-rights-hint]')).toBeNull();

      const editor = await agentEditor('desk');
      expect(disabled(editor.form, 'input[id$="-policy"], [id$="-run-policy"], [id$="-view-policy"]')).toEqual([false, false, false]);
      expect(serverBox(editor.form, 'calendar').disabled).toBeFalse();
      expect(editor.form.querySelector('[data-agent-rights-hint]')).toBeNull();
    }, formFlowTimeout);

    it('starts new run settings with a technical account, as before', async () => {
      const { form } = await runSettingsForm('plain');

      expect((form.querySelector('input[name$="-accountability"]:checked') as HTMLInputElement).value).toBe('service-account');
    }, formFlowTimeout);

    it('takes a tool live without approval', async () => {
      const fixture = TestBed.createComponent(NhAssistantWithoutApprovalActionComponent);
      fixture.componentRef.setInput('candidate', candidate('projects.complete-task'));
      const element = await settle(fixture);

      await chooseSwitch(fixture, element, 'shadow');
      expect((element.querySelector('[data-without-approval-action="live"]') as HTMLButtonElement).disabled).toBeFalse();
      expect(element.querySelector('[data-agent-rights-hint]')).toBeNull();
      await chooseSwitch(fixture, element, 'live');

      expect(element.querySelector('[data-without-approval-switch]')?.getAttribute('data-without-approval-switch-mode')).toBe('live');
    }, formFlowTimeout);

    it('adds, connects and edits MCP servers and enables their tools', async () => {
      const fixture = TestBed.createComponent(NhAssistantAdminMcpServersComponent);
      const element = await settle(fixture);

      expect((element.querySelector('[data-mcp-new]') as HTMLButtonElement).disabled).toBeFalse();
      expect((element.querySelector('[data-mcp-connect]') as HTMLButtonElement).disabled).toBeFalse();
      expect(text(element.querySelector('[data-mcp-edit]'))).toBe('Edit');
      expect(element.querySelector('[data-agent-rights-hint]')).toBeNull();

      (element.querySelector('[data-mcp-edit]') as HTMLButtonElement).click();
      await settle(fixture);
      expect((element.querySelector('[data-server-fields]') as HTMLFieldSetElement).disabled).toBeFalse();
      expect(disabled(element, 'button[type="submit"]')).toEqual([false]);

      await firstValueFrom(admin().syncMcpServer('files'));
      const tools = TestBed.createComponent(NhAssistantAdminMcpToolsComponent);
      tools.componentRef.setInput('server', (await servers()).find(server => server.id === 'files'));
      const toolList = await settle(tools);
      expect(disabled(toolList, '.card input[type="checkbox"], .card select')).not.toContain(true);
    }, formFlowTimeout);

    it('names what a package changes of an agent\'s rights and imports it', async () => {
      const fixture = TestBed.createComponent(NhAssistantAdminAgentsComponent);
      const element = await settle(fixture);
      const dialog = await checkPackage(fixture, element, deskPackage({ accountability: 'agent', approvalPolicy: 'review' }));

      expect(text(dialog.querySelector('[data-part-rights]'))).toBe('Changes agent rights: Approval policy.');
      expect(dialog.querySelector('[data-import-rights-required]')).toBeNull();
      (dialog.querySelector('[data-import-apply-action]') as HTMLButtonElement).click();
      await settle(fixture);

      expect(dialog.querySelector('[data-import-done]')).not.toBeNull();
    }, formFlowTimeout);
  });
}
