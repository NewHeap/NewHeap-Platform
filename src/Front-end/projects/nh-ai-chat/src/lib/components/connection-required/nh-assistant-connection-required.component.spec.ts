import { OverlayContainer } from '@angular/cdk/overlay';
import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { NhAssistantConfig } from '../../nh-assistant.config';
import { provideNhAssistant } from '../../provide-nh-assistant';
import { NhAssistantApiService } from '../../services/nh-assistant-api.service';
import { NhAssistantMcpConnectService } from '../../services/nh-assistant-mcp-connect.service';
import { NhAssistantPanelService } from '../../services/nh-assistant-panel.service';
import { NhAssistantStore } from '../../services/nh-assistant.store';
import { NhAssistantPanelComponent } from '../panel/nh-assistant-panel.component';
import { NhAssistantMcpConnectionsComponent } from '../preferences/nh-assistant-mcp-connections.component';

function flush(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve));
}

function scenario(headlessUse: 'never' | 'personal' = 'never'): NhAssistantMockScenario {
  return {
    agents: [{ id: 'projects', version: 1, displayNameKey: 'agents.projects', descriptionKey: 'agents.projects', canMutate: true }],
    timing: { firstEventDelayMs: 0, eventDelayMs: 0 },
    turns: [{
      match: 'calendar',
      steps: [
        { connectionRequired: { serverId: 'calendar', text: 'Connect your calendar first.' } },
        { text: 'You are free on Friday.' }
      ]
    }],
    admin: {
      agents: [{
        source: 'code', id: 'projects', displayName: 'Projects', description: '', instructions: '', toolSelectors: [],
        mcpServerIds: ['calendar'], requiredPolicy: null, autonomy: 'execute'
      }],
      mcpServers: [{
        id: 'calendar', displayName: 'Team calendar', url: 'https://calendar.example.com/mcp', authMode: 'oauth', headerName: null,
        requiredPolicy: null, isEnabled: true, connectionScope: 'user', headlessUse,
        oauth: { clientId: 'assistant', scopes: 'calendar:read' }
      }]
    }
  };
}

async function configure(script = scenario(), config: Partial<NhAssistantConfig> = {}): Promise<void> {
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      provideTranslateService({ fallbackLang: 'en' }),
      provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token', ...config }),
      provideNhAssistantMockApi(script)
    ]
  });
  await firstValueFrom(TestBed.inject(TranslateService).use('en'));
}

async function until(fixture: ComponentFixture<unknown>, condition: () => boolean): Promise<void> {
  for (let attempt = 0; attempt < 40 && !condition(); attempt++) {
    await flush();
    fixture.detectChanges();
  }
  expect(condition()).withContext('condition').toBeTrue();
}

describe('NhAssistantConnectionRequiredComponent in the chat', () => {
  @Component({ standalone: true, imports: [NhAssistantPanelComponent], template: '<nh-assistant-panel />' })
  class HostComponent {}

  it('shows the card of an answer without the person\'s connection, connects and asks again', async () => {
    await configure();
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    TestBed.inject(NhAssistantPanelService).open();
    const store = TestBed.inject(NhAssistantStore);
    await store.initialize();
    await store.send('Am I free in my calendar on Friday?');
    const overlay = TestBed.inject(OverlayContainer).getContainerElement();
    await until(fixture, () => overlay.querySelector('nh-assistant-connection-required') !== null && store.activeConversation()?.status === 'idle');

    const card = overlay.querySelector('nh-assistant-connection-required') as HTMLElement;
    expect(card.querySelector('[role="group"]')?.getAttribute('aria-labelledby')).toBeTruthy();
    expect(card.textContent).toContain('Connect Team calendar');
    expect(card.textContent).toContain('your account is not connected');
    expect(overlay.textContent).toContain('Connect your calendar first.');
    expect(overlay.textContent).not.toContain('You are free on Friday.');

    (Array.from(card.querySelectorAll('button')).find(button => button.textContent?.trim() === 'Connect') as HTMLButtonElement).click();
    await until(fixture, () => card.textContent?.includes('Team calendar is connected.') === true);
    (Array.from(card.querySelectorAll('button')).find(button => button.textContent?.trim() === 'Ask again') as HTMLButtonElement).click();
    await until(fixture, () => overlay.textContent?.includes('You are free on Friday.') === true);

    const backend = TestBed.inject(NhAssistantMockBackend);
    expect(backend.requests.filter(request => request.method === 'POST' && request.path === 'mcp-connections/calendar/authorize').length).toBe(1);
    expect(backend.requests.filter(request => request.method === 'POST' && request.path.endsWith('/messages')).map(request => (request.body as { text: string }).text))
      .toEqual(['Am I free in my calendar on Friday?', 'Am I free in my calendar on Friday?']);
  });

  it('reports a denied authorization on the card', async () => {
    const script = scenario();
    script.admin!.mcpAuthorization = 'deny';
    await configure(script);
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    TestBed.inject(NhAssistantPanelService).open();
    const store = TestBed.inject(NhAssistantStore);
    await store.initialize();
    await store.send('Check my calendar.');
    const overlay = TestBed.inject(OverlayContainer).getContainerElement();
    await until(fixture, () => overlay.querySelector('nh-assistant-connection-required') !== null);
    const card = overlay.querySelector('nh-assistant-connection-required') as HTMLElement;

    (card.querySelector('button') as HTMLButtonElement).click();
    await until(fixture, () => card.querySelector('[role="alert"]') !== null);

    expect(card.querySelector('[role="alert"]')?.textContent).toContain('The authorization was not given.');
    expect(Array.from(card.querySelectorAll('button')).some(button => button.textContent?.trim() === 'Connect')).toBeTrue();
  });
});

