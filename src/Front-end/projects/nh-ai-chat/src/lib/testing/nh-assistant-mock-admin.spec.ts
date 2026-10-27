import { TestBed } from '@angular/core/testing';
import {
  AdminAgentInput,
  McpServerInput,
  NhAssistantAdminApiService,
  NhAssistantApiError,
  NhAssistantApiService,
  provideNhAssistant
} from '@newheap/platform-ai-chat';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';

const scenario: NhAssistantMockScenario = {
  agents: [{ id: 'projects', version: 1, displayNameKey: 'agents.projects', descriptionKey: 'agents.projects-description', canMutate: true }],
  turns: [],
  admin: {
    context: 'Sample context.',
    tools: [{ id: 'sample-api.project.list', source: 'bridge', effect: 'read-only', description: 'List projects' }],
    policies: ['app.project.view'],
    forwardUserTokenHosts: ['planning.example.com'],
    mcpServers: [
      {
        id: 'files',
        displayName: 'Files',
        url: 'https://files.example.com/mcp',
        authMode: 'bearer',
        headerName: null,
        secret: 'top-secret-value',
        requiredPolicy: null,
        isEnabled: true,
        remoteTools: [
          { remoteName: 'readFile', description: 'Reads a file', inputSchemaHash: 'a1', readOnlyHint: true },
          { remoteName: 'deleteFile', description: 'Deletes a file', inputSchemaHash: 'b1', readOnlyHint: false }
        ]
      }
    ],
    // The chat profile and its fallbacks permit public and internal data.
    profileClassifications: { chat: ['public', 'internal'] }
  }
};

const newAgent: AdminAgentInput = {
  id: 'support-agent',
  displayName: 'Support agent',
  description: 'Answers support questions.',
  instructions: 'Be helpful.',
  toolSelectors: ['sample-api.project.*'],
  mcpServerIds: ['files'],
  requiredPolicy: 'app.project.view',
  autonomy: 'propose',
  isEnabled: true
};

async function failure(promise: Promise<unknown>): Promise<NhAssistantApiError> {
  return promise.then(() => { throw new Error('Expected a failure.'); }, error => error as NhAssistantApiError);
}

