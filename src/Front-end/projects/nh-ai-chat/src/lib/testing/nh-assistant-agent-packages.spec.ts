import { TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockAdminAgent, NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { canonicalJson, mockSha256 } from '../../../testing/src/nh-assistant-mock-agent-packages';
import { AdminAgent, AgentRunConfiguration } from '../models/assistant-admin.models';
import { AgentPackage, AgentPackagePreview, AgentPackageWarning } from '../models/assistant-agent-package.models';
import { AgentHandOffs } from '../models/assistant-hand-off.models';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAdminApiService } from '../services/nh-assistant-admin-api.service';
import { NhAssistantApiError } from '../services/nh-assistant-transport';

/**
 * Agent packages in the mock API (S7-2), with the rules of the server: an export of exactly the chosen
 * agents with the references outside them, a preview per part, an import that applies every part or
 * nothing, locked fields that every save refuses with `{field}: locked`, removals and deletes refused
 * with `409 assistant-package-locked`, and unlinked parts that an upgrade keeps local unless it relinks
 * them.
 */

/** The intake desk and the kickoff desk, which the intake desk hands projects to, and a planner in code. */
const desks: NhAssistantMockAdminAgent[] = [
  {
    source: 'admin', id: 'intake-desk', displayName: 'Intake desk', description: 'Takes in new projects.', instructions: 'Review each intake.',
    toolSelectors: ['projects.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'propose', isEnabled: true,
    worksOnSubjectTypes: ['project'], worksOnAllSubjects: false, worksOnApplication: false,
    metadata: { 'cost-center': { code: 'CC-1' } },
    runConfiguration: {
      runSettings: { accountability: 'service-account', serviceAccountActorId: 'ops' },
      skills: [{ skillId: 'intake', description: 'Reviews an intake.', instructions: 'Check the intake.', shadow: false }],
      triggers: [{ triggerId: 'intake-created', source: 'event', event: 'project.created', skill: 'intake', shadow: false }]
    }
  },
  {
    source: 'admin', id: 'kickoff-desk', displayName: 'Kickoff desk', description: 'Starts projects.', instructions: 'Start the project.',
    toolSelectors: ['projects.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'propose', isEnabled: true,
    worksOnSubjectTypes: ['project'], worksOnAllSubjects: false, worksOnApplication: false,
    runConfiguration: { runSettings: { accountability: 'agent' } }
  }
];

const planner: NhAssistantMockAdminAgent = {
  source: 'code', id: 'planner', displayName: 'Planner', description: 'Plans projects.', instructions: 'Plan the project.',
  toolSelectors: ['projects.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'execute', isEnabled: true
};

function scenario(agents: NhAssistantMockAdminAgent[] = [...desks, planner]): NhAssistantMockScenario {
  return {
    agents: [{ id: 'planner', version: 1, displayNameKey: 'Planner', descriptionKey: 'Plans projects.', canMutate: true }],
    turns: [],
    subjectTypes: [{ id: 'project', names: { en: 'Project' }, subjects: [] }],
    agentGroups: [{ id: 'desks', names: { en: 'Desks' }, agentIds: agents.some(agent => agent.id === 'intake-desk') ? ['intake-desk'] : [] }],
    admin: {
      handOffsAsData: {
        rules: [{ subjectType: 'project', toAgentsCreatedInAdministration: true, fromAgentsCreatedInAdministration: true }],
        maxPerSubjectPerDay: 5
      },
      accounts: [{ actorId: 'ops', displayName: 'Operations', detail: null }],
      agentMetadata: [{ name: 'cost-center', editable: true, fields: ['code'] }],
      runCatalog: {
        triggerEvents: [{ name: 'project.created', description: 'A project was created.', subjectType: 'project' }],
        subjectSets: [],
        ruleHandlers: [],
        approvalPolicies: ['intake'],
        profiles: []
      },
      agents
    }
  };
}

async function configure(script: NhAssistantMockScenario): Promise<void> {
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    providers: [
      provideTranslateService({ fallbackLang: 'en' }),
      provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
      provideNhAssistantMockApi(script)
    ]
  });
  await firstValueFrom(TestBed.inject(TranslateService).use('en'));
}

async function call(method: string, path: string, body?: unknown): Promise<{ status: number; body: any; headers: Headers }> {
  const response = await TestBed.inject(NhAssistantMockBackend).fetch(`/api/assistant/${path}`, {
    method,
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  const content = await response.text();
  return { status: response.status, body: content ? JSON.parse(content) : null, headers: response.headers };
}

/** The intake desk hands projects to the kickoff desk. */
async function connectDesks(): Promise<void> {
  const saved = await call('PUT', 'admin/agents/kickoff-desk/hand-offs/project', { sources: ['intake-desk'], description: 'Projects to start.' });
  expect(saved.status).withContext(JSON.stringify(saved.body)).toBe(200);
}

/** Exports both desks from the first host, with the kickoff desk's instructions and the intake skill locked. */
async function exportDesks(options: object = {}): Promise<AgentPackage> {
  await configure(scenario());
  await connectDesks();
  const exported = await call('POST', 'admin/agent-packages/export', {
    agentIds: ['intake-desk', 'kickoff-desk'],
    options: { locks: { 'kickoff-desk': { agent: ['instructions'] }, 'intake-desk': { 'skill:intake': ['*'] } }, ...options }
  });
  expect(exported.status).withContext(JSON.stringify(exported.body)).toBe(200);
  return exported.body as AgentPackage;
}

/** A second host with the same application and the planner, but without the desks. */
async function secondHost(): Promise<void> {
  await configure(scenario([planner]));
}

function partsOf(preview: AgentPackagePreview): Record<string, string> {
  return Object.fromEntries(preview.parts.map(part => [`${part.agentId}/${part.part}`, part.outcome]));
}

describe('Agent packages in the mock API', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('exports exactly the chosen agents with their parts, locks and hashes, and warns about references outside them', async () => {
    await configure(scenario());
    await connectDesks();

    const kickoff = await call('POST', 'admin/agent-packages/export', { agentIds: ['kickoff-desk'] });
    expect(kickoff.status).toBe(200);
    expect(kickoff.headers.get('content-disposition')).toContain('agent-package-kickoff-desk.json');
    const warnings = JSON.parse(kickoff.headers.get('X-NewHeap-Package-Warnings') ?? '[]') as AgentPackageWarning[];
    expect(warnings).toEqual([{ agentId: 'kickoff-desk', part: 'handOff:project', code: 'hand-off-source', referencedAgentId: 'intake-desk' }]);

    const agentPackage = await exportDesks();
    expect(agentPackage.format).toBe('newheap.assistant.agent-package');
    expect(agentPackage.formatVersion).toBe(1);
    expect(agentPackage.package).toEqual({ id: 'intake-desk', version: 1 });
    const intake = agentPackage.agents.find(agent => agent.id === 'intake-desk')!;
    expect(intake.source).toBe('admin');
    expect(intake.definition?.instructions).toBe('Review each intake.');
    // The technical account never travels.
    expect(intake.runSettings).toEqual(jasmine.objectContaining({ accountability: 'service-account' }));
    expect(JSON.stringify(intake.runSettings)).not.toContain('ops');
    expect(intake.skills?.map(skill => skill.id)).toEqual(['intake']);
    expect(intake.triggers?.map(trigger => trigger.id)).toEqual(['intake-created']);
    expect(intake.metadata).toEqual({ 'cost-center': { code: 'CC-1' } });
    expect(intake.groups).toEqual(['desks']);
    expect(intake.locks).toEqual({ 'skill:intake': ['*'] });
    expect(intake.hashes?.['skill:intake']).toBe(mockSha256(canonicalJson(intake.skills![0])));
    const kickoffAgent = agentPackage.agents.find(agent => agent.id === 'kickoff-desk')!;
    expect(kickoffAgent.handOffs).toEqual([jasmine.objectContaining({ subjectType: 'project', sources: ['intake-desk'] })]);
    expect(kickoffAgent.locks).toEqual({ agent: ['instructions'] });

    const refused = await call('POST', 'admin/agent-packages/export', {
      agentIds: ['intake-desk'],
      options: { locks: { 'intake-desk': { 'skill:intake': ['cron'] } } }
    });
    expect(refused.status).toBe(400);
    expect(refused.body.errors).toEqual({ locks: ['invalid'] });
    expect((await call('POST', 'admin/agent-packages/export', { agentIds: ['nobody'] })).body.code).toBe('assistant-agent-not-found');
  });

  it('hashes with SHA-256 over the canonical JSON', () => {
    expect(mockSha256('')).toBe('e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855');
    expect(mockSha256('abc')).toBe('ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad');
    expect(canonicalJson({ b: 1, a: [true, null], c: null })).toBe('{"a":[true,null],"b":1}');
  });

  it('refuses agents the host has but did not import from the package, and reads packages strictly', async () => {
    const agentPackage = await exportDesks();
    // The same host has both desks, created here and not from this package.
    const preview = await call('POST', 'admin/agent-packages/preview', { package: agentPackage });
    expect(preview.status).toBe(200);
    expect(preview.body.importable).toBeFalse();
    expect(preview.body.parts.filter((part: { outcome: string }) => part.outcome === 'refused').map((part: { code: string }) => part.code))
      .toEqual(['assistant-agent-exists', 'assistant-agent-exists']);

    const unknownProperty = { ...agentPackage, signature: 'x' };
    expect((await call('POST', 'admin/agent-packages/preview', { package: unknownProperty })).body.code).toBe('assistant-package-format-unsupported');
    const newer = { ...agentPackage, formatVersion: 2 };
    const refusedNewer = await call('POST', 'admin/agent-packages/preview', { package: newer });
    expect([refusedNewer.status, refusedNewer.body.code]).toEqual([400, 'assistant-package-format-unsupported']);
    const tooMany = { ...agentPackage, agents: Array.from({ length: 21 }, (_, index) => ({ ...agentPackage.agents[0], id: `agent-${index}` })) };
    const refusedMany = await call('POST', 'admin/agent-packages/preview', { package: tooMany });
    expect([refusedMany.status, refusedMany.body.code]).toEqual([413, 'assistant-package-too-large']);
  });

  it('previews per part and imports every part through the save validators, with the technical account named', async () => {
    const agentPackage = await exportDesks();
    await secondHost();

    const withoutAccount = (await call('POST', 'admin/agent-packages/preview', { package: agentPackage })).body as AgentPackagePreview;
    expect(withoutAccount.importable).toBeFalse();
    expect(withoutAccount.parts.find(part => part.agentId === 'intake-desk' && part.part === 'runSettings')?.errors)
      .toEqual({ serviceAccountActorId: ['required'] });

    const options = { serviceAccounts: { 'intake-desk': 'ops' } };
    const preview = (await call('POST', 'admin/agent-packages/preview', { package: agentPackage, options })).body as AgentPackagePreview;
    expect(preview.importable).toBeTrue();
    expect(preview.installedVersion).toBeNull();
    expect(partsOf(preview)).toEqual({
      'intake-desk/agent': 'created',
      'kickoff-desk/agent': 'created',
      'intake-desk/runSettings': 'created',
      'kickoff-desk/runSettings': 'created',
      'intake-desk/skill:intake': 'created',
      'intake-desk/trigger:intake-created': 'created',
      'kickoff-desk/handOff:project': 'created',
      'intake-desk/metadata:cost-center': 'created'
    });
    // The preview changed nothing.
    expect(((await call('GET', 'admin/agents')).body as AdminAgent[]).map(agent => agent.id)).toEqual(['planner']);

    const imported = await call('POST', 'admin/agent-packages/import', { package: agentPackage, options });
    expect(imported.status).withContext(JSON.stringify(imported.body)).toBe(200);
    expect(imported.body.parts.length).toBe(8);

    const agents = (await call('GET', 'admin/agents')).body as AdminAgent[];
    const kickoff = agents.find(agent => agent.id === 'kickoff-desk')!;
    expect(kickoff.package?.packageId).toBe('intake-desk');
    expect(kickoff.package?.parts.find(part => part.part === 'agent')?.lockedFields).toEqual(['instructions']);
    expect(agents.find(agent => agent.id === 'intake-desk')?.groups).toEqual(['desks']);
    const configuration = (await call('GET', 'admin/agents/intake-desk/configuration')).body as AgentRunConfiguration;
    expect(configuration.runSettings?.serviceAccountActorId).toBe('ops');
    expect(configuration.dataSkills[0].package).toEqual(jasmine.objectContaining({
      part: 'skill:intake', packageId: 'intake-desk', packageVersion: 1, lockedFields: ['*'], changedLocally: false
    }));
    const handOffs = (await call('GET', 'admin/agents/kickoff-desk/hand-offs')).body as AgentHandOffs;
    expect(handOffs.dataHandOffs[0].package?.part).toBe('handOff:project');

    // The same package again changes nothing.
    const again = (await call('POST', 'admin/agent-packages/preview', { package: agentPackage, options })).body as AgentPackagePreview;
    expect(again.installedVersion).toBe(1);
    expect(new Set(again.parts.map(part => part.outcome))).toEqual(new Set(['unchanged']));
  });

  it('imports all or nothing: one refused part leaves the host as it was', async () => {
    const agentPackage = await exportDesks();
    await secondHost();
    const broken = structuredClone(agentPackage);
    broken.agents.find(agent => agent.id === 'intake-desk')!.skills![0].approvalPolicy = 'unknown-policy';

    const refused = await call('POST', 'admin/agent-packages/import', { package: broken, options: { serviceAccounts: { 'intake-desk': 'ops' } } });
    expect(refused.status).toBe(409);
    expect(refused.body.code).toBe('assistant-package-refused');
    // The trigger that names the refused skill is refused with it.
    expect(refused.body.errors).toEqual({
      'intake-desk/skill:intake/approvalPolicy': ['not-found'],
      'intake-desk/trigger:intake-created/skill': ['not-found']
    });
    expect(((await call('GET', 'admin/agents')).body as AdminAgent[]).map(agent => agent.id)).toEqual(['planner']);

    const api = TestBed.inject(NhAssistantAdminApiService);
    const failure = await firstValueFrom(api.importAgentPackage(broken, { serviceAccounts: { 'intake-desk': 'ops' } })).catch((error: unknown) => error);
    expect((failure as NhAssistantApiError).code).toBe('assistant-package-refused');
  });

  it('refuses a change of a locked field in every save path and a removal of a locked part, and lets operations and unlocked fields change', async () => {
    const agentPackage = await exportDesks();
    await secondHost();
    expect((await call('POST', 'admin/agent-packages/import', { package: agentPackage, options: { serviceAccounts: { 'intake-desk': 'ops' } } })).status).toBe(200);

    const configuration = (await call('GET', 'admin/agents/intake-desk/configuration')).body as AgentRunConfiguration;
    const skill = configuration.dataSkills[0];
    const locked = await call('PUT', 'admin/agents/intake-desk/skills/intake', {
      description: 'Another description.', instructions: 'Other instructions.', tools: [], expectedVersion: skill.version, apply: 'live'
    });
    expect([locked.status, locked.body.code]).toEqual([400, 'assistant-validation']);
    expect(locked.body.errors).toEqual({ description: ['locked'], instructions: ['locked'] });
    expect((await call('DELETE', `admin/agents/intake-desk/skills/intake?expectedVersion=${skill.version}`)).body.code).toBe('assistant-package-locked');

    const agents = (await call('GET', 'admin/agents')).body as AdminAgent[];
    const kickoff = agents.find(agent => agent.id === 'kickoff-desk')!;
    const input = {
      id: kickoff.id, displayName: kickoff.displayName, description: 'Starts projects quickly.', instructions: kickoff.instructions,
      toolSelectors: kickoff.toolSelectors, mcpServerIds: kickoff.mcpServerIds, requiredPolicy: kickoff.requiredPolicy, autonomy: kickoff.autonomy
    };
    const instructions = await call('PUT', 'admin/agents/kickoff-desk', { ...input, instructions: 'Something else.', expectedVersion: kickoff.version });
    expect(instructions.body.errors).toEqual({ instructions: ['locked'] });
    // An unlocked field changes, and the part then shows as changed locally.
    const description = await call('PUT', 'admin/agents/kickoff-desk', { ...input, expectedVersion: kickoff.version });
    expect(description.status).toBe(200);
    expect((description.body as AdminAgent).package?.parts.find(part => part.part === 'agent')?.changedLocally).toBeTrue();
    // Operations stay free; deleting and resetting a linked agent do not.
    expect((await call('POST', 'admin/agents/kickoff-desk/state', { state: 'paused' })).status).toBe(200);
    expect((await call('DELETE', 'admin/agents/kickoff-desk')).body.code).toBe('assistant-package-locked');
    // A linked part without locks may be removed; it leaves the package.
    const handOffs = (await call('GET', 'admin/agents/kickoff-desk/hand-offs')).body as AgentHandOffs;
    expect((await call('DELETE', `admin/agents/kickoff-desk/hand-offs/project?expectedVersion=${handOffs.dataHandOffs[0].version}`)).status).toBe(204);
  });

  it('unlinks a part, keeps it local on the next upgrade unless relinked, and leaves agents the package no longer holds', async () => {
    const agentPackage = await exportDesks();
    await secondHost();
    const options = { serviceAccounts: { 'intake-desk': 'ops' } };
    expect((await call('POST', 'admin/agent-packages/import', { package: agentPackage, options })).status).toBe(200);

    const unlinked = await call('POST', 'admin/agents/intake-desk/package/unlink', { part: 'skill:intake', reason: 'Our own wording.' });
    expect(unlinked.body).toEqual({ agentId: 'intake-desk', packageId: 'intake-desk', parts: ['skill:intake'] });
    expect((await call('POST', 'admin/agents/intake-desk/package/unlink', { part: 'skill:intake' })).body.code).toBe('assistant-package-not-linked');
    expect((await call('POST', 'admin/agents/intake-desk/package/unlink', { part: 'nonsense' })).body.errors).toEqual({ part: ['invalid'] });
    const skill = ((await call('GET', 'admin/agents/intake-desk/configuration')).body as AgentRunConfiguration).dataSkills[0];
    expect(skill.package).toBeUndefined();
    const saved = await call('PUT', 'admin/agents/intake-desk/skills/intake', {
      description: 'Reviews an intake our way.', instructions: 'Check it our way.', tools: [], expectedVersion: skill.version, apply: 'live'
    });
    expect(saved.status).toBe(200);

    // Version 2 changes the skill and no longer holds the kickoff desk.
    const upgrade = structuredClone(agentPackage);
    upgrade.package.version = 2;
    upgrade.agents = upgrade.agents.filter(agent => agent.id === 'intake-desk');
    upgrade.agents[0].skills![0].instructions = 'Check the intake twice.';
    const preview = (await call('POST', 'admin/agent-packages/preview', { package: upgrade, options })).body as AgentPackagePreview;
    expect(partsOf(preview)['intake-desk/skill:intake']).toBe('kept-local');
    expect(partsOf(preview)['kickoff-desk/agent']).toBe('left-behind');
    const relinked = (await call('POST', 'admin/agent-packages/preview', {
      package: upgrade,
      options: { ...options, relink: [{ agentId: 'intake-desk', part: 'skill:intake' }] }
    })).body as AgentPackagePreview;
    expect(partsOf(relinked)['intake-desk/skill:intake']).toBe('changed');

    expect((await call('POST', 'admin/agent-packages/import', { package: upgrade, options })).status).toBe(200);
    const agents = (await call('GET', 'admin/agents')).body as AdminAgent[];
    // The kickoff desk stays, unlinked, and can be deleted again.
    expect(agents.find(agent => agent.id === 'kickoff-desk')?.package).toBeUndefined();
    const kept = ((await call('GET', 'admin/agents/intake-desk/configuration')).body as AgentRunConfiguration).dataSkills[0];
    expect(kept.instructions).toBe('Check it our way.');
    expect(kept.package).toBeUndefined();
  });

  it('makes changed skills candidates with apply shadow, which promote within the locks', async () => {
    const agentPackage = await exportDesks();
    await secondHost();
    const options = { serviceAccounts: { 'intake-desk': 'ops' } };
    expect((await call('POST', 'admin/agent-packages/import', { package: agentPackage, options })).status).toBe(200);

    const upgrade = structuredClone(agentPackage);
    upgrade.package.version = 2;
    upgrade.agents.find(agent => agent.id === 'intake-desk')!.skills![0].instructions = 'Check the intake twice.';
    const imported = await call('POST', 'admin/agent-packages/import', { package: upgrade, options: { ...options, apply: 'shadow' } });
    expect(imported.status).toBe(200);
    const part = imported.body.parts.find((item: { part: string }) => item.part === 'skill:intake');
    expect(part.outcome).toBe('changed');
    expect(part.candidateVersion).toEqual(jasmine.any(Number));

    const skill = ((await call('GET', 'admin/agents/intake-desk/configuration')).body as AgentRunConfiguration).dataSkills[0];
    expect(skill.instructions).toBe('Check the intake.');
    expect(skill.candidate?.instructions).toBe('Check the intake twice.');
    const promoted = await call('POST', 'admin/agents/intake-desk/skills/intake/candidate/promote', { version: skill.candidate!.version });
    expect(promoted.status).withContext(JSON.stringify(promoted.body)).toBe(200);
    expect(promoted.body.instructions).toBe('Check the intake twice.');
    expect(promoted.body.package?.changedLocally).toBeFalse();
  });
});
