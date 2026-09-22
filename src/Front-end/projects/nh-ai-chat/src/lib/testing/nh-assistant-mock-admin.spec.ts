import { TestBed } from '@angular/core/testing';
import {
  AdminAgentInput,
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
    ]
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

  it('lists admin agents in the chat with their literal names and hides disabled agents', async () => {
    await firstValueFrom(admin.createAgent(newAgent));
    const code = (await firstValueFrom(admin.getAgents())).find(agent => agent.id === 'projects')!;
    await firstValueFrom(admin.updateAgent('projects', { ...code, isEnabled: false, expectedVersion: code.version }));

    const agents = (await firstValueFrom(api.status())).agents;

    expect(agents.map(agent => agent.id)).toEqual(['support-agent']);
    expect(agents[0].displayNameKey).toBe('Support agent');
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

  it('never returns a secret and keeps, replaces or clears it on update', async () => {
    const [server] = await firstValueFrom(admin.getMcpServers());
    const { hasSecret, lastSyncAt, lastSyncStatus, assignedAgentIds, ...input } = server;

    expect(JSON.stringify(backend.requests)).not.toContain('top-secret-value');
    expect(hasSecret).toBeTrue();
    expect(JSON.stringify(server)).not.toContain('top-secret-value');

    const kept = await firstValueFrom(admin.updateMcpServer('files', { ...input, displayName: 'Files 2' }));
    const cleared = await firstValueFrom(admin.updateMcpServer('files', { ...input, secret: '' }));
    const replaced = await firstValueFrom(admin.updateMcpServer('files', { ...input, secret: 'new-value' }));

    expect(kept.hasSecret).toBeTrue();
    expect(cleared.hasSecret).toBeFalse();
    expect(replaced.hasSecret).toBeTrue();
    expect(JSON.stringify(replaced)).not.toContain('new-value');
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
    await firstValueFrom(admin.createMcpServer({ ...base, id: 'keyless', url: 'https://keys.example.com/mcp', authMode: 'api-key' }));
    await firstValueFrom(admin.createMcpServer({ ...base, id: 'planning', url: 'https://planning.example.com/mcp', authMode: 'forward-user-token' }));

    const results = await Promise.all(['blocked', 'down', 'forward', 'keyless', 'planning'].map(id => firstValueFrom(admin.testMcpServer(id))));
    const syncFailure = await failure(firstValueFrom(admin.syncMcpServer('down')));

    expect(results.map(result => result.code)).toEqual([
      'assistant-mcp-host-blocked', 'assistant-mcp-unreachable', 'assistant-mcp-host-blocked', 'assistant-mcp-unauthorized', null
    ]);
    const blockedSync = await failure(firstValueFrom(admin.syncMcpServer('blocked')));

    expect(syncFailure).toEqual(jasmine.objectContaining({ status: 502, code: 'assistant-mcp-unreachable' }));
    expect(blockedSync).toEqual(jasmine.objectContaining({ status: 400, code: 'assistant-mcp-host-blocked' }));
    expect((await firstValueFrom(admin.getMcpServers())).find(server => server.id === 'down')?.lastSyncStatus).toBe('failed');
    expect((await firstValueFrom(admin.getMcpServers())).find(server => server.id === 'keyless')?.headerName).toBe('X-Api-Key');
  });

  it('answers with the specific contract codes for duplicates, missing items and resets', async () => {
    await firstValueFrom(admin.createAgent(newAgent));
    const [server] = await firstValueFrom(admin.getMcpServers());
    const { hasSecret, lastSyncAt, lastSyncStatus, assignedAgentIds, ...serverInput } = server;

    const failures = await Promise.all([
      failure(firstValueFrom(admin.createAgent(newAgent))),
      failure(firstValueFrom(admin.createMcpServer(serverInput))),
      failure(firstValueFrom(admin.resetAgent(newAgent.id))),
      failure(firstValueFrom(admin.getMcpTools('unknown'))),
      failure(firstValueFrom(admin.updateMcpTool('files', 'unknown', { isEnabled: false, effect: 'mutation', descriptionOverride: null }))),
      failure(firstValueFrom(admin.resetAgent('unknown')))
    ]);

    expect(failures.map(item => [item.status, item.code])).toEqual([
      [409, 'assistant-agent-exists'],
      [409, 'assistant-mcp-server-exists'],
      [409, 'assistant-agent-not-code'],
      [404, 'assistant-mcp-server-not-found'],
      [404, 'assistant-mcp-tool-not-found'],
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
});