describe('provideNhAssistantMockApi administration', () => {
  let admin: NhAssistantAdminApiService;
  let api: NhAssistantApiService;
  let backend: NhAssistantMockBackend;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
        provideNhAssistantMockApi(structuredClone(scenario))
      ]
    });
    admin = TestBed.inject(NhAssistantAdminApiService);
    api = TestBed.inject(NhAssistantApiService);
    backend = TestBed.inject(NhAssistantMockBackend);
  });

  it('reports canAdminister and answers 403 on admin endpoints when it is off', async () => {
    expect((await firstValueFrom(api.status())).canAdminister).toBeTrue();

    backend.setCanAdminister(false);

    expect((await firstValueFrom(api.status())).canAdminister).toBeFalse();
    expect((await failure(firstValueFrom(admin.getAgents()))).code).toBe('assistant-forbidden');
  });

  it('stores and validates preferences', async () => {
    expect(await firstValueFrom(api.getPreferences())).toEqual({ style: 'default', addressForm: 'informal', responseLength: 'normal', customInstructions: null });

    const saved = await firstValueFrom(api.updatePreferences({ style: 'direct', addressForm: 'formal', responseLength: 'short', customInstructions: 'Use bullet points.' }));
    const invalid = await failure(firstValueFrom(api.updatePreferences({ style: 'direct', addressForm: 'formal', responseLength: 'short', customInstructions: 'x'.repeat(1001) })));

    expect(saved.customInstructions).toBe('Use bullet points.');
    expect(invalid.code).toBe('assistant-validation');
  });

  it('versions the application context and rejects a stale expected version', async () => {
    const current = await firstValueFrom(admin.getContext());

    const saved = await firstValueFrom(admin.updateContext({ text: 'New context.', expectedVersion: current.version }));
    const conflict = await failure(firstValueFrom(admin.updateContext({ text: 'Other.', expectedVersion: current.version })));
    const versions = await firstValueFrom(admin.getContextVersions());

    expect(saved.version).toBe(current.version + 1);
    expect(saved.hash).not.toBe(current.hash);
    expect(conflict.code).toBe('assistant-version-conflict');
    expect(versions.map(version => version.version)).toEqual([2, 1]);
    expect(Object.keys(versions[0])).not.toContain('text');
  });

  it('manages agents: create, conflicting update, override and reset a code agent, no delete of code agents', async () => {
    const created = await firstValueFrom(admin.createAgent(newAgent));
    const updated = await firstValueFrom(admin.updateAgent(created.id, { ...newAgent, displayName: 'Support', expectedVersion: created.version }));
    const stale = await failure(firstValueFrom(admin.updateAgent(created.id, { ...newAgent, expectedVersion: created.version })));

    const code = (await firstValueFrom(admin.getAgents())).find(agent => agent.id === 'projects')!;
    const overridden = await firstValueFrom(admin.updateAgent('projects', { ...code, instructions: 'Override.', expectedVersion: code.version }));
    const reset = await firstValueFrom(admin.resetAgent('projects'));
    const deleteCode = await failure(firstValueFrom(admin.deleteAgent('projects'), { defaultValue: undefined }));
    await firstValueFrom(admin.deleteAgent(created.id), { defaultValue: undefined });

    expect(created.source).toBe('admin');
    expect(updated.version).toBe(2);
    expect(stale.code).toBe('assistant-version-conflict');
    expect(overridden).toEqual(jasmine.objectContaining({ source: 'code', isOverridden: true }));
    expect(reset).toEqual(jasmine.objectContaining({ source: 'code', isOverridden: false, instructions: '' }));
    expect(deleteCode.code).toBe('assistant-code-agent-not-deletable');
    expect((await firstValueFrom(admin.getAgents())).map(agent => agent.id)).toEqual(['projects']);
  });

  it('lists admin agents in the chat with their literal names and hides stopped agents', async () => {
    await firstValueFrom(admin.createAgent(newAgent));
    await firstValueFrom(admin.setAgentState('projects', { state: 'stopped', reason: 'Wrong answers' }));

    const agents = (await firstValueFrom(api.status())).agents;

    expect(agents.map(agent => agent.id)).toEqual(['support-agent']);
    expect(agents[0].displayNameKey).toBe('Support agent');
  });

  it('pauses, stops and resumes an agent without changing its definition, and ignores isEnabled', async () => {
    const before = (await firstValueFrom(admin.getAgents())).find(agent => agent.id === 'projects')!;
    const paused = await firstValueFrom(admin.setAgentState('projects', { state: 'paused', reason: ' Import ' }));
    const edited = await firstValueFrom(admin.updateAgent('projects', {
      ...before, instructions: 'Edited.', isEnabled: false, expectedVersion: before.version
    }));
    const stopped = await firstValueFrom(admin.setAgentState('projects', { state: 'stopped' }));
    const reset = await firstValueFrom(admin.resetAgent('projects'));
    const resumed = await firstValueFrom(admin.setAgentState('projects', { state: 'active' }));
    const invalid = await failure(firstValueFrom(admin.setAgentState('projects', { state: 'sleeping' as 'active' })));

    expect(paused).toEqual(jasmine.objectContaining({ runState: 'paused', active: false, reason: 'Import' }));
    expect(edited).toEqual(jasmine.objectContaining({ runState: 'paused', isEnabled: true, stateReason: 'Import' }));
    expect(stopped.runState).toBe('stopped');
    expect(reset).toEqual(jasmine.objectContaining({ runState: 'stopped', isEnabled: false }));
    expect(resumed).toEqual(jasmine.objectContaining({ runState: 'active', active: true }));
    expect(invalid.code).toBe('assistant-validation');
    expect((await firstValueFrom(admin.getAgentState('projects'))).runState).toBe('active');
  });

  it('switches an agent off and on, and lets a new agent start off', async () => {
    const off = await firstValueFrom(admin.setAgentState('projects', { state: 'off', reason: 'Not yet' }));
    const on = await firstValueFrom(admin.setAgentState('projects', { state: 'active' }));
    const created = await firstValueFrom(admin.createAgent({ ...newAgent, id: 'quiet-agent', startsOff: true }));
    const active = await firstValueFrom(admin.createAgent({ ...newAgent, id: 'busy-agent' }));

    expect(off).toEqual(jasmine.objectContaining({ runState: 'off', active: false, reason: 'Not yet', startedRuns: 0 }));
    expect(on).toEqual(jasmine.objectContaining({ runState: 'active', active: true, startedRuns: 0 }));
    expect(created).toEqual(jasmine.objectContaining({ runState: 'off', isEnabled: true, stateChangedBy: 'current-user' }));
    expect(Object.keys(created)).not.toContain('startsOff');
    expect(active.runState).toBe('active');
  });

  it('releases the kill switch without switching on an agent that was off, as the server does', async () => {
    const killSwitch = async (engaged: boolean) => (await backend.fetch('/api/assistant/admin/agents/projects/kill-switch', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ engaged })
    })).json() as Promise<{ enabled: boolean; runState: string }>;

    const stoppedAgent = async () => (await firstValueFrom(admin.getAgents())).find(agent => agent.id === 'projects')!;

    await firstValueFrom(admin.setAgentState('projects', { state: 'off' }));
    const engaged = await killSwitch(true);
    const stoppedFromOff = await firstValueFrom(admin.getAgentState('projects'));
    const listedFromOff = await stoppedAgent();
    const released = await killSwitch(false);
    const offAgain = await firstValueFrom(admin.getAgentState('projects'));
    await firstValueFrom(admin.setAgentState('projects', { state: 'paused' }));
    await killSwitch(true);
    const stoppedFromPaused = await firstValueFrom(admin.getAgentState('projects'));
    const resumed = await killSwitch(false);

    expect(engaged).toEqual(jasmine.objectContaining({ enabled: false, runState: 'stopped' }));
    // While stopped the state and the agent say that releasing switches it back off.
    expect(stoppedFromOff).toEqual(jasmine.objectContaining({ runState: 'stopped', offBeforeStop: true }));
    expect(listedFromOff).toEqual(jasmine.objectContaining({ runState: 'stopped', offBeforeStop: true }));
    expect(released).toEqual(jasmine.objectContaining({ enabled: true, runState: 'off' }));
    expect(offAgain.offBeforeStop).toBeFalse();
    expect(stoppedFromPaused).toEqual(jasmine.objectContaining({ runState: 'stopped', offBeforeStop: false }));
    expect(resumed).toEqual(jasmine.objectContaining({ enabled: true, runState: 'active' }));
  });

  it('adds a data skill that starts off with its classification, as the server checks both', async () => {
    const invalid = await failure(firstValueFrom(admin.saveDataSkill('projects', 'audit', {
      description: 'Audits the project.', dataClassification: 'secret' as 'internal'
    })));
    const saved = await firstValueFrom(admin.saveDataSkill('projects', 'audit', {
      description: 'Audits the project.', dataClassification: 'confidential', startsOff: true
    }));
    const changed = await failure(firstValueFrom(admin.saveDataSkill('projects', 'audit', {
      description: 'Audits the project again.', expectedVersion: 1, startsOff: true
    })));
    const state = await firstValueFrom(admin.getAgentState('projects'));

    expect(invalid.fieldErrors?.['dataClassification']).toEqual(['invalid']);
    expect(saved.dataClassification).toBe('confidential');
    expect(changed.fieldErrors?.['startsOff']).toEqual(['invalid']);
    expect(state.skills?.find(skill => skill.skillId === 'audit')?.runState).toBe('off');
  });

  it('lets an agent created in the administration use only a profile the run catalog offers', async () => {
    const unknown = await failure(firstValueFrom(admin.createAgent({ ...newAgent, id: 'profiled-agent', profile: 'missing-chat' })));
    const standard = await firstValueFrom(admin.createAgent({ ...newAgent, id: 'standard-agent' }));

    expect(unknown.fieldErrors?.['profile']).toEqual(['not-found']);
    expect(standard.profile).toBe('chat');
  });

  it('lets an agent created in the administration choose its classification and keeps it when omitted, as the server does', async () => {
    const invalid = await failure(firstValueFrom(admin.createAgent({ ...newAgent, id: 'secret-agent', dataClassification: 'secret' as 'internal' })));
    const created = await firstValueFrom(admin.createAgent({ ...newAgent, id: 'figures-agent', dataClassification: 'confidential' }));
    const kept = await firstValueFrom(admin.updateAgent('figures-agent', { ...newAgent, id: 'figures-agent', expectedVersion: created.version }));
    const standard = await firstValueFrom(admin.createAgent({ ...newAgent, id: 'notes-agent' }));

    expect(invalid.fieldErrors?.['dataClassification']).toEqual(['invalid']);
    expect(created.dataClassification).toBe('confidential');
    expect(kept.dataClassification).toBe('confidential');
    expect(standard.dataClassification).toBeNull();
  });

  it('saves an agent and a data skill whose classification no profile permits with a warning, as the server does', async () => {
    const agent = await firstValueFrom(admin.createAgent({ ...newAgent, id: 'vault-agent', dataClassification: 'restricted' }));
    const standard = await firstValueFrom(admin.createAgent({ ...newAgent, id: 'notes-agent' }));
    const skill = await firstValueFrom(admin.saveDataSkill('projects', 'audit', { description: 'Audits.', dataClassification: 'confidential' }));
    const permitted = await firstValueFrom(admin.saveDataSkill('projects', 'notes', { description: 'Notes.', dataClassification: 'internal' }));
    const configuration = await firstValueFrom(admin.getAgentConfiguration('projects'));

    expect(agent.warnings).toEqual(['classification-not-permitted']);
    expect(standard.warnings).toEqual([]);
    expect(skill.warnings).toEqual(['classification-not-permitted']);
    expect(permitted.warnings).toEqual([]);
    expect(configuration.dataSkills.find(item => item.skillId === 'audit')?.warnings).toEqual(['classification-not-permitted']);
  });

  it('stores whether a data skill and the run settings of an agent accept files', async () => {
    const skill = await firstValueFrom(admin.saveDataSkill('projects', 'audit', {
      description: 'Audits the project documents.', acceptsFiles: true
    }));
    const unchanged = await firstValueFrom(admin.saveDataSkill('projects', 'notes', { description: 'Writes notes.' }));

    expect(skill.acceptsFiles).toBeTrue();
    expect(unchanged.acceptsFiles).toBeFalse();
  });

  it('stores lower file limits of an agent and a data skill only within the application\'s and next to accepting files, as the server does', async () => {
    await firstValueFrom(admin.createAgent({ ...newAgent, id: 'reader-agent' }));
    const catalog = await firstValueFrom(admin.getRunCatalog());
    const above = await failure(firstValueFrom(admin.saveRunSettings('reader-agent', {
      accountability: 'agent', acceptsFiles: true, maxFileBytes: 20 * 1024 * 1024 + 1, maxFilePages: 0, maxFilesPerTurn: 6, maxFileTokensPerTurn: 100_001
    })));
    const withoutFiles = await failure(firstValueFrom(admin.saveDataSkill('projects', 'audit', { description: 'Audits.', maxFilesPerTurn: 1 })));
    const saved = await firstValueFrom(admin.saveRunSettings('reader-agent', { accountability: 'agent', acceptsFiles: true, maxFileBytes: 1_024, maxFilesPerTurn: 2 }));
    const skill = await firstValueFrom(admin.saveDataSkill('projects', 'audit', { description: 'Audits.', acceptsFiles: true, maxFilePages: 10 }));

    expect(catalog.fileLimits).toEqual({ maxFileBytes: 20 * 1024 * 1024, maxFilePages: 100, maxFilesPerTurn: 5, maxFileTokensPerTurn: 100_000 });
    expect(above.code).toBe('assistant-validation');
    expect(above.fieldErrors).toEqual({
      maxFileBytes: ['invalid'], maxFilePages: ['invalid'], maxFilesPerTurn: ['invalid'], maxFileTokensPerTurn: ['invalid']
    });
    expect(withoutFiles.fieldErrors).toEqual({ maxFilesPerTurn: ['invalid'] });
    expect([saved.maxFileBytes, saved.maxFilePages, saved.maxFilesPerTurn, saved.maxFileTokensPerTurn]).toEqual([1_024, null, 2, null]);
    expect([skill.maxFilePages, skill.maxFileBytes]).toEqual([10, null]);
  });

  it('rejects invalid agents', async () => {
    const invalid = [
      { ...newAgent, id: 'Not Dash Case' },
      { ...newAgent, mcpServerIds: ['unknown'] },
      { ...newAgent, requiredPolicy: 'app.unknown' }
    ];

    for (const input of invalid) {
      expect((await failure(firstValueFrom(admin.createAgent(input)))).code).toBe('assistant-validation');
    }
    expect((await failure(firstValueFrom(admin.createAgent({ ...newAgent, instructions: 'x'.repeat(20_001) })))).code)
      .toBe('assistant-instructions-too-long');
  });

  it('never returns a secret, keeps or replaces it and requires one for a mode that sends it', async () => {
    const [server] = await firstValueFrom(admin.getMcpServers());
    const { hasSecret, lastSyncAt, lastSyncStatus, assignedAgentIds, ...input } = server;

    expect(JSON.stringify(backend.requests)).not.toContain('top-secret-value');
    expect(hasSecret).toBeTrue();
    expect(JSON.stringify(server)).not.toContain('top-secret-value');

    const kept = await firstValueFrom(admin.updateMcpServer('files', { ...input, displayName: 'Files 2' }));
    const refusedClear = await failure(firstValueFrom(admin.updateMcpServer('files', { ...input, secret: '' })));
    const replaced = await firstValueFrom(admin.updateMcpServer('files', { ...input, secret: 'new-value' }));
    const withoutSecret = await failure(firstValueFrom(admin.createMcpServer({ ...input, id: 'keyless', authMode: 'api-key', secret: undefined })));
    const noneMode = await firstValueFrom(admin.updateMcpServer('files', { ...input, authMode: 'none', secret: undefined }));

    expect(kept.hasSecret).toBeTrue();
    expect(refusedClear).toEqual(jasmine.objectContaining({ status: 400, code: 'assistant-validation', fieldErrors: { secret: ['required'] } }));
    expect(replaced.hasSecret).toBeTrue();
    expect(JSON.stringify(replaced)).not.toContain('new-value');
    expect(withoutSecret).toEqual(jasmine.objectContaining({ status: 400, code: 'assistant-validation' }));
    // A mode without a secret drops the stored one.
    expect(noneMode.hasSecret).toBeFalse();
  });

  it('keeps a stored secret only on the origin, mode and api-key header it was given for, as the server does', async () => {
    const [server] = await firstValueFrom(admin.getMcpServers());
    const { hasSecret, lastSyncAt, lastSyncStatus, assignedAgentIds, ...input } = server;

    const otherPath = await firstValueFrom(admin.updateMcpServer('files', { ...input, url: 'https://files.example.com/v2/mcp' }));
    const otherOrigin = await failure(firstValueFrom(admin.updateMcpServer('files', { ...input, url: 'https://collector.example.org/mcp' })));
    const otherMode = await failure(firstValueFrom(admin.updateMcpServer('files', { ...input, authMode: 'api-key' })));
    const moved = await firstValueFrom(admin.updateMcpServer('files', { ...input, url: 'https://collector.example.org/mcp', secret: 'entered-again' }));
    const there = { ...input, url: 'https://collector.example.org/mcp', authMode: 'api-key' as const };
    const apiKey = await firstValueFrom(admin.updateMcpServer('files', { ...there, headerName: 'X-Api-Key', secret: 'api-key' }));
    const sameHeader = await firstValueFrom(admin.updateMcpServer('files', { ...there, headerName: 'x-api-key' }));
    const otherHeader = await failure(firstValueFrom(admin.updateMcpServer('files', { ...there, headerName: 'X-Other-Key' })));

    expect(otherPath.hasSecret).toBeTrue();
    for (const refused of [otherOrigin, otherMode, otherHeader]) {
      expect(refused).toEqual(jasmine.objectContaining({ status: 400, code: 'assistant-validation', fieldErrors: { secret: ['required'] } }));
    }
    expect([moved.url, moved.hasSecret]).toEqual(['https://collector.example.org/mcp', true]);
    expect([apiKey.hasSecret, sameHeader.hasSecret]).toEqual([true, true]);
  });

  it('syncs tools disabled as mutations, keeps hints as hints and disables a tool on a schema change', async () => {
    const synced = await firstValueFrom(admin.syncMcpServer('files'));

    expect(synced.map(tool => [tool.localId, tool.isEnabled, tool.effect, tool.status, tool.readOnlyHint])).toEqual([
      ['mcp.files.read-file', false, 'mutation', 'available', true],
      ['mcp.files.delete-file', false, 'mutation', 'available', false]
    ]);

    await firstValueFrom(admin.updateMcpTool('files', 'readFile', { isEnabled: true, effect: 'read-only', descriptionOverride: 'Read one file' }));
    expect((await firstValueFrom(admin.getTools())).map(tool => tool.id)).toContain('mcp.files.read-file');

    backend.setRemoteTools('files', [{ remoteName: 'readFile', description: 'Reads a file', inputSchemaHash: 'a2', readOnlyHint: true }]);
    const resynced = await firstValueFrom(admin.syncMcpServer('files'));

    expect(resynced.find(tool => tool.remoteName === 'readFile')).toEqual(jasmine.objectContaining({ isEnabled: false, status: 'schema-changed' }));
    expect(resynced.find(tool => tool.remoteName === 'deleteFile')).toEqual(jasmine.objectContaining({ status: 'missing' }));
    expect((await firstValueFrom(admin.getTools())).map(tool => tool.id)).not.toContain('mcp.files.read-file');
  });

  it('reports connection failures with contract codes', async () => {
    const base = { displayName: 'Server', headerName: null, requiredPolicy: null, isEnabled: true };
    await firstValueFrom(admin.createMcpServer({ ...base, id: 'blocked', url: 'https://169.254.169.254/mcp', authMode: 'none' }));
    await firstValueFrom(admin.createMcpServer({ ...base, id: 'down', url: 'https://unreachable.example.com/mcp', authMode: 'none' }));
    await firstValueFrom(admin.createMcpServer({ ...base, id: 'forward', url: 'https://other.example.com/mcp', authMode: 'forward-user-token' }));
    await firstValueFrom(admin.createMcpServer({ ...base, id: 'keyless', url: 'https://keys.example.com/mcp', authMode: 'api-key', secret: 'key' }));
    await firstValueFrom(admin.createMcpServer({ ...base, id: 'planning', url: 'https://planning.example.com/mcp', authMode: 'forward-user-token' }));

    const results = await Promise.all(['blocked', 'down', 'forward', 'keyless', 'planning'].map(id => firstValueFrom(admin.testMcpServer(id))));
    const syncFailure = await failure(firstValueFrom(admin.syncMcpServer('down')));

    expect(results.map(result => result.code)).toEqual([
      'assistant-mcp-host-blocked', 'assistant-mcp-unreachable', 'assistant-mcp-host-blocked', null, null
    ]);
    const blockedSync = await failure(firstValueFrom(admin.syncMcpServer('blocked')));

    expect(syncFailure).toEqual(jasmine.objectContaining({ status: 502, code: 'assistant-mcp-unreachable' }));
    expect(blockedSync).toEqual(jasmine.objectContaining({ status: 400, code: 'assistant-mcp-host-blocked' }));
    expect((await firstValueFrom(admin.getMcpServers())).find(server => server.id === 'down')?.lastSyncStatus).toBe('failed');
    expect((await firstValueFrom(admin.getMcpServers())).find(server => server.id === 'keyless')?.headerName).toBe('X-Api-Key');
  });

  it('keeps headless use per server and records the connection state like the server', async () => {
    const base = { displayName: 'Server', headerName: null, requiredPolicy: null, isEnabled: true, authMode: 'none' as const };
    const created = await firstValueFrom(admin.createMcpServer({ ...base, id: 'docs', url: 'https://docs.example.com/mcp' }));
    const allowed = await firstValueFrom(admin.updateMcpServer('docs', {
      ...base, id: 'docs', url: 'https://docs.example.com/mcp', headlessUse: 'organization'
    }));
    const kept = await firstValueFrom(admin.updateMcpServer('docs', { ...base, id: 'docs', url: 'https://docs.example.com/mcp' }));
    const forwarded = await failure(firstValueFrom(admin.updateMcpServer('docs', {
      ...base, id: 'docs', url: 'https://docs.example.com/mcp', authMode: 'forward-user-token'
    })));
    await firstValueFrom(admin.testMcpServer('docs'));
    const [server] = (await firstValueFrom(admin.getMcpServers())).filter(item => item.id === 'docs');

    expect(created.headlessUse).toBe('never');
    expect(created.lastConnectAt).toBeNull();
    expect(allowed.headlessUse).toBe('organization');
    expect(kept.headlessUse).toBe('organization');
    expect(forwarded).toEqual(jasmine.objectContaining({ status: 400, code: 'assistant-validation' }));
    expect(server.lastConnectAt).not.toBeNull();
    expect(server.lastConnectCode).toBeNull();
  });

  it('validates basic authentication, secret references and client certificates like the server', async () => {
    const base = { displayName: 'Vault', headerName: null, requiredPolicy: null, isEnabled: true, id: 'vault', url: 'https://vault.example.com/mcp' };
    const noUser = await failure(firstValueFrom(admin.createMcpServer({ ...base, authMode: 'basic', secret: 'basic-password' })));
    const basic = await firstValueFrom(admin.createMcpServer({ ...base, authMode: 'basic', userName: 'planner', secret: 'basic-password' }));
    const referenced = await firstValueFrom(admin.updateMcpServer('vault', { ...base, authMode: 'bearer', secretReference: 'config:vault-token' }));
    const unknownPrefix = await failure(firstValueFrom(admin.updateMcpServer('vault', { ...base, authMode: 'bearer', secretReference: 'vault:token' })));
    const climbing = await failure(firstValueFrom(admin.updateMcpServer('vault', { ...base, authMode: 'bearer', secretReference: 'config:a/../b' })));
    const certificate = await failure(firstValueFrom(admin.updateMcpServer('vault', { ...base, authMode: 'none', clientCertificateReference: 'missing' })));
    const auto = await firstValueFrom(admin.updateMcpServer('vault', { ...base, authMode: 'none', transport: 'auto' }));

    expect(noUser).toEqual(jasmine.objectContaining({ status: 400, code: 'assistant-validation' }));
    expect(basic.userName).toBe('planner');
    expect(basic.hasSecret).toBeTrue();
    expect(basic.transport).toBe('streamable-http');
    expect(JSON.stringify(basic)).not.toContain('basic-password');
    expect(referenced.secretReference).toBe('config:vault-token');
    expect(referenced.hasSecret).toBeFalse();
    expect(referenced.userName).toBeNull();
    // A reference the application cannot resolve is a field error, as on the server.
    expect(unknownPrefix).toEqual(jasmine.objectContaining({ status: 400, code: 'assistant-validation' }));
    expect(unknownPrefix.fieldErrors).toEqual({ secretReference: ['not-found'] });
    expect(climbing.code).toBe('assistant-validation');
    expect(climbing.fieldErrors).toEqual({ secretReference: ['invalid'] });
    expect(certificate.fieldErrors).toEqual({ clientCertificateReference: ['not-found'] });
    expect(auto.transport).toBe('auto');
    expect(auto.secretReference).toBeNull();
  });

  it('records the caller\'s consent to a personal server until its address or authentication changes', async () => {
    const planning = {
      id: 'planning', displayName: 'Planning', url: 'https://planning.example.com/mcp', authMode: 'forward-user-token' as const,
      headerName: null, requiredPolicy: null, isEnabled: true, headlessUse: 'personal' as const
    };
    await firstValueFrom(admin.createMcpServer(planning));
    const unassigned = await firstValueFrom(api.getMcpConnections());
    await firstValueFrom(admin.createAgent({ ...newAgent, mcpServerIds: ['planning'] }));
    const [listed] = await firstValueFrom(api.getMcpConnections());
    const allowed = await firstValueFrom(api.updateMcpConnection('planning', { allowBackgroundUse: true }));
    const notPersonal = await failure(firstValueFrom(api.updateMcpConnection('files', { allowBackgroundUse: true })));
    await firstValueFrom(admin.updateMcpServer('planning', { ...planning, url: 'https://planning.example.com/mcp/v2' }));
    const [moved] = await firstValueFrom(api.getMcpConnections());

    expect(unassigned).toEqual([]);
    expect(listed).toEqual({
      serverId: 'planning', displayName: 'Planning', host: 'planning.example.com', allowBackgroundUse: false, consentedAt: null, agentIds: ['support-agent'],
      connectionStatus: null, grantedScopes: null, requiredScopes: null, connectedAt: null, offersBackgroundUse: true
    });
    expect(allowed.allowBackgroundUse).toBeTrue();
    expect(allowed.consentedAt).not.toBeNull();
    expect(notPersonal).toEqual(jasmine.objectContaining({ status: 404, code: 'assistant-mcp-server-not-found' }));
    expect(moved.allowBackgroundUse).toBeFalse();
  });

  it('answers with the specific contract codes for duplicates, missing items and resets', async () => {
    await firstValueFrom(admin.createAgent(newAgent));
    const [server] = await firstValueFrom(admin.getMcpServers());
    const { hasSecret, lastSyncAt, lastSyncStatus, assignedAgentIds, ...listed } = server;
    // A valid input for an id that exists; the server checks the fields first.
    const serverInput = { ...listed, secret: 'another-secret' };

    const failures = await Promise.all([
      failure(firstValueFrom(admin.createAgent(newAgent))),
      failure(firstValueFrom(admin.createMcpServer(serverInput))),
      failure(firstValueFrom(admin.resetAgent(newAgent.id))),
      failure(firstValueFrom(admin.getMcpTools('unknown'))),
      // A tool no sync has seen waits for one; a tool of an unknown server is refused.
      failure(firstValueFrom(admin.updateMcpTool('unknown', 'readFile', { isEnabled: false, effect: 'mutation', descriptionOverride: null }))),
      failure(firstValueFrom(admin.resetAgent('unknown')))
    ]);

    expect(failures.map(item => [item.status, item.code])).toEqual([
      [409, 'assistant-agent-exists'],
      [409, 'assistant-mcp-server-exists'],
      [409, 'assistant-agent-not-code'],
      [404, 'assistant-mcp-server-not-found'],
      [404, 'assistant-mcp-server-not-found'],
      [404, 'assistant-not-found']
    ]);
    expect(failures.every(item => item.messageKey === `nh-assistant.errors.${item.code}`)).toBeTrue();
  });

  it('removes agent assignments when a server is deleted', async () => {
    await firstValueFrom(admin.createAgent(newAgent));
    expect((await firstValueFrom(admin.getMcpServers()))[0].assignedAgentIds).toEqual(['support-agent']);

    await firstValueFrom(admin.deleteMcpServer('files'), { defaultValue: undefined });

    expect((await firstValueFrom(admin.getAgents())).find(agent => agent.id === 'support-agent')?.mcpServerIds).toEqual([]);
  });

  describe('OAuth', () => {
    const crm: McpServerInput = {
      id: 'crm',
      displayName: 'CRM',
      url: 'https://crm.example.com/mcp',
      authMode: 'oauth',
      headerName: null,
      requiredPolicy: null,
      isEnabled: true,
      connectionScope: 'organization',
      oauth: { clientId: 'assistant', clientSecret: 'test-client-secret-value', scopes: 'mcp:read', organizationAccountLabel: 'CRM service account', confirmAuthorization: true }
    };

    it('discovers without storing and pins the authorization server only when the administrator confirms it', async () => {
      const discovered = await firstValueFrom(admin.discoverMcpServer(crm.url));
      const unconfirmed = await failure(firstValueFrom(admin.createMcpServer({ ...crm, oauth: { ...crm.oauth, confirmAuthorization: false } })));
      const withoutLabel = await failure(firstValueFrom(admin.createMcpServer({ ...crm, oauth: { ...crm.oauth, organizationAccountLabel: '' } })));
      const withoutPkce = await failure(firstValueFrom(admin.createMcpServer({ ...crm, url: 'https://no-pkce.example.com/mcp' })));
      const nothing = await failure(firstValueFrom(admin.discoverMcpServer('https://no-oauth.example.com/mcp')));
      const created = await firstValueFrom(admin.createMcpServer(crm));
      const text = JSON.stringify(await firstValueFrom(admin.getMcpServers()));

      expect(discovered.issuer).toBe('https://auth.crm.example.com');
      expect(discovered.pkceSupported).toBeTrue();
      expect(unconfirmed.fieldErrors).toEqual({ 'oauth.confirmAuthorization': ['required'] });
      expect(withoutLabel.fieldErrors).toEqual({ 'oauth.organizationAccountLabel': ['required'] });
      expect(withoutPkce.code).toBe('assistant-mcp-pkce-unsupported');
      expect(nothing).toEqual(jasmine.objectContaining({ status: 502, code: 'assistant-mcp-discovery-failed' }));
      expect(created.authorization?.issuer).toBe('https://auth.crm.example.com');
      expect(created.oauth).toEqual({ clientId: 'assistant', hasClientSecret: true, clientSecretReference: null, scopes: 'mcp:read', organizationAccountLabel: 'CRM service account' });
      expect(created.organizationConnection?.status).toBe('not-connected');
      expect(text).not.toContain('test-client-secret-value');
    });

    it('connects the organization through the authorization window and completes only for an administrator', async () => {
      await firstValueFrom(admin.createMcpServer(crm));
      const untested = await firstValueFrom(admin.testMcpServer('crm'));

      const start = await firstValueFrom(admin.authorizeMcpOrganizationConnection('crm'));
      const outcome = await firstValueFrom(backend.authorizationWindow.open().authorize(start));
      backend.setCanAdminister(false);
      const forbidden = await failure(firstValueFrom(admin.completeMcpAuthorization({ flowId: start.flowId, completionCode: outcome.completionCode! })));
      backend.setCanAdminister(true);
      const wrong = await failure(firstValueFrom(admin.completeMcpAuthorization({ flowId: start.flowId, completionCode: 'guessed' })));
      const completed = await firstValueFrom(admin.completeMcpAuthorization({ flowId: start.flowId, completionCode: outcome.completionCode! }));
      const again = await failure(firstValueFrom(admin.completeMcpAuthorization({ flowId: start.flowId, completionCode: outcome.completionCode! })));
      const connection = await firstValueFrom(admin.getMcpOrganizationConnection('crm'));
      const tested = await firstValueFrom(admin.testMcpServer('crm'));

      expect(untested.code).toBe('assistant-mcp-connection-required');
      expect(start.authorizationUrl).toContain('https://auth.crm.example.com/authorize?');
      expect(backend.authorizationWindow.shown).toEqual([start.authorizationUrl]);
      expect(forbidden).toEqual(jasmine.objectContaining({ status: 403, code: 'assistant-forbidden' }));
      expect(wrong.code).toBe('assistant-mcp-authorization-invalid');
      expect(completed).toEqual({ serverId: 'crm', ownerKind: 'organization', status: 'active', grantedScopes: 'mcp:read' });
      expect(again.code).toBe('assistant-mcp-authorization-expired');
      expect(connection.status).toBe('active');
      expect(tested.ok).toBeTrue();

      backend.setMcpConnectionStatus('crm', 'reauthorization-required');
      expect((await firstValueFrom(admin.testMcpServer('crm'))).code).toBe('assistant-mcp-reauthorization-required');
      await firstValueFrom(admin.disconnectMcpOrganizationConnection('crm'), { defaultValue: undefined });
      expect((await firstValueFrom(admin.getMcpOrganizationConnection('crm'))).status).toBe('not-connected');
    });

    it('reports a denied or closed authorization with its code, and changing the address ends the connection', async () => {
      await firstValueFrom(admin.createMcpServer({ ...crm }));
      backend.setMcpAuthorizationOutcome('deny');
      const denied = await firstValueFrom(backend.authorizationWindow.open().authorize(await firstValueFrom(admin.authorizeMcpOrganizationConnection('crm'))));
      backend.setMcpAuthorizationOutcome('cancel');
      const cancelled = await firstValueFrom(backend.authorizationWindow.open().authorize(await firstValueFrom(admin.authorizeMcpOrganizationConnection('crm'))));
      backend.setMcpAuthorizationOutcome('approve');
      const start = await firstValueFrom(admin.authorizeMcpOrganizationConnection('crm'));
      const approved = await firstValueFrom(backend.authorizationWindow.open().authorize(start));
      await firstValueFrom(admin.completeMcpAuthorization({ flowId: start.flowId, completionCode: approved.completionCode! }));
      const unconfirmedMove = await failure(firstValueFrom(admin.updateMcpServer('crm', { ...crm, url: 'https://crm.example.com/mcp/v2', oauth: { confirmAuthorization: false } })));
      const moved = await firstValueFrom(admin.updateMcpServer('crm', { ...crm, url: 'https://crm.example.com/mcp/v2', oauth: { confirmAuthorization: true } }));

      expect(denied.code).toBe('assistant-mcp-authorization-failed');
      expect(cancelled.code).toBe('assistant-mcp-authorization-cancelled');
      expect(unconfirmedMove.fieldErrors).toEqual({ 'oauth.confirmAuthorization': ['required'] });
      expect(moved.organizationConnection?.status).toBe('not-connected');
      expect(moved.oauth?.hasClientSecret).toBeTrue();
    });

    it('lets each person connect, disconnect and consent to background use of a server people connect to themselves', async () => {
      const personal: McpServerInput = {
        ...crm,
        id: 'calendar',
        displayName: 'Calendar',
        url: 'https://calendar.example.com/mcp',
        connectionScope: 'user',
        headlessUse: 'personal',
        oauth: { clientId: 'assistant', scopes: 'calendar:read', confirmAuthorization: true }
      };
      await firstValueFrom(admin.createMcpServer(personal));
      await firstValueFrom(admin.createAgent({ ...newAgent, mcpServerIds: ['calendar'] }));
      const [before] = await firstValueFrom(api.getMcpConnections());
      const untested = await firstValueFrom(admin.testMcpServer('calendar'));

      const start = await firstValueFrom(api.authorizeMcpConnection('calendar', { allowBackgroundUse: true }));
      const outcome = await firstValueFrom(backend.authorizationWindow.open().authorize(start));
      const completed = await firstValueFrom(api.completeMcpAuthorization({ flowId: start.flowId, completionCode: outcome.completionCode! }));
      const [connected] = await firstValueFrom(api.getMcpConnections());
      const summary = await firstValueFrom(admin.getMcpConnectionsSummary('calendar'));
      await firstValueFrom(api.disconnectMcpConnection('calendar'), { defaultValue: undefined });
      const [disconnected] = await firstValueFrom(api.getMcpConnections());

      expect(before).toEqual(jasmine.objectContaining({ connectionStatus: 'not-connected', offersBackgroundUse: true, allowBackgroundUse: false }));
      expect(untested.code).toBe('assistant-mcp-connection-required');
      expect(completed.ownerKind).toBe('user');
      expect(connected).toEqual(jasmine.objectContaining({ connectionStatus: 'active', grantedScopes: 'calendar:read', allowBackgroundUse: true }));
      expect(summary).toEqual({ active: 1, reauthorizationRequired: 0, issuerChanged: 0 });
      // The consent to background use stays until the person withdraws it.
      expect(disconnected).toEqual(jasmine.objectContaining({ connectionStatus: 'not-connected', allowBackgroundUse: true }));
    });

    it('refuses consent for a server that is not offered in background runs and revokes everyone on request', async () => {
      await firstValueFrom(admin.createMcpServer({ ...crm, id: 'mail', displayName: 'Mail', url: 'https://mail.example.com/mcp', connectionScope: 'user', oauth: { clientId: 'assistant', confirmAuthorization: true } }));
      await firstValueFrom(admin.createAgent({ ...newAgent, mcpServerIds: ['mail'] }));
      const refused = await failure(firstValueFrom(api.updateMcpConnection('mail', { allowBackgroundUse: true })));
      const start = await firstValueFrom(admin.authorizeMcpOrganizationConnection('mail'));
      const outcome = await firstValueFrom(backend.authorizationWindow.open().authorize(start));
      await firstValueFrom(admin.completeMcpAuthorization({ flowId: start.flowId, completionCode: outcome.completionCode! }));
      const tested = await firstValueFrom(admin.testMcpServer('mail'));
      await firstValueFrom(admin.revokeMcpConnections('mail'), { defaultValue: undefined });
      const [revoked] = await firstValueFrom(api.getMcpConnections());

      expect(refused.code).toBe('assistant-mcp-headless-not-allowed');
      expect(tested.ok).toBeTrue();
      expect(revoked.connectionStatus).toBe('not-connected');
    });

    it('connects client credentials on first use and requires a client id and secret', async () => {
      const input: McpServerInput = {
        id: 'erp',
        displayName: 'ERP',
        url: 'https://erp.example.com/mcp',
        authMode: 'oauth-client-credentials',
        headerName: null,
        requiredPolicy: null,
        isEnabled: true,
        oauth: { clientId: 'assistant-erp', clientSecret: 'test-erp-secret', confirmAuthorization: true }
      };
      const withoutClient = await failure(firstValueFrom(admin.createMcpServer({ ...input, oauth: { ...input.oauth, clientId: '' } })));
      const withoutSecret = await failure(firstValueFrom(admin.createMcpServer({ ...input, oauth: { ...input.oauth, clientSecret: null } })));
      const created = await firstValueFrom(admin.createMcpServer(input));
      const tested = await firstValueFrom(admin.testMcpServer('erp'));
      const [listed] = (await firstValueFrom(admin.getMcpServers())).filter(server => server.id === 'erp');

      expect(withoutClient.fieldErrors).toEqual({ 'oauth.clientId': ['required'] });
      expect(withoutSecret.fieldErrors).toEqual({ 'oauth.clientSecret': ['required'] });
      expect(created.connectionScope).toBe('organization');
      expect(tested.ok).toBeTrue();
      expect(listed.organizationConnection).toEqual(jasmine.objectContaining({ status: 'active', connectedBy: 'client-credentials' }));
    });
  });
});
