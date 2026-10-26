import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { AdminAgent, McpServer, NhAssistantApiError } from '@newheap/platform-ai-chat';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAdminApiService } from '../services/nh-assistant-admin-api.service';
import { NhAssistantAdminAgentEditorComponent, NhAssistantAdminMcpToolsComponent } from '../../../admin/src/public-api';

/**
 * FB9 and FB10 in the administration: the agent editor saves an existing agent without
 * `mcpServerIds` and then assigns or unassigns only the servers the administrator changed, and the
 * tools of a server list a tool reviewed before any sync as not seen yet.
 */
function scenario(): NhAssistantMockScenario {
  const server = (id: string, displayName: string) => ({
    id, displayName, url: `https://${id}.example.com/mcp`, authMode: 'none', headerName: null, requiredPolicy: null, isEnabled: true,
    remoteTools: [{ remoteName: 'readFile', description: 'Reads a file', inputSchemaHash: 'a1', readOnlyHint: true }]
  });
  return {
    agents: [{ id: 'projects', version: 1, displayNameKey: 'agents.projects', descriptionKey: 'agents.projects', canMutate: true }],
    turns: [],
    admin: {
      agents: [{
        source: 'code', id: 'projects', displayName: 'Project assistant', description: 'Projects', instructions: 'Code instructions.',
        toolSelectors: ['sample-api.project.*'], mcpServerIds: ['files'], requiredPolicy: null, autonomy: 'execute', isEnabled: true
      }],
      mcpServers: [server('files', 'Files'), server('calendar', 'Calendar'), server('planning', 'Planning')]
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

async function settle(fixture: ComponentFixture<unknown>): Promise<HTMLElement> {
  for (let round = 0; round < 6; round++) {
    fixture.detectChanges();
    await new Promise(resolve => setTimeout(resolve));
  }
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

function button(element: HTMLElement, text: string): HTMLButtonElement {
  const match = Array.from(element.querySelectorAll('button')).find(item => item.textContent?.trim() === text);
  if (!match) {
    throw new Error(`Button "${text}" not found.`);
  }
  return match;
}

/** The checkbox of an MCP server in the editor, found by the server's id next to its name. */
function serverBox(element: HTMLElement, serverId: string): HTMLInputElement {
  const label = Array.from(element.querySelectorAll('label.check'))
    .find(item => item.querySelector('.mono')?.textContent?.trim() === serverId);
  if (!label) {
    throw new Error(`Server "${serverId}" not found.`);
  }
  return label.querySelector('input') as HTMLInputElement;
}

/** The requests of the editor's save, as method and path, after the given number of earlier requests. */
function savedRequests(after: number): string[] {
  return TestBed.inject(NhAssistantMockBackend).requests
    .slice(after)
    .filter(item => item.method !== 'GET')
    .map(item => `${item.method} ${item.path}`);
}

function requestCount(): number {
  return TestBed.inject(NhAssistantMockBackend).requests.length;
}

async function agent(id = 'projects'): Promise<AdminAgent> {
  return (await firstValueFrom(TestBed.inject(NhAssistantAdminApiService).getAgents())).find(item => item.id === id)!;
}

async function servers(): Promise<McpServer[]> {
  return firstValueFrom(TestBed.inject(NhAssistantAdminApiService).getMcpServers());
}

async function openEditor(): Promise<{ fixture: ComponentFixture<NhAssistantAdminAgentEditorComponent>; element: HTMLElement; saved: AdminAgent[] }> {
  const fixture = TestBed.createComponent(NhAssistantAdminAgentEditorComponent);
  const saved: AdminAgent[] = [];
  fixture.componentInstance.saved.subscribe(item => saved.push(item));
  fixture.componentRef.setInput('agent', await agent());
  fixture.componentRef.setInput('servers', await servers());
  return { fixture, element: await settle(fixture), saved };
}

describe('NhAssistantAdminAgentEditorComponent and MCP assignments', () => {
  beforeEach(configure);

  it('saves an unchanged selection of servers without a list and without an assignment', async () => {
    const { fixture, element, saved } = await openEditor();
    const before = requestCount();

    (element.querySelector('textarea') as HTMLTextAreaElement).value = 'Answer about projects.';
    element.querySelector('textarea')!.dispatchEvent(new Event('input'));
    button(element, 'Save').click();
    await settle(fixture);

    expect(savedRequests(before)).toEqual(['PUT admin/agents/projects']);
    const body = TestBed.inject(NhAssistantMockBackend).requests.slice(before).find(item => item.method === 'PUT')!.body as Record<string, unknown>;
    expect(Object.keys(body)).not.toContain('mcpServerIds');
    expect(body['expectedVersion']).toBe(1);
    expect(saved.map(item => item.mcpServerIds)).toEqual([['files']]);
  });

  it('removes and then adds only the servers that changed, without a new agent version', async () => {
    const { fixture, element, saved } = await openEditor();
    const before = requestCount();

    serverBox(element, 'files').click();
    serverBox(element, 'calendar').click();
    fixture.detectChanges();
    button(element, 'Save').click();
    await settle(fixture);

    expect(savedRequests(before)).toEqual([
      'PUT admin/agents/projects',
      'DELETE admin/mcp-servers/files/agents/projects',
      'PUT admin/mcp-servers/calendar/agents/projects'
    ]);
    expect(saved.map(item => [item.version, item.mcpServerIds])).toEqual([[2, ['calendar']]]);
    const stored = await agent();
    expect([stored.version, stored.mcpServerIds]).toEqual([2, ['calendar']]);
  });

  it('keeps a server assigned after the editor opened', async () => {
    const { fixture, element, saved } = await openEditor();
    const admin = TestBed.inject(NhAssistantAdminApiService);

    // Another administrator assigns a server while this editor holds version 1.
    const planning = await firstValueFrom(admin.assignMcpServer('planning', 'projects'));
    expect(planning.assignedAgentIds).toEqual(['projects']);
    expect((await agent()).version).toBe(1);
    button(element, 'Save').click();
    await settle(fixture);

    expect(saved.map(item => [...item.mcpServerIds].sort())).toEqual([['files', 'planning']]);
    expect([...(await agent()).mcpServerIds].sort()).toEqual(['files', 'planning']);
  });

  it('keeps the saved agent and the changes that are left when an assignment fails, and the next save completes them', async () => {
    const { fixture, element, saved } = await openEditor();
    serverBox(element, 'calendar').click();
    serverBox(element, 'planning').click();
    fixture.detectChanges();
    // The calendar server is deleted while the editor is open.
    await firstValueFrom(TestBed.inject(NhAssistantAdminApiService).deleteMcpServer('calendar'), { defaultValue: undefined });
    const first = requestCount();

    button(element, 'Save').click();
    await settle(fixture);

    // The definition is saved; the first failed assignment stops the rest and the editor stays open.
    expect(savedRequests(first)).toEqual(['PUT admin/agents/projects', 'PUT admin/mcp-servers/calendar/agents/projects']);
    expect(saved).toEqual([]);
    expect(element.querySelector('[data-agent-editor-mcp-failed]')?.textContent)
      .toContain('The agent is saved, but not every change to its MCP servers went through.');
    expect(element.querySelector('.message.error')?.textContent).toContain('The MCP server no longer exists.');

    serverBox(element, 'calendar').click();
    fixture.detectChanges();
    const second = requestCount();
    button(element, 'Save').click();
    await settle(fixture);

    const update = TestBed.inject(NhAssistantMockBackend).requests.slice(second).find(item => item.method === 'PUT')!.body as Record<string, unknown>;
    expect(update['expectedVersion']).toBe(2);
    expect(savedRequests(second)).toEqual(['PUT admin/agents/projects', 'PUT admin/mcp-servers/planning/agents/projects']);
    expect(saved.map(item => [item.version, [...item.mcpServerIds].sort()])).toEqual([[3, ['files', 'planning']]]);
    expect(element.querySelector('[data-agent-editor-mcp-failed]')).toBeNull();
  });

  it('answers the assignment endpoints with the server and its agents, and with the service codes', async () => {
    const admin = TestBed.inject(NhAssistantAdminApiService);

    const assigned = await firstValueFrom(admin.assignMcpServer('calendar', 'projects'));
    const removed = await firstValueFrom(admin.unassignMcpServer('files', 'projects'));
    const unknown = await firstValueFrom(admin.assignMcpServer('calendar', 'unknown')).then(
      () => null,
      (error: NhAssistantApiError) => [error.status, error.code]);

    expect(assigned.assignedAgentIds).toEqual(['projects']);
    expect(removed.assignedAgentIds).toEqual([]);
    expect(unknown).toEqual([404, 'assistant-agent-not-found']);
    expect([(await agent()).version, (await agent()).mcpServerIds]).toEqual([1, ['calendar']]);
  });
});

describe('NhAssistantAdminMcpToolsComponent and tools that no sync has seen', () => {
  beforeEach(configure);

  it('lists a tool reviewed before any sync as not seen yet, and the first sync keeps its settings', async () => {
    const admin = TestBed.inject(NhAssistantAdminApiService);
    const reviewed = await firstValueFrom(admin.updateMcpTool('files', 'readFile', { isEnabled: true, effect: 'read-only', descriptionOverride: 'Read one file' }));
    expect([reviewed.status, reviewed.awaitingSync, reviewed.isEnabled]).toEqual(['missing', true, true]);

    const fixture = TestBed.createComponent(NhAssistantAdminMcpToolsComponent);
    fixture.componentRef.setInput('server', (await servers()).find(server => server.id === 'files')!);
    const element = await settle(fixture);
    const card = element.querySelector('.card') as HTMLElement;

    expect(card.querySelector('.badge')?.textContent?.trim()).toBe('Not seen yet');
    expect(card.querySelector('[data-tool-awaiting-sync]')?.textContent).toContain('No sync has seen this tool yet.');
    expect(card.textContent).not.toContain('Server description');
    // Its settings change here while it waits.
    expect((card.querySelector('input[type="checkbox"]') as HTMLInputElement).disabled).toBeFalse();

    button(element, 'Sync tools').click();
    await settle(fixture);

    const synced = element.querySelector('.card') as HTMLElement;
    expect(synced.querySelector('.badge')?.textContent?.trim()).toBe('Available');
    expect(synced.querySelector('[data-tool-awaiting-sync]')).toBeNull();
    const tool = (await firstValueFrom(admin.getMcpTools('files')))[0];
    expect([tool.status, tool.awaitingSync, tool.isEnabled, tool.effect, tool.descriptionOverride]).toEqual(['available', false, true, 'read-only', 'Read one file']);
  });
});
