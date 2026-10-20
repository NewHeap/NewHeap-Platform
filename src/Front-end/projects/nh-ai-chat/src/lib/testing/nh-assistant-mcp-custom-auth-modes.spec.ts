import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAdminMcpServersComponent } from '../../../admin/src/public-api';

/**
 * Authentication modes an application registers in code (follow-up F4 of the MCP connections plan):
 * the administration lists them next to the built-in modes, a server may use them, and background use
 * follows whose credential the mode sends.
 */
function scenario(): NhAssistantMockScenario {
  return {
    agents: [{ id: 'projects', version: 1, displayNameKey: 'agents.projects', descriptionKey: 'agents.projects', canMutate: true }],
    turns: [],
    admin: {
      agents: [{
        source: 'code', id: 'projects', displayName: 'Project assistant', description: 'Projects', instructions: 'Code.',
        toolSelectors: [], mcpServerIds: [], requiredPolicy: null, autonomy: 'execute', isEnabled: true
      }],
      mcpAuthModes: [
        { mode: 'token-exchange', ownership: 'person' },
        { mode: 'signed-request', ownership: 'organization' }
      ],
      mcpServers: [{
        id: 'planning', displayName: 'Planning', url: 'https://planning.example.com/mcp', authMode: 'token-exchange', headerName: null,
        requiredPolicy: null, isEnabled: true, remoteTools: []
      }]
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

async function call(method: string, path: string, body?: unknown): Promise<{ status: number; body: any }> {
  const response = await TestBed.inject(NhAssistantMockBackend).fetch(`/api/assistant/${path}`, {
    method,
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  const text = await response.text();
  return { status: response.status, body: text ? JSON.parse(text) : null };
}

function button(element: HTMLElement, text: string): HTMLButtonElement {
  const match = Array.from(element.querySelectorAll('button')).find(item => item.textContent?.trim() === text);
  if (!match) {
    throw new Error(`Button "${text}" not found.`);
  }
  return match;
}

function server(authMode: string, headlessUse: string): unknown {
  return {
    id: 'reports', displayName: 'Reports', url: 'https://reports.example.com/mcp', authMode, headerName: null,
    requiredPolicy: null, isEnabled: true, headlessUse
  };
}

describe('Authentication modes of the application', () => {
  it('are listed by the mock API and fit the background use of whose credential they send', async () => {
    await configure();

    expect((await call('GET', 'admin/mcp-auth-modes')).body).toEqual([
      { mode: 'signed-request', ownership: 'organization' },
      { mode: 'token-exchange', ownership: 'person' }
    ]);
    expect((await call('POST', 'admin/mcp-servers', server('signed-request', 'organization'))).status).toBe(201);
    const personal = await call('POST', 'admin/mcp-servers', { ...(server('token-exchange', 'organization') as object), id: 'calendar' });
    expect(personal.status).toBe(400);
    expect(personal.body.errors).toEqual(jasmine.objectContaining({ headlessUse: ['invalid'] }));
    expect((await call('POST', 'admin/mcp-servers', { ...(server('token-exchange', 'personal') as object), id: 'calendar' })).status).toBe(201);
    const unknown = await call('POST', 'admin/mcp-servers', { ...(server('magic', 'never') as object), id: 'magic' });
    expect(unknown.body.errors).toEqual(jasmine.objectContaining({ authMode: ['invalid'] }));
  });

  it('are offered in the server editor with what they need, at 375px without sideways scrolling', async () => {
    await configure();
    const fixture = TestBed.createComponent(NhAssistantAdminMcpServersComponent);
    const host = fixture.nativeElement as HTMLElement;
    host.style.display = 'block';
    host.style.width = '375px';
    let element = await settle(fixture);
    // The list names the application's mode by its name when it has no text of its own.
    expect(element.textContent).toContain('token-exchange');

    button(element, 'Edit').click();
    element = await settle(fixture);
    const auth = element.querySelector('select[id$="-auth"]') as HTMLSelectElement;
    expect(Array.from(auth.options).map(option => option.value)).toEqual([
      'none', 'bearer', 'api-key', 'basic', 'forward-user-token', 'oauth', 'oauth-client-credentials', 'signed-request', 'token-exchange'
    ]);
    expect(auth.value).toBe('token-exchange');
    expect(element.querySelector('[data-custom-auth-mode]')?.textContent).toContain('each person\'s own credential');
    // A person's credential serves background runs only with the person's consent.
    const headless = element.querySelector('select[id$="-headless"]') as HTMLSelectElement;
    expect(Array.from(headless.options).map(option => option.value)).toEqual(['never', 'personal']);

    auth.value = 'signed-request';
    auth.dispatchEvent(new Event('change'));
    element = await settle(fixture);
    expect(element.querySelector('[data-custom-auth-mode]')?.textContent).toContain('every caller shares it');
    const organization = element.querySelector('select[id$="-headless"]') as HTMLSelectElement;
    expect(Array.from(organization.options).map(option => option.value)).toEqual(['never', 'organization']);
    // The application supplies the credential: no secret field.
    expect(element.querySelector('input[type="password"]')).toBeNull();
    expect(host.scrollWidth).toBeLessThanOrEqual(host.clientWidth + 1);
  });
});
