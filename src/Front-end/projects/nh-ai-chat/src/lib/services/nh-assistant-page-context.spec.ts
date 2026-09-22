import { OverlayContainer } from '@angular/cdk/overlay';
import { Component, InjectionToken, inject } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockBackend, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { NhAssistantPanelComponent } from '../components/panel/nh-assistant-panel.component';
import { ClientContext } from '../models/assistant-api.models';
import { NhAssistantConfig } from '../nh-assistant.config';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantPanelService } from './nh-assistant-panel.service';
import { NhAssistantStore } from './nh-assistant.store';
import { describeNhAssistantClientContext, normalizeNhAssistantClientContext } from './nh-assistant-page-context';

const PAGE = new InjectionToken<ClientContext | null>('PAGE');

describe('normalizeNhAssistantClientContext', () => {
  it('keeps a valid context unchanged', () => {
    const context: ClientContext = {
      route: '/projects/AA09027',
      title: 'Project AA09027',
      entities: [{ type: 'project', id: 'AA09027', label: 'Project AA09027' }]
    };

    expect(normalizeNhAssistantClientContext(context)).toEqual(context);
  });

  it('truncates route, title and labels to the contract limits', () => {
    const result = normalizeNhAssistantClientContext({
      route: '/' + 'r'.repeat(300),
      title: 't'.repeat(200),
      entities: [{ type: 'project', id: 'p1', label: 'l'.repeat(200) }]
    })!;

    expect(result.route.length).toBe(200);
    expect(result.title!.length).toBe(120);
    expect(result.entities![0].label!.length).toBe(120);
  });

  it('keeps at most five entities and drops invalid ones instead of truncating their ids', () => {
    const result = normalizeNhAssistantClientContext({
      route: '/x',
      entities: [
        { type: 'Not Dash', id: '1' },
        { type: 'project', id: '' },
        { type: 'project', id: 'x'.repeat(65) },
        'text',
        ...Array.from({ length: 7 }, (_, index) => ({ type: 'order-group', id: `g${index}` }))
      ]
    })!;

    expect(result.entities!.map(entity => entity.id)).toEqual(['g0', 'g1', 'g2', 'g3', 'g4']);
  });

  it('returns null for an invalid shape', () => {
    for (const value of [null, 'route', [], { title: 'No route' }, { route: '   ' }, { route: 42 }]) {
      expect(normalizeNhAssistantClientContext(value)).withContext(JSON.stringify(value)).toBeNull();
    }
  });

  it('describes a context by its first entity, title or route', () => {
    expect(describeNhAssistantClientContext({ route: '/a', entities: [{ type: 'project', id: 'AA1', label: 'Project AA1' }] })).toBe('Project AA1');
    expect(describeNhAssistantClientContext({ route: '/a', entities: [{ type: 'project', id: 'AA1' }] })).toBe('project AA1');
    expect(describeNhAssistantClientContext({ route: '/a', title: 'Dashboard' })).toBe('Dashboard');
    expect(describeNhAssistantClientContext({ route: '/a' })).toBe('/a');
  });
});

