import { Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { NhAssistantAdminAgentsComponent } from '../../../admin/src/public-api';
import { AgentHandOffs, AgentShadowVariants } from '../models/assistant-hand-off.models';
import { AdminAgentTab, openAdminAgent, openAdminTab } from './nh-assistant-admin-navigation';
import { provideNhAssistant } from '../provide-nh-assistant';

/**
 * Hand-offs defined as data and shadow variants created as data (S9): the mock API keeps them within
 * the ceilings in code as the server does, and the run configuration of an agent lists its hand-offs
 * in code read only and as data with a form, and its shadow variants with a form that creates one.
 */

/** A form flow renders several times; one generous timeout covers it. */
const formFlowTimeout = 20_000;

function scenario(): NhAssistantMockScenario {
  return {
    agents: [{ id: 'planner', version: 1, displayNameKey: 'Project planner', descriptionKey: 'Plans projects.', canMutate: true }],
    turns: [],
    subjectTypes: [
      { id: 'project', names: { en: 'Project', nl: 'Project' }, pluralNames: { en: 'Projects', nl: 'Projecten' }, subjects: [] },
      { id: 'order', names: { en: 'Order', nl: 'Bestelling' }, pluralNames: { en: 'Orders', nl: 'Bestellingen' }, subjects: [] }
    ],
    admin: {
      // The application connects the order desk to the planner and agents created here about orders,
      // and lets administrators try one shadow variant of the planner.
      handOffsAsData: {
        rules: [{ subjectType: 'order', to: ['closer'], from: ['planner'], fromAgentsCreatedInAdministration: true }],
        maxPerSubjectPerDay: 10
      },
      shadowVariantsAsData: { of: ['planner'], maxPerAgent: 1 },
      agents: [
        {
          source: 'code', id: 'planner', displayName: 'Project planner', description: 'Plans projects.', instructions: 'Plan the project.',
          toolSelectors: ['projects.*', 'assistant.handoff.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'execute', isEnabled: true
        },
        {
          source: 'code', id: 'closer', displayName: 'Project closer', description: 'Closes projects.', instructions: 'Close.',
          toolSelectors: ['projects.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'execute', isEnabled: true,
          codeHandOffs: [{ subjectType: 'project', sources: ['planner'], description: 'Closes finished projects.', notifySource: true, maxPerSubjectPerDay: 3 }]
        },
        {
          source: 'code', id: 'reviewer', displayName: 'Intake reviewer', description: 'Reviews intakes.', instructions: 'Review.',
          toolSelectors: [], mcpServerIds: [], requiredPolicy: null, autonomy: 'propose', isEnabled: true
        },
        {
          source: 'admin', id: 'helper', displayName: 'Helper', description: 'Helps.', instructions: 'Help.',
          toolSelectors: [], mcpServerIds: [], requiredPolicy: null, autonomy: 'propose', isEnabled: true,
          runConfiguration: { runSettings: { accountability: 'agent' } }
        },
        {
          source: 'admin', id: 'drafts', displayName: 'Draft helper', description: 'Drafts.', instructions: 'Draft.',
          toolSelectors: [], mcpServerIds: [], requiredPolicy: null, autonomy: 'propose', isEnabled: true
        }
      ],
      runCatalog: { triggerEvents: [], subjectSets: [], ruleHandlers: [], approvalPolicies: [], profiles: [] }
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

function type(scope: Element, selector: string, value: string): void {
  const input = scope.querySelector(selector) as HTMLInputElement | HTMLTextAreaElement;
  input.value = value;
  input.dispatchEvent(new Event('input'));
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

/** A data shadow variant of the planner, as an administrator creates it. */
const variantDefinition = {
  id: 'planner-v2', displayName: 'Planner v2', description: 'Plans strictly.', instructions: 'Plan strictly.',
  toolSelectors: ['projects.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'propose', shadowOf: 'planner'
};

/**
 * Opens the page of the agent on the agents page, at Build where its hand-offs are, or at Rollout
 * where its shadow variants are, and returns the agents page.
 */
async function openRunConfiguration(fixture: ComponentFixture<unknown>, element: HTMLElement, agentId: string, tab: AdminAgentTab = 'build'): Promise<HTMLElement> {
  await openAdminAgent(fixture, element, agentId, tab);
  return settle(fixture);
}

describe('Hand-offs and shadow variants as data in the mock API', () => {
  beforeEach(async () => configure());

  it('reports the hand-offs in code and the ceiling, and saves a hand-off within it', async () => {
    const before = (await call('GET', 'admin/agents/closer/hand-offs')).body as AgentHandOffs;
    expect(before.codeHandOffs).toEqual([{
      subjectType: 'project',
      sources: ['planner'],
      description: 'Closes finished projects.',
      notifySource: true,
      maxPerSubjectPerDay: 3,
      toolId: 'assistant.handoff.closer.project'
    }]);
    expect(before.dataHandOffs).toEqual([]);
    expect(before.configured).toBeTrue();
    // Agents created here count once they have run settings: the helper does, the draft helper not.
    expect(before.ceiling?.subjectTypes).toEqual([{ subjectType: 'order', sources: ['helper', 'planner'], sourcesCreatedInAdministration: true }]);
    expect(before.ceiling?.maxPerSubjectPerDay).toBe(10);

    const saved = await call('PUT', 'admin/agents/closer/hand-offs/order', { sources: ['planner'], description: 'Closes orders.', notifySource: true });
    expect(saved.status).toBe(200);
    expect(saved.body).toEqual(jasmine.objectContaining({
      subjectType: 'order',
      version: 1,
      sources: ['planner'],
      maxPerSubjectPerDay: 5,
      toolId: 'assistant.handoff.closer.order',
      inEffect: true,
      problem: null
    }));
    const changed = await call('PUT', 'admin/agents/closer/hand-offs/order', { sources: ['planner', 'helper'], maxPerSubjectPerDay: 2, expectedVersion: 1 });
    expect(changed.body.version).toBe(2);
    expect((await call('PUT', 'admin/agents/closer/hand-offs/order', { sources: ['planner'], expectedVersion: 1 })).body.code).toBe('assistant-version-conflict');

    expect((await call('DELETE', 'admin/agents/closer/hand-offs/order?expectedVersion=2')).status).toBe(204);
    expect(((await call('GET', 'admin/agents/closer/hand-offs')).body as AgentHandOffs).dataHandOffs).toEqual([]);
  });

  it('refuses what the server refuses, with its codes and field errors', async () => {
    const refusal = async (agentId: string, subjectType: string, body: unknown) =>
      (await call('PUT', `admin/agents/${agentId}/hand-offs/${subjectType}`, body)).body;

    expect((await refusal('closer', 'order', { sources: [] })).errors).toEqual({ sources: ['required'] });
    expect((await refusal('closer', 'order', { sources: ['closer'] })).errors).toEqual({ sources: ['invalid'] });
    expect((await refusal('closer', 'order', { sources: ['reviewer'] })).errors).toEqual({ sources: ['not-allowed'] });
    expect((await refusal('closer', 'order', { sources: ['drafts'] })).errors).toEqual({ sources: ['not-found'] });
    expect((await refusal('closer', 'order', { sources: ['planner'], maxPerSubjectPerDay: 11 })).errors).toEqual({ maxPerSubjectPerDay: ['not-allowed'] });
    expect((await refusal('closer', 'task', { sources: ['planner'] })).errors).toEqual({ subjectType: ['not-allowed'] });
    expect((await refusal('closer', 'project', { sources: ['planner'] })).code).toBe('assistant-handoff-in-code');
    expect((await refusal('planner', 'order', { sources: ['closer'] })).code).toBe('assistant-handoffs-not-allowed');
    const missing = await call('DELETE', 'admin/agents/closer/hand-offs/order');
    expect([missing.status, missing.body.code]).toEqual([404, 'assistant-handoff-not-found']);
    const inCode = await call('DELETE', 'admin/agents/closer/hand-offs/project');
    expect([inCode.status, inCode.body.code]).toEqual([409, 'assistant-handoff-in-code']);
  });

  it('keeps the run settings of an agent created here in use while it accepts a hand-off as data', async () => {
    TestBed.resetTestingModule();
    const script = scenario();
    script.admin!.handOffsAsData!.rules.push({ subjectType: 'project', toAgentsCreatedInAdministration: true, from: ['planner'] });
    await configure(script);

    expect((await call('PUT', 'admin/agents/helper/hand-offs/project', { sources: ['planner'] })).status).toBe(200);
    const inUse = await call('DELETE', 'admin/agents/helper/run-settings');
    expect([inUse.status, inUse.body.code]).toEqual([409, 'assistant-run-settings-in-use']);
    expect((await call('DELETE', 'admin/agents/helper/hand-offs/project?expectedVersion=1')).status).toBe(204);
    expect((await call('DELETE', 'admin/agents/helper/run-settings')).status).toBe(204);
  });

  it('creates a data shadow variant within the ceiling, always in shadow and never in the chat', async () => {
    const definition = {
      id: 'planner-v2', displayName: 'Planner v2', description: 'Plans strictly.', instructions: 'Plan strictly.',
      toolSelectors: ['projects.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'execute'
    };

    expect((await call('POST', 'admin/agents', { ...definition, shadowOf: 'closer' })).body.code).toBe('assistant-shadow-variants-not-allowed');
    expect((await call('POST', 'admin/agents', { ...definition, id: 'planner', shadowOf: 'planner' })).status).toBe(409);
    const created = await call('POST', 'admin/agents', { ...definition, shadowOf: 'planner', offeredInChat: true });
    expect(created.status).toBe(201);
    expect(created.body).toEqual(jasmine.objectContaining({ id: 'planner-v2', shadowOf: 'planner', offeredInChat: false }));
    expect(created.body.shadow).toEqual(jasmine.objectContaining({ shadow: true, locked: true }));
    expect((await call('POST', 'admin/agents/planner-v2/shadow', { mode: 'live' })).body.code).toBe('assistant-shadow-variant');

    // The ceiling allows one variant of the planner.
    expect((await call('POST', 'admin/agents', { ...definition, id: 'planner-v3', shadowOf: 'planner' })).body.code).toBe('assistant-configuration-limit');
    const variants = (await call('GET', 'admin/agents/planner/shadow-variants')).body as AgentShadowVariants;
    expect(variants).toEqual({
      agentId: 'planner',
      allowsDataVariants: true,
      maxDataVariants: 1,
      variants: [{ agentId: 'planner-v2', displayName: 'Planner v2', source: 'data', hasRunSettings: false, inEffect: false, problem: null, version: 1, packageId: null, packageVersion: null }]
    });
    expect(((await call('GET', 'admin/agents/reviewer/shadow-variants')).body as AgentShadowVariants).allowsDataVariants).toBeFalse();
  });

  it('promotes a data variant onto its live agent through the update path, moves its skills and removes it', async () => {
    expect((await call('POST', 'admin/agents', variantDefinition)).status).toBe(201);
    expect((await call('PUT', 'admin/agents/planner-v2/run-settings', { accountability: 'agent' })).status).toBe(200);
    expect((await call('PUT', 'admin/agents/planner-v2/skills/review', { description: 'Reviews plans.', instructions: 'Review the plan.', tools: ['projects.*'] })).status).toBe(200);

    // Only a data variant, at the versions the administrator saw.
    expect((await call('POST', 'admin/agents/planner/promote', { expectedVersion: 1 })).body.code).toBe('assistant-not-shadow-variant');
    expect((await call('POST', 'admin/agents/ghost/promote', { expectedVersion: 1 })).body.code).toBe('assistant-agent-not-found');
    const missing = await call('POST', 'admin/agents/planner-v2/promote', { moveSkills: true });
    expect([missing.status, missing.body.errors]).toEqual([400, { expectedVersion: ['required'] }]);
    expect((await call('POST', 'admin/agents/planner-v2/promote', { expectedVersion: 2 })).body.code).toBe('assistant-version-conflict');
    expect((await call('POST', 'admin/agents/planner-v2/promote', { expectedVersion: 1, expectedVariantVersion: 3 })).body.code).toBe('assistant-version-conflict');

    const promoted = await call('POST', 'admin/agents/planner-v2/promote', { expectedVersion: 1, moveSkills: true, expectedVariantVersion: 1 });
    expect(promoted.status).toBe(200);
    // The live agent in code keeps its name and takes the fields it overrides, as its next version.
    expect(promoted.body).toEqual(jasmine.objectContaining({ variantId: 'planner-v2', movedSkills: ['review'] }));
    expect(promoted.body.agent).toEqual(jasmine.objectContaining({
      id: 'planner', version: 2, displayName: 'Project planner', description: 'Plans strictly.', instructions: 'Plan strictly.',
      autonomy: 'propose', isOverridden: true, source: 'code'
    }));
    const agents = (await call('GET', 'admin/agents')).body as { id: string }[];
    expect(agents.map(agent => agent.id)).not.toContain('planner-v2');
    expect(((await call('GET', 'admin/agents/planner/shadow-variants')).body as AgentShadowVariants).variants).toEqual([]);
    const configuration = (await call('GET', 'admin/agents/planner/configuration')).body as { dataSkills: { skillId: string; description: string }[] };
    expect(configuration.dataSkills.map(skill => [skill.skillId, skill.description])).toEqual([['review', 'Reviews plans.']]);
  });

  it('answers only administrators', async () => {
    TestBed.inject(NhAssistantMockBackend).setCanAdminister(false);

    for (const [method, path] of [
      ['GET', 'admin/agents/closer/hand-offs'],
      ['PUT', 'admin/agents/closer/hand-offs/order'],
      ['DELETE', 'admin/agents/closer/hand-offs/order'],
      ['GET', 'admin/agents/planner/shadow-variants'],
      ['POST', 'admin/agents/planner-v2/promote']
    ]) {
      const answer = await call(method, path, method === 'PUT' ? { sources: ['planner'] } : undefined);
      expect([answer.status, answer.body.code]).withContext(`${method} ${path}`).toEqual([403, 'assistant-forbidden']);
    }
  });
});

describe('Hand-offs and shadow variants in the run configuration', () => {
  beforeEach(async () => configure());

  it('lists the hand-offs in code and adds one within the ceiling', async () => {
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    let page = await openRunConfiguration(fixture, element, 'closer');
    let section = page.querySelector('[data-hand-offs]') as HTMLElement;

    const code = section.querySelector('[data-hand-off="project"][data-hand-off-origin="code"]');
    expect(text(code)).toContain('Project');
    expect(text(code?.querySelector('[data-hand-off-sources]'))).toContain('Project planner');
    expect(text(code)).toContain('At most 3 per source and object per day');
    expect(text(code?.querySelector('[data-hand-off-tool]'))).toBe('assistant.handoff.closer.project');

    (section.querySelector('[data-open-form="hand-off"]') as HTMLButtonElement).click();
    page = await settle(fixture);
    section = page.querySelector('[data-hand-offs]') as HTMLElement;
    const form = section.querySelector('[data-form="hand-off"]') as HTMLElement;
    // The one kind of object the ceiling connects the closer about is chosen; the sources are those it allows.
    expect((form.querySelector('[data-hand-off-type]') as HTMLSelectElement).value).toBe('order');
    expect(Array.from(form.querySelectorAll<HTMLInputElement>('[data-hand-off-source]')).map(input => input.dataset['handOffSource'])).toEqual(['helper', 'planner']);

    // Without a source the form refuses before it sends.
    (form.querySelector('button[type="submit"]') as HTMLButtonElement).click();
    page = await settle(fixture);
    expect(text(page.querySelector('[data-hand-offs] [data-hand-off-sources] .field-error'))).toBe('Required.');
    expect(requestBodies('PUT', 'admin/agents/closer/hand-offs/order')).toEqual([]);

    (page.querySelector('[data-hand-off-source="planner"]') as HTMLInputElement).click();
    type(page, '[data-form="hand-off"] textarea', 'Closes orders.');
    type(page, '[data-hand-off-limit]', '3');
    (page.querySelector('[data-form="hand-off"] button[type="submit"]') as HTMLButtonElement).click();
    page = await settle(fixture);

    expect(requestBodies('PUT', 'admin/agents/closer/hand-offs/order')).toEqual([jasmine.objectContaining({
      sources: ['planner'],
      description: 'Closes orders.',
      notifySource: false,
      maxPerSubjectPerDay: 3,
      expectedVersion: null
    })]);
    section = page.querySelector('[data-hand-offs]') as HTMLElement;
    expect(section.querySelector('[data-form="hand-off"]')).toBeNull();
    const data = section.querySelector('[data-hand-off="order"][data-hand-off-origin="data"]');
    expect(text(data)).toContain('Version 1');
    expect(text(data?.querySelector('[data-hand-off-tool]'))).toBe('assistant.handoff.closer.order');
    expect(text(section.querySelector('[data-hand-offs-message]'))).toContain('The hand-off was added.');
    // Every kind of object the ceiling connects it about has a hand-off now.
    expect(section.querySelector('[data-hand-offs-closed]')?.getAttribute('data-hand-offs-closed')).toBe('no-types');

    // Removing asks first.
    (data?.querySelector('[data-remove-hand-off]') as HTMLButtonElement).click();
    page = await settle(fixture);
    (page.querySelector('[data-hand-offs] [data-confirm-remove]') as HTMLButtonElement).click();
    page = await settle(fixture);
    expect(page.querySelector('[data-hand-off="order"]')).toBeNull();
    expect(text(page.querySelector('[data-hand-offs-message]'))).toBe('The hand-off was removed.');
  }, formFlowTimeout);

  it('creates a shadow variant from the live agent and continues in its run configuration', async () => {
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    let page = await openRunConfiguration(fixture, element, 'planner', 'rollout');
    let section = page.querySelector('[data-shadow-variants]') as HTMLElement;
    expect(text(section.querySelector('[data-shadow-variants-empty]'))).toBe('This agent has no shadow variants.');

    (section.querySelector('[data-open-form="shadow-variant"]') as HTMLButtonElement).click();
    page = await settle(fixture);
    const form = page.querySelector('[data-form="shadow-variant"]') as HTMLElement;
    // The form starts from the live agent.
    expect((form.querySelector('[data-variant-id]') as HTMLInputElement).value).toBe('planner-variant');
    expect((form.querySelector('[data-variant-name]') as HTMLInputElement).value).toBe('Project planner (variant)');
    expect((form.querySelector('[data-variant-instructions]') as HTMLTextAreaElement).value).toBe('Plan the project.');

    type(form, '[data-variant-id]', 'Planner V2');
    (form.querySelector('button[type="submit"]') as HTMLButtonElement).click();
    page = await settle(fixture);
    expect(text(page.querySelector('[data-form="shadow-variant"] [data-variant-id] ~ .field-error'))).toContain('Use lowercase letters');

    type(page, '[data-variant-id]', 'planner-v2');
    type(page, '[data-variant-instructions]', 'Plan strictly.');
    (page.querySelector('[data-form="shadow-variant"] button[type="submit"]') as HTMLButtonElement).click();
    page = await settle(fixture);

    expect(requestBodies('POST', 'admin/agents')).toEqual([jasmine.objectContaining({
      id: 'planner-v2',
      instructions: 'Plan strictly.',
      toolSelectors: ['projects.*', 'assistant.handoff.*'],
      offeredInChat: false,
      shadowOf: 'planner'
    })]);
    // The page continues on the variant's page at Build, where its guided setup gives it run settings.
    expect(page.querySelector('[data-agent-page="planner-v2"] [data-agent-panel="build"] nh-assistant-agent-run-configuration')).not.toBeNull();
    expect(page.querySelector('[data-work-setup]')).not.toBeNull();
    await openAdminTab(fixture, element, 'rollout');
    page = await settle(fixture);
    section = page.querySelector('[data-shadow-variants]') as HTMLElement;
    expect(section.querySelector('[data-shadow-variants-closed]')?.getAttribute('data-shadow-variants-closed')).toBe('not-allowed');
    const variants = (await call('GET', 'admin/agents/planner/shadow-variants')).body as AgentShadowVariants;
    expect(variants.variants.map(variant => [variant.agentId, variant.hasRunSettings])).toEqual([['planner-v2', false]]);
  }, formFlowTimeout);

  it('promotes a data variant onto the agent after a confirmation and says what changed', async () => {
    expect((await call('POST', 'admin/agents', variantDefinition)).status).toBe(201);
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    let page = await openRunConfiguration(fixture, element, 'planner', 'rollout');
    const action = page.querySelector('[data-shadow-variants] [data-promote-variant="planner-v2"]') as HTMLButtonElement;
    expect(text(action)).toBe('Promote onto Project planner');
    expect(action.getAttribute('aria-label')).toBe('Promote Planner v2 onto Project planner');

    // It asks first, says what the agent in code keeps, and is cancelled without a request.
    action.click();
    page = await settle(fixture);
    let confirm = page.querySelector('[data-promote-confirm]') as HTMLElement;
    expect(text(confirm.querySelector('h5'))).toBe('Promote Planner v2 onto Project planner?');
    expect(text(confirm.querySelector('[data-promote-explain]'))).toContain('keeps its name, MCP servers, run settings, triggers, state and shadow mode');
    expect(document.activeElement).toBe(confirm.querySelector('[data-promote-confirm-button]'));
    (confirm.querySelector('.actions button.secondary') as HTMLButtonElement).click();
    page = await settle(fixture);
    expect(page.querySelector('[data-promote-confirm]')).toBeNull();
    expect(document.activeElement).toBe(page.querySelector('[data-promote-variant="planner-v2"]'));
    expect(requestBodies('POST', 'admin/agents/planner-v2/promote')).toEqual([]);

    // Confirmed with its skills, at the versions shown.
    (page.querySelector('[data-promote-variant="planner-v2"]') as HTMLButtonElement).click();
    page = await settle(fixture);
    confirm = page.querySelector('[data-promote-confirm]') as HTMLElement;
    (confirm.querySelector('[data-promote-move-skills]') as HTMLInputElement).click();
    (confirm.querySelector('[data-promote-confirm-button]') as HTMLButtonElement).click();
    page = await settle(fixture);

    expect(requestBodies('POST', 'admin/agents/planner-v2/promote')).toEqual([{ expectedVersion: 1, moveSkills: true, expectedVariantVersion: 1 }]);
    const section = page.querySelector('[data-shadow-variants]') as HTMLElement;
    expect(text(section.querySelector('[data-promoted]'))).toBe('Planner v2 was promoted onto Project planner, which is at version 2 now.');
    expect(section.querySelector('[data-shadow-variant="planner-v2"]')).toBeNull();
    expect(text(section.querySelector('[data-shadow-variants-empty]'))).toBe('This agent has no shadow variants.');
  }, formFlowTimeout);

  it('refuses a promotion onto an agent that changed in the meantime', async () => {
    expect((await call('POST', 'admin/agents', variantDefinition)).status).toBe(201);
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    let page = await openRunConfiguration(fixture, element, 'planner', 'rollout');
    (page.querySelector('[data-promote-variant="planner-v2"]') as HTMLButtonElement).click();
    page = await settle(fixture);

    // Someone else changes the planner while the confirmation is open.
    const planner = ((await call('GET', 'admin/agents')).body as { id: string }[]).find(agent => agent.id === 'planner');
    expect((await call('PUT', 'admin/agents/planner', { ...planner, description: 'Plans everything.', expectedVersion: 1 })).status).toBe(200);
    (page.querySelector('[data-promote-confirm-button]') as HTMLButtonElement).click();
    page = await settle(fixture);

    expect(text(page.querySelector('[data-promote-error]'))).toBe('Someone else changed this in the meantime. Reload and apply your change again.');
    expect(page.querySelector('[data-shadow-variant="planner-v2"]')).not.toBeNull();
    expect(page.querySelector('[data-promoted]')).toBeNull();
  }, formFlowTimeout);

  it('asks to promote a variant in Dutch', async () => {
    await firstValueFrom(TestBed.inject(TranslateService).use('nl'));
    expect((await call('POST', 'admin/agents', variantDefinition)).status).toBe(201);
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    let page = await openRunConfiguration(fixture, element, 'planner', 'rollout');
    const action = page.querySelector('[data-promote-variant="planner-v2"]') as HTMLButtonElement;
    expect(text(action)).toBe('Promoveren naar Project planner');
    action.click();
    page = await settle(fixture);
    const confirm = page.querySelector('[data-promote-confirm]') as HTMLElement;
    expect(text(confirm.querySelector('h5'))).toBe('Planner v2 promoveren naar Project planner?');
    expect(text(confirm.querySelector('[data-promote-confirm-button]'))).toBe('Promoveren');
    expect(text(confirm)).toContain('Ook zijn vaardigheden verplaatsen');
  }, formFlowTimeout);

  it('says why an agent accepts no hand-offs and gets no shadow variants', async () => {
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const page = await openRunConfiguration(fixture, element, 'reviewer');

    expect(page.querySelector('[data-hand-offs-closed]')?.getAttribute('data-hand-offs-closed')).toBe('application');
    expect(page.querySelector('[data-open-form="hand-off"]')).toBeNull();
    expect(text(page.querySelector('[data-hand-offs-empty]'))).toBe('No agent can hand work to this agent yet.');
    await openAdminTab(fixture, element, 'rollout');
    expect(page.querySelector('[data-shadow-variants-closed]')?.getAttribute('data-shadow-variants-closed')).toBe('not-allowed');
    expect(page.querySelector('[data-open-form="shadow-variant"]')).toBeNull();
  }, formFlowTimeout);

  it('reads in Dutch', async () => {
    await firstValueFrom(TestBed.inject(TranslateService).use('nl'));
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const page = await openRunConfiguration(fixture, element, 'closer');

    expect(text(page.querySelector('[data-hand-offs] h3'))).toBe('Overdrachten');
    expect(text(page.querySelector('[data-hand-off="project"]'))).toContain('Hoogstens 3 per bron en object per dag');
    await openAdminTab(fixture, element, 'rollout');
    expect(text(page.querySelector('[data-shadow-variants] h3'))).toBe('Schaduwvarianten');
  }, formFlowTimeout);

  it('names agents in code in the reader\'s language, as the agent list does', async () => {
    TestBed.resetTestingModule();
    const script = scenario();
    const agents = script.admin!.agents!;
    // The planner and a variant of it in code name themselves with keys of the application.
    agents[0] = { ...agents[0], displayName: 'nh-assistant.agents.planner.name' };
    agents.push({
      source: 'code', id: 'planner-strict', displayName: 'nh-assistant.agents.planner-strict.name', description: 'Plans strictly.',
      instructions: 'Plan strictly.', toolSelectors: ['projects.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'execute',
      isEnabled: true, shadowOf: 'planner'
    });
    await configure(script);
    TestBed.inject(TranslateService).setTranslation('en', {
      'nh-assistant': { agents: { planner: { name: 'Project planner' }, 'planner-strict': { name: 'Strict planner' } } }
    }, true);

    const closer = await render(NhAssistantAdminAgentsComponent);
    let page = await openRunConfiguration(closer.fixture, closer.element, 'closer');
    expect(text(page.querySelector('[data-hand-off="project"] [data-hand-off-sources]'))).toContain('Project planner');
    (page.querySelector('[data-hand-offs] [data-open-form="hand-off"]') as HTMLButtonElement).click();
    page = await settle(closer.fixture);
    const source = page.querySelector('[data-form="hand-off"] [data-hand-off-source="planner"]')?.closest('label');
    expect(text(source?.querySelector('strong'))).toBe('Project planner');
    expect(page.querySelector('[data-hand-offs]')?.textContent).not.toContain('nh-assistant.agents.planner.name');

    const planner = await render(NhAssistantAdminAgentsComponent);
    page = await openRunConfiguration(planner.fixture, planner.element, 'planner', 'rollout');
    const section = page.querySelector('[data-shadow-variants]') as HTMLElement;
    expect(text(section.querySelector('[data-shadow-variant="planner-strict"] [data-shadow-variant-name]'))).toBe('Strict planner');
    (section.querySelector('[data-open-form="shadow-variant"]') as HTMLButtonElement).click();
    page = await settle(planner.fixture);
    expect((page.querySelector('[data-shadow-variants] [data-variant-name]') as HTMLInputElement).value).toContain('Project planner');
  }, formFlowTimeout);
});
