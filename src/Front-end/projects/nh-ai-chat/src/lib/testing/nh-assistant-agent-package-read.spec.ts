import { TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockAdminAgent, NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { AgentPackage } from '../models/assistant-agent-package.models';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAdminApiService } from '../services/nh-assistant-admin-api.service';
import { NhAssistantApiError } from '../services/nh-assistant-transport';

/**
 * An agent's parts and an installed package read without an export (FB34):
 * `readAgentPackageParts` answers the agents as an export builds them, with the hash of every part and
 * without locks, and `getInstalledAgentPackage` the package this host imported with the current links
 * of its parts, in the admin API and the mock API alike.
 */

const desks: NhAssistantMockAdminAgent[] = [
  {
    source: 'admin', id: 'intake-desk', displayName: 'Intake desk', description: 'Takes in new projects.', instructions: 'Review each intake.',
    toolSelectors: ['projects.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'propose', isEnabled: true,
    worksOnSubjectTypes: ['project'], worksOnAllSubjects: false, worksOnApplication: false,
    runConfiguration: {
      runSettings: { accountability: 'agent' },
      skills: [{ skillId: 'intake', description: 'Reviews an intake.', instructions: 'Check the intake.', shadow: false }]
    }
  },
  {
    source: 'admin', id: 'kickoff-desk', displayName: 'Kickoff desk', description: 'Starts projects.', instructions: 'Start the project.',
    toolSelectors: ['projects.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'propose', isEnabled: true,
    worksOnSubjectTypes: ['project'], worksOnAllSubjects: false, worksOnApplication: false,
    runConfiguration: { runSettings: { accountability: 'agent' } }
  }
];

function scenario(): NhAssistantMockScenario {
  return {
    agents: [],
    turns: [],
    subjectTypes: [{ id: 'project', names: { en: 'Project' }, subjects: [] }],
    admin: {
      runCatalog: { triggerEvents: [], subjectSets: [], ruleHandlers: [], approvalPolicies: [], profiles: [] },
      agents: desks
    }
  };
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
}

async function call(method: string, path: string, body?: unknown): Promise<{ status: number; body: any }> {
  const response = await TestBed.inject(NhAssistantMockBackend).fetch(`/api/assistant/${path}`, {
    method,
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  const content = await response.text();
  return { status: response.status, body: content ? JSON.parse(content) : null };
}

/** Exports both desks as the package `desks` with the intake skill locked and imports it back, linked. */
async function linkDesks(): Promise<AgentPackage> {
  const exported = await call('POST', 'admin/agent-packages/export', {
    agentIds: ['intake-desk', 'kickoff-desk'],
    options: { packageId: 'desks', locks: { 'intake-desk': { 'skill:intake': ['instructions'] } } }
  });
  expect(exported.status).withContext(JSON.stringify(exported.body)).toBe(200);
  const imported = await call('POST', 'admin/agent-packages/import', {
    package: exported.body,
    options: { relink: [{ agentId: 'intake-desk' }, { agentId: 'kickoff-desk' }] }
  });
  expect(imported.status).withContext(JSON.stringify(imported.body)).toBe(200);
  return exported.body as AgentPackage;
}

async function failureOf(request: Promise<unknown>): Promise<NhAssistantApiError> {
  try {
    await request;
  } catch (error) {
    return error as NhAssistantApiError;
  }
  throw new Error('The request did not fail.');
}

describe('Reading agent package parts without an export', () => {
  beforeEach(async () => configure());

  it('reads the agents as an export builds them, with their hashes and without locks, and sends the agent ids', async () => {
    const shipped = await linkDesks();
    const api = TestBed.inject(NhAssistantAdminApiService);

    const read = await firstValueFrom(api.readAgentPackageParts(['kickoff-desk', 'intake-desk']));

    expect(read.agents.map(agent => agent.id)).toEqual(['kickoff-desk', 'intake-desk']);
    expect(read.agents.every(agent => !agent.locks)).toBeTrue();
    // The hashes are the export's: nothing changed on this host since the package.
    for (const agent of read.agents) {
      expect(agent.hashes).toEqual(shipped.agents.find(item => item.id === agent.id)!.hashes!);
    }
    expect(read.agents[1].skills?.map(skill => skill.id)).toEqual(['intake']);
    const requests = TestBed.inject(NhAssistantMockBackend).requests.filter(item => item.method === 'POST' && item.path === 'admin/agent-packages/read');
    expect(requests.map(item => item.body)).toEqual([{ agentIds: ['kickoff-desk', 'intake-desk'] }]);

    // A part changed on this host has another hash than the package's.
    const kickoff = ((await call('GET', 'admin/agents')).body as { id: string; version: number }[]).find(agent => agent.id === 'kickoff-desk')!;
    const changed = await call('PUT', 'admin/agents/kickoff-desk', { ...kickoff, description: 'Starts projects carefully.', expectedVersion: kickoff.version });
    expect(changed.status).withContext(JSON.stringify(changed.body)).toBe(200);
    const after = await firstValueFrom(api.readAgentPackageParts(['kickoff-desk']));
    expect(after.agents[0].hashes!['agent']).not.toBe(shipped.agents.find(agent => agent.id === 'kickoff-desk')!.hashes!['agent']);
  });

  it('refuses the agent ids an export refuses', async () => {
    const api = TestBed.inject(NhAssistantAdminApiService);

    expect((await failureOf(firstValueFrom(api.readAgentPackageParts([])))).fieldErrors).toEqual({ agentIds: ['required'] });
    expect((await failureOf(firstValueFrom(api.readAgentPackageParts(['kickoff-desk', 'kickoff-desk'])))).fieldErrors).toEqual({ agentIds: ['invalid'] });
    expect((await failureOf(firstValueFrom(api.readAgentPackageParts(['Kickoff Desk'])))).fieldErrors).toEqual({ agentIds: ['invalid'] });
    expect((await failureOf(firstValueFrom(api.readAgentPackageParts(['unknown-desk'])))).code).toBe('assistant-agent-not-found');
  });

  it('gets the installed package with the current links of its parts, and refuses a package this host never imported', async () => {
    const shipped = await linkDesks();
    const api = TestBed.inject(NhAssistantAdminApiService);

    const installed = await firstValueFrom(api.getInstalledAgentPackage('desks'));

    expect([installed.packageId, installed.version, installed.agentIds]).toEqual(['desks', 1, ['intake-desk', 'kickoff-desk']]);
    expect(installed.hash).toMatch(/^[0-9a-f]{64}$/);
    expect(installed.agents.map(agent => agent.agentId)).toEqual(['intake-desk', 'kickoff-desk']);
    const intake = installed.agents.find(agent => agent.agentId === 'intake-desk')!;
    expect(intake.parts.find(part => part.part === 'skill:intake')?.lockedFields).toEqual(['instructions']);
    for (const agent of installed.agents) {
      const hashes = shipped.agents.find(item => item.id === agent.agentId)!.hashes!;
      expect(agent.parts.map(part => [part.part, part.hash, part.changedLocally])).toEqual(agent.parts.map(part => [part.part, hashes[part.part], false]));
    }
    const requests = TestBed.inject(NhAssistantMockBackend).requests.filter(item => item.method === 'GET' && item.path === 'admin/agent-packages/desks');
    expect(requests.length).toBe(1);

    const missing = await failureOf(firstValueFrom(api.getInstalledAgentPackage('other-desks')));
    expect([missing.status, missing.code]).toEqual([404, 'assistant-package-not-found']);
  });
});
