import { Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { NhAssistantAdminAgentsComponent } from '../../../admin/src/public-api';
import { AdminAgent } from '../models/assistant-admin.models';
import { NH_ASSISTANT_AGENT_PACKAGE_FORMAT, NH_ASSISTANT_AGENT_PACKAGE_FORMAT_VERSION } from '../models/assistant-agent-package.models';
import { AgentShadowVariants, ShadowVariantPromotion } from '../models/assistant-hand-off.models';
import { openAdminAgent } from './nh-assistant-admin-navigation';
import { provideNhAssistant } from '../provide-nh-assistant';

/**
 * A shadow variant promoted as its package's next version (FB11): the mock API takes a variant whose
 * every part is linked to one package onto its live agent as an import of that version, and the run
 * configuration of the live agent offers "As the package's next version" for such a variant.
 */

/** A form flow renders several times; one generous timeout covers it. */
const formFlowTimeout = 20_000;

function scenario(): NhAssistantMockScenario {
  return {
    agents: [],
    turns: [],
    subjectTypes: [{ id: 'project', names: { en: 'Project', nl: 'Project' }, subjects: [] }],
    admin: {
      shadowVariantsAsData: { ofAgentsCreatedInAdministration: true, maxPerAgent: 2 },
      agents: [
        {
          source: 'admin', id: 'kickoff-desk', displayName: 'Kickoff desk', description: 'Starts projects.', instructions: 'Start the project.',
          toolSelectors: ['projects.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'propose', isEnabled: true,
          worksOnSubjectTypes: ['project'], worksOnAllSubjects: false, worksOnApplication: false,
          runConfiguration: { runSettings: { accountability: 'agent' } }
        }
      ],
      runCatalog: { triggerEvents: [], subjectSets: [], ruleHandlers: [], approvalPolicies: [], profiles: [] }
    }
  };
}

/** The next version of the kickoff desk, waiting as a shadow variant in a package of its own. */
const nextVersion = {
  format: NH_ASSISTANT_AGENT_PACKAGE_FORMAT,
  formatVersion: NH_ASSISTANT_AGENT_PACKAGE_FORMAT_VERSION,
  package: { id: 'desks-next', version: 2 },
  agents: [
    {
      id: 'kickoff-trial',
      source: 'admin',
      definition: {
        displayName: 'Kickoff trial', description: 'Starts projects carefully.', instructions: 'Explain the plan before you start.',
        toolSelectors: ['projects.*'], autonomy: 'propose', worksOnSubjectTypes: ['project'], worksOnAllSubjects: false,
        worksOnApplication: false, shadowOf: 'kickoff-desk'
      },
      runSettings: { accountability: 'agent', retryCount: 5 },
      skills: [{ id: 'kickoff', description: 'Starts a project that is ready.', instructions: 'Start once the plan is approved.', tools: ['projects.*'] }],
      locks: { agent: ['instructions'] }
    }
  ]
};

function flush(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve));
}

