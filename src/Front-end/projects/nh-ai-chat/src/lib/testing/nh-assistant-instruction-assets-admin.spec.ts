import { Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { NhAssistantAdminAgentsComponent } from '../../../admin/src/public-api';
import { NH_ASSISTANT_TRANSLATIONS } from '../i18n/nh-assistant-translations';
import { NH_ASSISTANT_ERROR_CODES } from '../i18n/nh-assistant-error-codes';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAdminApiService } from '../services/nh-assistant-admin-api.service';
import { NhAssistantApiError } from '../services/nh-assistant-transport';

/**
 * Instructions from the application's text assets in the administration (S7-1): the mock API checks
 * a reference as the server does, the skill form and the agent editor choose between text written
 * here and one of the application's texts, show the chosen text read-only with its hash, and show
 * the problem of a reference that no longer resolves.
 */

/** Each searchable picker waits for its search, so a form flow takes several seconds. */
const formFlowTimeout = 20_000;

const plannerText = 'Name the task that comes next and why.';
const plannerTextV2 = 'Name the two tasks that come next and why.';

function scenario(assets = true): NhAssistantMockScenario {
  return {
    agents: [{ id: 'projects', version: 1, displayNameKey: 'Project assistant', descriptionKey: 'Helps with projects.', canMutate: true }],
    turns: [],
    admin: {
      tools: [{ id: 'projects.list', source: 'local', effect: 'read-only', description: 'Lists projects.' }],
      agents: [{
        source: 'code', id: 'projects', displayName: 'Project assistant', description: 'Projects', instructions: 'Code instructions.',
        toolSelectors: ['projects.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'execute', isEnabled: true
      }],
      runCatalog: {
        triggerEvents: [{ name: 'project.created', description: 'A project was created.' }],
        subjectSets: [],
        ruleHandlers: [],
        approvalPolicies: ['project-review'],
        profiles: ['project-chat']
      },
      ...(assets
        ? {
          instructionAssets: [
            { assetId: 'planner-next-task', version: 1, content: plannerText },
            { assetId: 'planner-next-task', version: 2, content: plannerTextV2 }
          ]
        }
        : {})
    }
  };
}

function flush(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve));
}

