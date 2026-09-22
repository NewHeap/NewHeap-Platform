import { Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAdminApiService } from '../services/nh-assistant-admin-api.service';
import { NhAssistantAgentPickerComponent } from '../components/agent-picker/nh-assistant-agent-picker.component';
import {
  NhAssistantAdminAgentsComponent,
  NhAssistantAdminComponent,
  NhAssistantAdminContextComponent,
  NhAssistantAdminMcpServersComponent
} from '../../../admin/src/public-api';

const secretValue = 'do-not-show-this-secret';

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

function requestBodies(method: string, path: string): unknown[] {
  return TestBed.inject(NhAssistantMockBackend).requests
    .filter(item => item.method === method && item.path === path)
    .map(item => item.body);
}

describe('NhAssistantAdminComponent', () => {
  it('shows the tabs as an ARIA tablist that follows the arrow keys', async () => {
    await configure();
    const { fixture, element } = await render(NhAssistantAdminComponent);

    const tabs = () => Array.from(element.querySelectorAll('[role="tab"]')) as HTMLElement[];
    expect(tabs().map(tab => tab.textContent?.trim())).toEqual(['Context', 'Agents', 'MCP servers']);
    expect(tabs()[0].getAttribute('aria-selected')).toBe('true');
    expect(element.querySelector('[role="tabpanel"] nh-assistant-admin-context')).not.toBeNull();

    element.querySelector('[role="tablist"]')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
    await settle(fixture);

    expect(tabs()[1].getAttribute('aria-selected')).toBe('true');
    expect(tabs()[0].getAttribute('tabindex')).toBe('-1');
    expect(element.querySelector('[role="tabpanel"] nh-assistant-admin-agents')).not.toBeNull();
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
  it('lists code agents without a delete action', async () => {
    await configure();
    const { element } = await render(NhAssistantAdminAgentsComponent);

    const card = element.querySelector('.card') as HTMLElement;
    expect(card.textContent).toContain('Project assistant');
    expect(card.textContent).toContain('Code');
    expect(buttons(card, 'Delete').length).toBe(0);
  });

  it('creates an agent with catalog selectors and an MCP server', async () => {
    await configure();
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);

    button(element, 'New agent').click();
    await settle(fixture);
    type(element, 'input[id$="-id"]', 'support-agent');
    type(element, 'input[id$="-name"]', 'Support agent');
    type(element, 'textarea', 'Help with support questions.');
    const catalog = Array.from(element.querySelectorAll('.catalog input[type="checkbox"]')) as HTMLInputElement[];
    catalog[0].click();
    const server = Array.from(element.querySelectorAll('fieldset .check input[type="checkbox"]')).pop() as HTMLInputElement;
    server.click();
    fixture.detectChanges();

    expect(element.textContent).toContain('1 catalog tools match the selectors.');

    button(element, 'Save').click();
    await settle(fixture);

    expect(requestBodies('POST', 'admin/agents')).toEqual([jasmine.objectContaining({
      id: 'support-agent',
      displayName: 'Support agent',
      instructions: 'Help with support questions.',
      toolSelectors: ['sample-api.project.list'],
      mcpServerIds: ['files'],
      autonomy: 'explain',
      isEnabled: true
    })]);
    expect(element.textContent).toContain('Support agent');
    expect(element.textContent).toContain('Created here');
  });

  it('refuses an invalid id without calling the server', async () => {
    await configure();
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);

    button(element, 'New agent').click();
    await settle(fixture);
    type(element, 'input[id$="-id"]', 'Not Valid');
    type(element, 'input[id$="-name"]', 'Name');
    button(element, 'Save').click();
    await settle(fixture);

    expect(element.textContent).toContain('Use lowercase letters, digits and single dashes.');
    expect(requestBodies('POST', 'admin/agents')).toEqual([]);
  });

  it('overrides, disables and resets a code agent after confirmation', async () => {
    await configure();
    const admin = TestBed.inject(NhAssistantAdminApiService);
    const [agent] = await firstValueFrom(admin.getAgents());
    await firstValueFrom(admin.updateAgent(agent.id, { ...agent, instructions: 'Override.', expectedVersion: agent.version }));
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);

    expect(element.textContent).toContain('Overridden');
    button(element, 'Disable').click();
    await settle(fixture);
    expect(element.querySelector('.card')?.textContent).toContain('Disabled');

    button(element, 'Reset to code').click();
    fixture.detectChanges();
    button(element, 'Confirm reset').click();
    await settle(fixture);

    expect(element.textContent).not.toContain('Overridden');
    expect(requestBodies('POST', 'admin/agents/projects/reset').length).toBe(1);
  });

  it('deletes an admin agent only after confirmation', async () => {
    await configure();
    await firstValueFrom(TestBed.inject(NhAssistantAdminApiService).createAgent({
      id: 'temporary', displayName: 'Temporary', description: '', instructions: '', toolSelectors: [], mcpServerIds: [],
      requiredPolicy: null, autonomy: 'observe', isEnabled: true
    }));
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);

    button(element, 'Delete').click();
    fixture.detectChanges();
    expect(requestBodies('DELETE', 'admin/agents/temporary')).toEqual([]);

    button(element, 'Confirm delete').click();
    await settle(fixture);

    expect(requestBodies('DELETE', 'admin/agents/temporary').length).toBe(1);
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

  it('replaces or clears the secret explicitly', async () => {
    await configure();
    const { fixture, element } = await render(NhAssistantAdminMcpServersComponent);

    button(element, 'Edit').click();
    await settle(fixture);
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
    button(element, 'Clear').click();
    fixture.detectChanges();
    expect(element.textContent).toContain('The secret will be cleared when you save.');
    button(element, 'Save').click();
    await settle(fixture);

    const bodies = requestBodies('PUT', 'admin/mcp-servers/files') as { secret?: string }[];
    expect(bodies.map(body => body.secret)).toEqual(['new-secret', '']);
    expect(element.textContent).toContain('No secret');
  });

  it('tests the connection and shows the result', async () => {
    await configure();
    const { fixture, element } = await render(NhAssistantAdminMcpServersComponent);

    button(element, 'Test connection').click();
    await settle(fixture);

    expect(element.querySelector('[role="status"]')?.textContent).toContain('The connection works: 2 tools.');
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