describe('page context of a message', () => {
  let page: ClientContext | null;

  async function setup(getPageContext?: NhAssistantConfig['getPageContext']): Promise<NhAssistantStore> {
    TestBed.configureTestingModule({
      providers: [
        provideTranslateService({ fallbackLang: 'en' }),
        { provide: PAGE, useFactory: () => page },
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token', getPageContext }),
        provideNhAssistantMockApi({
          agents: [{ id: 'projects', version: 1, displayNameKey: 'agents.projects', descriptionKey: 'agents.projects', canMutate: false }],
          timing: { firstEventDelayMs: 0, eventDelayMs: 0 },
          turns: [{ steps: [{ text: context => context ? `You are looking at ${describeNhAssistantClientContext(context)}.` : 'No page.' }] }]
        })
      ]
    });
    await firstValueFrom(TestBed.inject(TranslateService).use('en'));
    const store = TestBed.inject(NhAssistantStore);
    await store.initialize();
    return store;
  }

  function sentBodies(): Record<string, unknown>[] {
    return TestBed.inject(NhAssistantMockBackend).requests
      .filter(request => request.path.endsWith('/messages'))
      .map(request => request.body as Record<string, unknown>);
  }

  async function sendAndWait(store: NhAssistantStore, text: string): Promise<void> {
    await store.send(text);
    for (let attempt = 0; attempt < 50 && store.streaming(); attempt++) {
      await new Promise(resolve => setTimeout(resolve, 5));
    }
  }

  beforeEach(() => {
    page = { route: '/projects/AA09027', entities: [{ type: 'project', id: 'AA09027', label: 'Project AA09027' }] };
  });

  it('omits the field when the host has no getter', async () => {
    const store = await setup();

    await sendAndWait(store, 'Hello');

    expect(Object.keys(sentBodies()[0])).not.toContain('clientContext');
  });

  it('sends the getter result, read in the assistant injection context, and the mock can reflect it', async () => {
    const store = await setup(() => inject(PAGE));

    await sendAndWait(store, 'What am I looking at?');

    expect(sentBodies()[0]['clientContext']).toEqual(page);
    const answer = store.activeConversation()!.messages[1].parts[0];
    expect(answer).toEqual({ type: 'text', text: 'You are looking at Project AA09027.' });
  });

  it('truncates a too long field before sending', async () => {
    page = { route: '/' + 'x'.repeat(250) };
    const store = await setup(() => inject(PAGE));

    await sendAndWait(store, 'Hello');

    expect((sentBodies()[0]['clientContext'] as ClientContext).route.length).toBe(200);
  });

  it('still sends when the getter throws, without the field', async () => {
    const store = await setup(() => {
      throw new Error('Page not ready');
    });

    await sendAndWait(store, 'Hello');

    expect(sentBodies().length).toBe(1);
    expect(Object.keys(sentBodies()[0])).not.toContain('clientContext');
    expect(store.error()).toBeNull();
  });

  it('still sends when the getter rejects, without the field', async () => {
    const store = await setup(() => Promise.reject(new Error('Unavailable')));
    await sendAndWait(store, 'First');

    expect(Object.keys(sentBodies()[0])).not.toContain('clientContext');
  });

  it('sends null for the next message only when the user leaves the context out', async () => {
    const store = await setup(() => inject(PAGE));
    await store.refreshPageContext();
    expect(store.pageContext()).toEqual(page);

    store.setPageContextExcluded(true);
    await sendAndWait(store, 'First');
    await sendAndWait(store, 'Second');

    expect(sentBodies().map(body => body['clientContext'])).toEqual([null, page]);
    expect(store.pageContextExcluded()).toBeFalse();
  });
});

describe('page context chip', () => {
  @Component({ standalone: true, imports: [NhAssistantPanelComponent], template: '<nh-assistant-panel />' })
  class HostComponent {}

  it('shows what is sent and leaves it out of the next message with the close button', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideTranslateService({ fallbackLang: 'en' }),
        provideNhAssistant({
          apiBaseUrl: '/api/assistant',
          getAccessToken: () => 'token',
          getPageContext: () => ({ route: '/projects/AA09027', entities: [{ type: 'project', id: 'AA09027', label: 'Project AA09027' }] })
        }),
        provideNhAssistantMockApi({
          agents: [{ id: 'projects', version: 1, displayNameKey: 'agents.projects', descriptionKey: 'agents.projects', canMutate: false }],
          timing: { firstEventDelayMs: 0, eventDelayMs: 0 },
          turns: [{ steps: [{ text: 'OK' }] }]
        })
      ]
    });
    await firstValueFrom(TestBed.inject(TranslateService).use('en'));
    const fixture: ComponentFixture<HostComponent> = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    TestBed.inject(NhAssistantPanelService).open();
    for (let round = 0; round < 5; round++) {
      await new Promise(resolve => setTimeout(resolve));
      fixture.detectChanges();
    }
    const overlay = TestBed.inject(OverlayContainer).getContainerElement();

    const chip = overlay.querySelector('.context-chip') as HTMLElement;
    expect(chip.textContent).toContain('Sent with your message: Project AA09027');
    const button = chip.querySelector('button') as HTMLButtonElement;
    expect(button.getAttribute('aria-label')).toBe('Do not send Project AA09027 with this message');

    button.click();
    fixture.detectChanges();
    expect(chip.textContent).toContain('Not sent with this message: Project AA09027');
    expect(button.getAttribute('aria-pressed')).toBe('true');

    const store = TestBed.inject(NhAssistantStore);
    await store.send('Hello');
    const body = TestBed.inject(NhAssistantMockBackend).requests.find(request => request.path.endsWith('/messages'))!.body as Record<string, unknown>;
    expect(body['clientContext']).toBeNull();
  });
});