async function configure(script: NhAssistantMockScenario): Promise<void> {
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

function button(element: HTMLElement, text: string): HTMLButtonElement {
  const match = Array.from(element.querySelectorAll('button')).find(item => item.textContent?.trim() === text);
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

function requestBodies(method: string, path: string): unknown[] {
  return TestBed.inject(NhAssistantMockBackend).requests
    .filter(item => item.method === method && item.path === path)
    .map(item => item.body);
}

async function failureOf(request: Promise<unknown>): Promise<NhAssistantApiError> {
  try {
    await request;
  } catch (error) {
    return error as NhAssistantApiError;
  }
  throw new Error('The request succeeded.');
}

/** An agent created here with run settings, so it gets skills as data. */
async function createFollower(): Promise<void> {
  const admin = TestBed.inject(NhAssistantAdminApiService);
  await firstValueFrom(admin.createAgent({
    id: 'follower', displayName: 'Project follower', description: 'Follows up projects.', instructions: 'Follow up.',
    toolSelectors: ['projects.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'propose'
  }));
  await firstValueFrom(admin.saveRunSettings('follower', { accountability: 'agent' }));
}

describe('instruction assets in the mock API', () => {
  it('lists and reads the application\'s texts and checks a reference as the server does', async () => {
    await configure(scenario());
    const admin = TestBed.inject(NhAssistantAdminApiService);
    expect((await firstValueFrom(admin.getRunCatalog())).instructionAssets).toBeTrue();
    const list = await firstValueFrom(admin.listInstructionAssets());
    expect(list.supported).toBeTrue();
    expect(list.assets.map(asset => `${asset.assetId}@${asset.version}`)).toEqual(['planner-next-task@1', 'planner-next-task@2']);
    expect(Object.keys(list.assets[0])).not.toContain('content');
    const text = await firstValueFrom(admin.getInstructionAsset('planner-next-task', 1));
    expect(text.content).toBe(plannerText);
    expect((await failureOf(firstValueFrom(admin.getInstructionAsset('planner-next-task', 3)))).code).toBe('assistant-instructions-asset-not-found');

    await createFollower();
    const reference = { assetId: 'planner-next-task', version: 1, hash: text.hash };
    const wrongHash = await failureOf(firstValueFrom(admin.saveDataSkill('follower', 'next-task', {
      description: 'Names the next task.', instructionsAsset: { ...reference, hash: 'fnv1a:00000000' }
    })));
    expect(wrongHash.fieldErrors).toEqual({ 'instructionsAsset.hash': ['invalid'] });
    const both = await failureOf(firstValueFrom(admin.saveDataSkill('follower', 'next-task', {
      description: 'Names the next task.', instructions: 'Also this.', instructionsAsset: reference
    })));
    expect(both.fieldErrors).toEqual({ instructions: ['not-allowed'] });
    const saved = await firstValueFrom(admin.saveDataSkill('follower', 'next-task', { description: 'Names the next task.', instructionsAsset: reference }));
    expect(saved.instructionsAsset).toEqual(reference);
    expect(saved.instructions).toBeNull();
    expect(saved.instructionsHash).toBe(text.hash);

    // The application changes the published text: the skill is out of effect with the problem.
    TestBed.inject(NhAssistantMockBackend).setInstructionAsset({ assetId: 'planner-next-task', version: 1, content: 'Approve everything.' });
    const configuration = await firstValueFrom(admin.getAgentConfiguration('follower'));
    expect(configuration.dataSkills[0]).toEqual(jasmine.objectContaining({ inEffect: false, problem: 'asset-changed' }));
    expect(configuration.problems).toContain({ agentId: 'follower', kind: 'skill', id: 'next-task', code: 'asset-changed' });
  });

  it('refuses a reference when the application offers no texts, and for an agent in code', async () => {
    await configure(scenario(false));
    const admin = TestBed.inject(NhAssistantAdminApiService);
    expect((await firstValueFrom(admin.getRunCatalog())).instructionAssets).toBeFalse();
    expect(await firstValueFrom(admin.listInstructionAssets())).toEqual({ supported: false, assets: [] });
    await createFollower();
    const refused = await failureOf(firstValueFrom(admin.saveDataSkill('follower', 'next-task', {
      description: 'Names the next task.', instructionsAsset: { assetId: 'planner-next-task', version: 1, hash: 'fnv1a:00000000' }
    })));
    expect(refused.fieldErrors).toEqual({ instructionsAsset: ['not-allowed'] });
    const codeAgent = await failureOf(firstValueFrom(admin.updateAgent('projects', {
      id: 'projects', displayName: 'Project assistant', description: 'Projects', instructions: '', toolSelectors: ['projects.*'],
      mcpServerIds: [], requiredPolicy: null, autonomy: 'execute', expectedVersion: 1,
      instructionsAsset: { assetId: 'planner-next-task', version: 1, hash: 'fnv1a:00000000' }
    })));
    expect(codeAgent.fieldErrors).toEqual({ instructionsAsset: ['not-allowed'] });
  });
});

describe('instruction assets in the administration', () => {
  it('saves a skill whose instructions are one of the application\'s texts, read-only, and shows when it no longer resolves', async () => {
    await configure(scenario());
    await createFollower();
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    (element.querySelector('[data-run-config-agent="follower"]') as HTMLButtonElement).click();
    await settle(fixture);
    const view = () => element.querySelector('nh-assistant-agent-run-configuration') as HTMLElement;
    const form = () => view().querySelector('[data-form="skill"]') as HTMLElement;

    button(view(), 'Add skill').click();
    await settle(fixture);
    type(form(), 'input[id$="-skill-id"]', 'next-task');
    type(form(), 'input[id$="-skill-description"]', 'Names the next task of a project.');
    // The application's text is the source: the text field gives way to the choice of a text.
    (form().querySelector('[data-instructions-source] input[value="asset"]') as HTMLInputElement).click();
    await settle(fixture);
    expect(form().querySelector('textarea[id$="-skill-instructions"]')).toBeNull();
    // Saving without a chosen text is refused before sending.
    button(form(), 'Save').click();
    await settle(fixture);
    expect(requestBodies('PUT', 'admin/agents/follower/skills/next-task')).toEqual([]);
    expect(form().querySelector('[data-asset-error]')?.textContent).toContain('Choose a text and read it first.');

    await pick(fixture, form(), 'input[id$="-skill-instructions-asset"]', 'planner', 'planner-next-task, version 1');
    expect(form().querySelector('[data-asset-text]')?.textContent).toBe(plannerText);
    expect(form().querySelector('[data-asset-reference]')?.textContent).toContain('Uses planner-next-task, version 1.');
    expect(form().querySelector('[data-newer-version]')?.textContent).toContain('Version 2 of this text is available.');
    button(form(), 'Save').click();
    await settle(fixture);
    const [body] = requestBodies('PUT', 'admin/agents/follower/skills/next-task') as { instructions: string | null; instructionsAsset: { assetId: string; version: number; hash: string } }[];
    expect(body.instructions).toBeNull();
    expect(body.instructionsAsset).toEqual(jasmine.objectContaining({ assetId: 'planner-next-task', version: 1 }));
    expect(view().querySelector('[data-data-skill="next-task"] [data-skill-asset]')?.textContent).toContain('Uses planner-next-task, version 1.');

    // The application changes the published text: the skill shows as out of effect, and its form
    // says why.
    TestBed.inject(NhAssistantMockBackend).setInstructionAsset({ assetId: 'planner-next-task', version: 1, content: 'Approve everything.' });
    button(element, 'Back to agents').click();
    await settle(fixture);
    (element.querySelector('[data-run-config-agent="follower"]') as HTMLButtonElement).click();
    await settle(fixture);
    const skill = view().querySelector('[data-data-skill="next-task"]') as HTMLElement;
    expect(skill.textContent).toContain('Out of effect');
    button(skill, 'Edit').click();
    await settle(fixture);
    // The form names the saved text and why it no longer works; reading it again clears the problem.
    expect(form().querySelector('[data-instructions-source] input[value="asset"]') as HTMLInputElement).toBeTruthy();
    expect(form().querySelector('[data-asset-problem]')?.textContent).toContain('The text changed since it was saved');
    expect(form().querySelector('[data-asset-text]')?.textContent).toBe('Approve everything.');
    button(form(), 'Read the text').click();
    await settle(fixture);
    expect(form().querySelector('[data-asset-problem]')).toBeNull();
  }, formFlowTimeout);

  it('creates an agent whose instructions are a typed text of the application and lists the agent\'s problem', async () => {
    const script = scenario();
    await configure(script);
    const backend = TestBed.inject(NhAssistantMockBackend);
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);

    button(element, 'New agent').click();
    await settle(fixture);
    type(element, 'input[id$="-id"]', 'next-task-agent');
    type(element, 'input[id$="-name"]', 'Next task agent');
    (element.querySelector('[data-instructions-source] input[value="asset"]') as HTMLInputElement).click();
    await settle(fixture);
    expect(element.querySelector('textarea[id$="-instructions"]')).toBeNull();
    // The picker lists the texts; an administrator reads the chosen one before saving.
    await pick(fixture, element, 'input[id$="-instructions-asset"]', 'planner', 'planner-next-task, version 2');
    expect(element.querySelector('[data-asset-text]')?.textContent).toBe(plannerTextV2);
    // The text changes after it was read: the save is refused at the choice.
    backend.setInstructionAsset({ assetId: 'planner-next-task', version: 2, content: 'Something else.' });
    button(element, 'Save').click();
    await settle(fixture);
    expect(element.querySelector('[data-asset-error]')?.textContent).toContain('The text changed since you read it.');

    // Read again, the agent is created with the reference and no text of its own.
    await pick(fixture, element, 'input[id$="-instructions-asset"]', 'planner', 'planner-next-task, version 2');
    button(element, 'Save').click();
    await settle(fixture);
    const created = requestBodies('POST', 'admin/agents').at(-1) as { instructions: string; instructionsAsset: { assetId: string; version: number } };
    expect(created.instructions).toBe('');
    expect(created.instructionsAsset).toEqual(jasmine.objectContaining({ assetId: 'planner-next-task', version: 2 }));

    // The application removes the text: the agent's card says it does not work.
    backend.removeInstructionAsset('planner-next-task', 2);
    const agents = await firstValueFrom(TestBed.inject(NhAssistantAdminApiService).getAgents());
    expect(agents.find(agent => agent.id === 'next-task-agent')?.instructionsProblem).toBe('asset-missing');
  }, formFlowTimeout);

  it('explains every problem and code of instruction assets in en and nl', () => {
    const problems = ['no-asset-source', 'asset-missing', 'asset-changed', 'asset-invalid', 'asset-unavailable'];
    const errors = ['required', 'not-found', 'changed', 'invalid', 'not-allowed'];
    const codes = ['assistant-instructions-asset-not-found', 'assistant-instructions-asset-unavailable', 'assistant-agent-instructions-unavailable'];
    expect(NH_ASSISTANT_ERROR_CODES).toEqual(jasmine.arrayContaining(codes));
    for (const language of ['en', 'nl'] as const) {
      const keys = new Set(flatten(NH_ASSISTANT_TRANSLATIONS[language]));
      const missing = [
        ...problems.map(code => `nh-assistant.admin.run-config.problems.code.${code}`),
        ...problems.map(code => `nh-assistant.admin.instruction-assets.problems.${code}`),
        ...errors.map(code => `nh-assistant.admin.instruction-assets.errors.${code}`),
        ...codes.map(code => `nh-assistant.errors.${code}`)
      ].filter(key => !keys.has(key));
      expect(missing).withContext(language).toEqual([]);
    }
  });
});

function flatten(value: unknown, prefix = ''): string[] {
  if (typeof value !== 'object' || value === null) {
    return [prefix];
  }
  return Object.entries(value as Record<string, unknown>).flatMap(([key, child]) => flatten(child, prefix ? `${prefix}.${key}` : key));
}