async function configure(): Promise<void> {
  TestBed.configureTestingModule({
    providers: [
      provideTranslateService({ fallbackLang: 'en' }),
      provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
      provideNhAssistantMockApi(scenario())
    ]
  });
  await firstValueFrom(TestBed.inject(TranslateService).use('en'));
  const imported = await call('POST', 'admin/agent-packages/import', { package: nextVersion });
  expect(imported.status).withContext(JSON.stringify(imported.body)).toBe(200);
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

/** Opens the rollout of the agent on the agents page, where its shadow variants are, and returns the page. */
async function openRunConfiguration(fixture: ComponentFixture<unknown>, element: HTMLElement, agentId: string): Promise<HTMLElement> {
  await openAdminAgent(fixture, element, agentId, 'rollout');
  return settle(fixture);
}

describe('A shadow variant promoted as its package\'s next version', () => {
  beforeEach(async () => configure());

  it('names the variant\'s package and applies its parts onto the live agent as an import of that version', async () => {
    const variants = (await call('GET', 'admin/agents/kickoff-desk/shadow-variants')).body as AgentShadowVariants;
    expect(variants.variants.map(variant => [variant.agentId, variant.packageId, variant.packageVersion])).toEqual([['kickoff-trial', 'desks-next', 2]]);

    // The skills are parts of the package version; they do not move on their own as well.
    const both = await call('POST', 'admin/agents/kickoff-trial/promote', { expectedVersion: 1, moveSkills: true, asPackageVersion: true });
    expect([both.status, both.body.errors]).toEqual([400, { moveSkills: ['not-allowed'] }]);

    const promoted = await call('POST', 'admin/agents/kickoff-trial/promote', { expectedVersion: 1, expectedVariantVersion: 1, asPackageVersion: true });

    expect(promoted.status).withContext(JSON.stringify(promoted.body)).toBe(200);
    const promotion = promoted.body as ShadowVariantPromotion;
    expect(promotion.agent).toEqual(jasmine.objectContaining({
      id: 'kickoff-desk',
      version: 2,
      displayName: 'Kickoff desk',
      description: 'Starts projects carefully.',
      instructions: 'Explain the plan before you start.'
    }));
    expect(promotion.agent.package?.packageId).toBe('desks-next');
    expect(promotion.package?.packageId).toBe('desks-next');
    expect(promotion.movedSkills).toEqual(['kickoff']);
    const agents = (await call('GET', 'admin/agents')).body as AdminAgent[];
    expect(agents.map(agent => agent.id)).toEqual(['kickoff-desk']);
    // The package's lock holds on the live agent.
    const locked = await call('PUT', 'admin/agents/kickoff-desk', { ...agents[0], instructions: 'Start right away.', expectedVersion: 2 });
    expect([locked.status, locked.body.errors]).toEqual([400, { instructions: ['locked'] }]);
  });

  it('refuses a variant with a part an administrator unlinked and changes nothing', async () => {
    expect((await call('POST', 'admin/agents/kickoff-trial/package/unlink', { part: 'skill:kickoff' })).status).toBe(200);

    const refused = await call('POST', 'admin/agents/kickoff-trial/promote', { expectedVersion: 1, asPackageVersion: true });

    expect([refused.status, refused.body.code]).toEqual([404, 'assistant-package-not-linked']);
    const agents = (await call('GET', 'admin/agents')).body as AdminAgent[];
    expect(agents.map(agent => [agent.id, agent.version])).toEqual([['kickoff-desk', 1], ['kickoff-trial', 1]]);
  });

  it('offers the package\'s next version for a linked variant and sends it', async () => {
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    let page = await openRunConfiguration(fixture, element, 'kickoff-desk');
    const section = page.querySelector('[data-shadow-variants]') as HTMLElement;
    expect(text(section.querySelector('[data-shadow-variant="kickoff-trial"] [data-shadow-variant-package]'))).toBe('Package desks-next version 2');

    (section.querySelector('[data-promote-variant="kickoff-trial"]') as HTMLButtonElement).click();
    page = await settle(fixture);
    let confirm = page.querySelector('[data-promote-confirm]') as HTMLElement;
    const option = confirm.querySelector('[data-promote-as-package-version]') as HTMLInputElement;
    expect(option).not.toBeNull();
    expect(confirm.querySelector('[data-promote-move-skills]')).not.toBeNull();

    // As the package's version its skills come along and the update path's explanation leaves.
    option.click();
    page = await settle(fixture);
    confirm = page.querySelector('[data-promote-confirm]') as HTMLElement;
    expect(confirm.querySelector('[data-promote-move-skills]')).toBeNull();
    expect(confirm.querySelector('[data-promote-explain]')).toBeNull();
    expect(text(confirm)).toContain('All its parts from package desks-next version 2 replace those of Kickoff desk');
    (confirm.querySelector('[data-promote-confirm-button]') as HTMLButtonElement).click();
    page = await settle(fixture);

    expect(requestBodies('POST', 'admin/agents/kickoff-trial/promote')).toEqual([
      { expectedVersion: 1, moveSkills: false, expectedVariantVersion: 1, asPackageVersion: true }
    ]);
    expect(text(page.querySelector('[data-shadow-variants] [data-promoted]'))).toBe('Kickoff trial was promoted onto Kickoff desk, which is at version 2 now.');
  }, formFlowTimeout);

  it('offers the package\'s next version in Dutch', async () => {
    await firstValueFrom(TestBed.inject(TranslateService).use('nl'));
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    let page = await openRunConfiguration(fixture, element, 'kickoff-desk');
    expect(text(page.querySelector('[data-shadow-variant-package]'))).toBe('Pakket desks-next versie 2');
    (page.querySelector('[data-promote-variant="kickoff-trial"]') as HTMLButtonElement).click();
    page = await settle(fixture);

    expect(text(page.querySelector('[data-promote-confirm]'))).toContain('Als de volgende versie van het pakket');
  }, formFlowTimeout);
});