describe('NhAssistantMcpConnectionsComponent for OAuth servers', () => {
  it('connects, offers background use only where the administrator does and disconnects after confirmation', async () => {
    await configure(scenario('personal'));
    const fixture = TestBed.createComponent(NhAssistantMcpConnectionsComponent);
    const element = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
    await until(fixture, () => element.querySelector('fieldset') !== null);

    expect(element.querySelector('legend')?.textContent?.trim()).toBe('Connected services');
    expect(element.textContent).toContain('Not connected');
    expect(element.querySelector('input[type="checkbox"]')).not.toBeNull();
    (Array.from(element.querySelectorAll('button')).find(button => button.textContent?.trim() === 'Connect') as HTMLButtonElement).click();
    await until(fixture, () => element.textContent?.includes('Connected since') === true);
    expect(element.textContent).toContain('Team calendar is connected.');

    (Array.from(element.querySelectorAll('button')).find(button => button.textContent?.trim() === 'Disconnect') as HTMLButtonElement).click();
    fixture.detectChanges();
    (Array.from(element.querySelectorAll('button')).find(button => button.textContent?.trim() === 'Disconnect and revoke') as HTMLButtonElement).click();
    await until(fixture, () => element.textContent?.includes('is disconnected and its access revoked') === true);
    expect(TestBed.inject(NhAssistantMockBackend).requests.some(request => request.method === 'DELETE' && request.path === 'mcp-connections/calendar')).toBeTrue();
  });

  it('shows no consent switch for a server that is not offered in background runs', async () => {
    await configure(scenario('never'));
    const fixture = TestBed.createComponent(NhAssistantMcpConnectionsComponent);
    const element = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
    await until(fixture, () => element.querySelector('fieldset') !== null);

    expect(element.querySelector('input[type="checkbox"]')).toBeNull();
    expect(element.textContent).not.toContain('Not allowed');
  });
});

describe('NhAssistantMcpConnectService in redirect mode', () => {
  it('sends the page to the authorization server and completes the connection from the returned fragment', async () => {
    await configure(scenario(), { mcpConnectMode: 'redirect', mcpConnectReturnUrl: () => 'https://app.example/settings' });
    const connector = TestBed.inject(NhAssistantMcpConnectService);
    const api = TestBed.inject(NhAssistantApiService);
    const assigned: string[] = [];
    spyOn(connector, 'navigate').and.callFake((url: string) => assigned.push(url));
    const backend = TestBed.inject(NhAssistantMockBackend);

    const pending = connector.connect('calendar').subscribe();
    for (let attempt = 0; attempt < 10 && assigned.length === 0; attempt++) {
      await flush();
    }
    pending.unsubscribe();
    const started = backend.requests.find(request => request.path === 'mcp-connections/calendar/authorize')!;
    expect((started.body as { returnUrl: string }).returnUrl).toBe('https://app.example/settings');
    expect(assigned[0]).toContain('https://auth.calendar.example.com/authorize?');

    // The authorization server and the callback return to the page with the code in the fragment.
    const flowId = new URL(assigned[0]).searchParams.get('state')!;
    const outcome = backend.authorizationWindow.open();
    const returned = await firstValueFrom(outcome.authorize({ flowId, authorizationUrl: assigned[0], expiresAt: '', callbackOrigin: '' }));
    const original = location.href;
    history.replaceState(history.state, '', `${location.pathname}${location.search}#nh-mcp-flow=${flowId}&nh-mcp-code=${returned.completionCode}`);
    try {
      const completed = await firstValueFrom(connector.completeReturn());
      expect(completed?.completion?.status).toBe('active');
      expect(location.hash).toBe('');
      expect(connector.returned()?.completion?.serverId).toBe('calendar');
      expect((await firstValueFrom(api.getMcpConnections()))[0].connectionStatus).toBe('active');
    } finally {
      history.replaceState(history.state, '', original);
    }
  });
});
