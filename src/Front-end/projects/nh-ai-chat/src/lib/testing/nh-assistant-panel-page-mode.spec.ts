import { OverlayContainer } from '@angular/cdk/overlay';
import { Component, ErrorHandler, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import {
  NhAssistantBeforeSubmit,
  NhAssistantOutgoingMessage,
  NhAssistantPanelComponent,
  NhAssistantPanelConversationList
} from '../components/panel/nh-assistant-panel.component';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantPanelService } from '../services/nh-assistant-panel.service';
import { NhAssistantStore } from '../services/nh-assistant.store';

/**
 * The application layout with its drawer, and a chat page the person can open and leave. Both panels
 * carry the host's own content above the thread and above the composer, and the same `beforeSubmit`.
 */
@Component({
  standalone: true,
  imports: [NhAssistantPanelComponent],
  template: `
    @if (drawer()) {
      <nh-assistant-panel [beforeSubmit]="beforeSubmit()" [conversationList]="drawerList()">
        <div nhAssistantAboveThread class="drawer-topic">Drawer topic</div>
        <div nhAssistantAboveComposer class="drawer-choice">Drawer choice</div>
      </nh-assistant-panel>
    }
    @if (chatPage()) {
      <div class="chat-page" style="height: 640px">
        <nh-assistant-panel
          mode="page"
          [beforeSubmit]="beforeSubmit()"
          [conversationList]="pageList()"
          [(conversationsOpen)]="conversationsOpen">
          <div nhAssistantAboveThread class="page-topic">Topic: planning</div>
          <div nhAssistantAboveComposer class="page-choice">Continue or start apart</div>
        </nh-assistant-panel>
      </div>
    }
  `
})
class LayoutComponent {
  readonly drawer = signal(true);
  readonly chatPage = signal(false);
  readonly beforeSubmit = signal<NhAssistantBeforeSubmit | null>(null);
  readonly drawerList = signal<NhAssistantPanelConversationList>('panel');
  readonly pageList = signal<NhAssistantPanelConversationList>('panel');
  readonly conversationsOpen = signal(false);
}

/** A chat page without content of the host. */
@Component({
  standalone: true,
  imports: [NhAssistantPanelComponent],
  template: '<div class="chat-page" style="height: 640px"><nh-assistant-panel mode="page" /></div>'
})
class PlainChatPageComponent {}

function scenario(): NhAssistantMockScenario {
  const conversation = (id: string, title: string) => ({
    id,
    agentId: 'projects',
    agentVersion: 1,
    title,
    status: 'idle' as const,
    createdAt: '2026-10-29T09:00:00Z',
    updatedAt: '2026-10-29T09:00:00Z',
    messages: [{
      id: `${id}-m1`,
      role: 'user' as const,
      createdAt: '2026-10-29T09:00:00Z',
      parts: [{ type: 'text' as const, text: `About ${title}` }]
    }],
    pendingApproval: null
  });
  return {
    agents: [{ id: 'projects', version: 1, displayNameKey: 'agents.projects', descriptionKey: 'agents.projects', canMutate: false }],
    conversations: [conversation('c1', 'Weekly planning'), conversation('c2', 'Budget review')],
    turns: [{ steps: [{ text: 'Two projects are late.' }] }],
    timing: { firstEventDelayMs: 0, eventDelayMs: 0 }
  };
}

function flush(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve));
}

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  for (let round = 0; round < 6; round++) {
    fixture.detectChanges();
    await flush();
  }
  fixture.detectChanges();
}

/** Waits for a condition with a generous bound that only matters when the spec fails. */
async function until(fixture: ComponentFixture<unknown>, condition: () => boolean): Promise<void> {
  for (let round = 0; round < 200 && !condition(); round++) {
    await settle(fixture);
  }
  expect(condition()).withContext('the condition was not met in time').toBeTrue();
}

/** FB44 to FB49: the panel's page mode next to the layout's drawer. */
describe('NhAssistantPanelComponent page mode with the drawer (FB44-FB49)', () => {
  let fixture: ComponentFixture<LayoutComponent>;
  let layout: LayoutComponent;
  let panel: NhAssistantPanelService;
  let store: NhAssistantStore;
  let errorHandler: jasmine.SpyObj<ErrorHandler>;

  async function render(setup: (layout: LayoutComponent) => void = () => undefined): Promise<void> {
    const scope = `page-mode-${crypto.randomUUID()}`;
    errorHandler = jasmine.createSpyObj<ErrorHandler>('ErrorHandler', ['handleError']);
    TestBed.configureTestingModule({
      providers: [
        provideTranslateService({ fallbackLang: 'en' }),
        // A scope of its own, so no conversation of an earlier spec is restored.
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token', getStateScope: () => scope }),
        provideNhAssistantMockApi(scenario()),
        { provide: ErrorHandler, useValue: errorHandler }
      ]
    });
    await firstValueFrom(TestBed.inject(TranslateService).use('en'));
    fixture = TestBed.createComponent(LayoutComponent);
    layout = fixture.componentInstance;
    setup(layout);
    await settle(fixture);
    panel = TestBed.inject(NhAssistantPanelService);
    store = TestBed.inject(NhAssistantStore);
  }

  function overlay(): HTMLElement {
    return TestBed.inject(OverlayContainer).getContainerElement();
  }

  function page(): HTMLElement {
    return (fixture.nativeElement as HTMLElement).querySelector('.chat-page') as HTMLElement;
  }

  function pageComposer(): HTMLTextAreaElement {
    return page().querySelector<HTMLTextAreaElement>('nh-assistant-composer textarea')!;
  }

  async function sendFrom(textarea: HTMLTextAreaElement, text: string): Promise<void> {
    textarea.value = text;
    textarea.dispatchEvent(new Event('input'));
    await settle(fixture);
    textarea.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', cancelable: true }));
    await settle(fixture);
  }

  function userTexts(): string[] {
    return (store.activeConversation()?.messages ?? [])
      .filter(message => message.role === 'user')
      .flatMap(message => message.parts.map(part => part.type === 'text' ? part.text : ''));
  }

  describe('the thread in view (FB44)', () => {
    it('keeps the page\'s thread in view when the drawer is created after the page', async () => {
      await render(layout => {
        layout.drawer.set(false);
        layout.chatPage.set(true);
      });
      expect(store.threadInView()).toBeTrue();

      layout.drawer.set(true);
      await settle(fixture);

      expect(store.threadInView()).toBeTrue();
    });

    it('keeps the page\'s thread in view when the drawer closes as the page shows, and while the drawer is asked to open and close', async () => {
      await render();
      panel.open('c1');
      await settle(fixture);
      expect(store.threadInView()).toBeTrue();

      layout.chatPage.set(true);
      await settle(fixture);
      // The page took over from the drawer, whose panel reported last that it no longer shows the thread.
      expect(panel.isOpen()).toBeFalse();
      expect(store.threadInView()).toBeTrue();

      panel.toggle();
      await settle(fixture);
      panel.close();
      await settle(fixture);
      expect(store.threadInView()).toBeTrue();

      // The page's own list covers its thread.
      layout.conversationsOpen.set(true);
      await settle(fixture);
      expect(store.threadInView()).toBeFalse();
    });

    it('no longer counts a page panel once it is destroyed', async () => {
      await render(layout => layout.chatPage.set(true));
      expect(store.threadInView()).toBeTrue();

      layout.chatPage.set(false);
      await settle(fixture);

      expect(store.threadInView()).toBeFalse();
      expect(panel.pagePanelShown()).toBeFalse();
    });
  });

  describe('opening on the page (FB45)', () => {
    it('opens a conversation, a new chat and a draft in the page while the drawer stays closed', async () => {
      await render(layout => layout.chatPage.set(true));

      panel.open('c2');
      await settle(fixture);
      expect(store.activeConversation()?.id).toBe('c2');
      expect(page().querySelector('.nh-assistant-sidebar .item[aria-current="true"]')?.textContent).toContain('Budget review');

      panel.open({ agentId: 'projects', draft: 'Plan the next milestone.' });
      await settle(fixture);
      expect(store.activeConversation()).toBeNull();
      expect(pageComposer().value).toBe('Plan the next milestone.');

      expect(panel.isOpen()).toBeFalse();
      expect(overlay().querySelector('.drawer')).toBeNull();
      expect(document.activeElement).toBe(pageComposer());
    });

    it('opens the drawer as before once the person left the page', async () => {
      await render(layout => layout.chatPage.set(true));
      layout.chatPage.set(false);
      await settle(fixture);

      panel.open('c1');
      await settle(fixture);

      expect(panel.isOpen()).toBeTrue();
      expect(overlay().querySelector('.drawer')).not.toBeNull();
    });
  });

  describe('the host\'s content and its decision before sending (FB46)', () => {
    it('places the host\'s content above the thread and above the composer, in the page and in the drawer', async () => {
      await render(layout => layout.chatPage.set(true));

      const aboveThread = page().querySelector('.slot-above-thread') as HTMLElement;
      expect(aboveThread.querySelector('.page-topic')?.textContent).toBe('Topic: planning');
      expect(aboveThread.nextElementSibling?.classList).toContain('body');
      const aboveComposer = page().querySelector('.footer .slot-above-composer') as HTMLElement;
      expect(aboveComposer.querySelector('.page-choice')).not.toBeNull();
      expect(aboveComposer.nextElementSibling?.tagName.toLowerCase()).toBe('nh-assistant-composer');

      layout.chatPage.set(false);
      await settle(fixture);
      panel.open();
      await settle(fixture);
      expect(overlay().querySelector('.slot-above-thread .drawer-topic')?.textContent).toBe('Drawer topic');
      expect(overlay().querySelector('.slot-above-composer .drawer-choice')).not.toBeNull();
    });

    it('shows nothing in the places of a host without content of its own', async () => {
      TestBed.configureTestingModule({
        providers: [
          provideTranslateService({ fallbackLang: 'en' }),
          provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
          provideNhAssistantMockApi(scenario())
        ]
      });
      await firstValueFrom(TestBed.inject(TranslateService).use('en'));
      const plain = TestBed.createComponent(PlainChatPageComponent);
      await settle(plain);

      const slots = Array.from((plain.nativeElement as HTMLElement).querySelectorAll<HTMLElement>('.slot'));
      expect(slots.length).toBe(2);
      for (const slot of slots) {
        expect(getComputedStyle(slot).display).toBe('none');
      }
    });

    it('holds the first message of a new conversation for the host, which then continues an existing conversation with it', async () => {
      const asked: NhAssistantOutgoingMessage[] = [];
      await render(layout => {
        layout.chatPage.set(true);
        layout.beforeSubmit.set(message => {
          asked.push(message);
          return Promise.resolve(!message.firstMessage);
        });
      });
      store.startNewConversation();
      await settle(fixture);

      await sendFrom(pageComposer(), '  Which projects are late?  ');

      expect(asked).toEqual([{ text: 'Which projects are late?', files: [], conversationId: null, firstMessage: true, agentId: 'projects' }]);
      // Nothing was sent: the message waits in the composer as the person's own text.
      expect(store.activeConversation()).toBeNull();
      expect(store.conversations().length).toBe(2);
      expect(pageComposer().value).toBe('Which projects are late?');
      expect(store.unsentMessage()).toEqual({ text: 'Which projects are late?', prepared: false });

      // The host's decision: ask it in the planning conversation instead.
      store.keepUnsentMessage(null);
      await store.openConversation('c1');
      expect(await store.submit(asked[0].text)).toBeTrue();
      await settle(fixture);

      expect(store.activeConversation()?.id).toBe('c1');
      expect(userTexts()).toEqual(['About Weekly planning', 'Which projects are late?']);
      expect(pageComposer().value).toBe('');
      store.startNewConversation();
      await settle(fixture);
      expect(pageComposer().value).toBe('');
      expect(store.conversations().length).toBe(2);
    });

    it('lets the host send a held message as it is, and asks again for the next message', async () => {
      const asked: NhAssistantOutgoingMessage[] = [];
      await render(layout => {
        layout.chatPage.set(true);
        layout.beforeSubmit.set(message => {
          asked.push(message);
          return Promise.resolve(!message.firstMessage);
        });
      });
      store.startNewConversation();
      await settle(fixture);
      await sendFrom(pageComposer(), 'Start a risk review');

      // The host's decision: start apart, with the message as the person wrote it.
      store.keepUnsentMessage(null);
      expect(await store.submit(asked[0].text, asked[0].files)).toBeTrue();
      await settle(fixture);
      const created = store.activeConversation()!.id;
      expect(userTexts()).toEqual(['Start a risk review']);
      expect(pageComposer().value).toBe('');
      await until(fixture, () => store.activeConversation()?.status === 'idle' && !store.streaming());

      await sendFrom(pageComposer(), 'And the budget?');
      expect(asked[1]).toEqual(jasmine.objectContaining({ conversationId: created, firstMessage: false }));
      expect(userTexts()).toEqual(['Start a risk review', 'And the budget?']);
    });

    it('keeps the message in the composer and blocks sending while the host decides, then sends on true', async () => {
      let decide!: (send: boolean) => void;
      await render(layout => {
        layout.chatPage.set(true);
        layout.beforeSubmit.set(() => new Promise<boolean>(resolve => decide = resolve));
      });
      await store.openConversation('c2');
      await settle(fixture);

      await sendFrom(pageComposer(), 'Is the budget approved?');

      expect(pageComposer().value).toBe('Is the budget approved?');
      expect(page().querySelector<HTMLButtonElement>('nh-assistant-composer button.send')!.disabled).toBeTrue();
      expect(userTexts()).toEqual(['About Budget review']);

      decide(true);
      await settle(fixture);

      expect(userTexts()).toEqual(['About Budget review', 'Is the budget approved?']);
      expect(pageComposer().value).toBe('');
      expect(store.unsentMessage()).toBeNull();
    });

    it('keeps the message unsent when the person changed it or opened another conversation while the host decided', async () => {
      const decisions: ((send: boolean) => void)[] = [];
      await render(layout => {
        layout.chatPage.set(true);
        layout.beforeSubmit.set(() => new Promise<boolean>(resolve => decisions.push(resolve)));
      });
      await store.openConversation('c2');
      await settle(fixture);

      await sendFrom(pageComposer(), 'Is the budget approved?');
      pageComposer().value = 'Is the budget approved for 2027?';
      pageComposer().dispatchEvent(new Event('input'));
      await settle(fixture);
      decisions[0](true);
      await settle(fixture);
      expect(userTexts()).toEqual(['About Budget review']);
      expect(pageComposer().value).toBe('Is the budget approved for 2027?');

      await sendFrom(pageComposer(), 'Is the budget approved for 2027?');
      await store.openConversation('c1');
      decisions[1](true);
      await settle(fixture);
      expect(userTexts()).toEqual(['About Weekly planning']);
      await store.openConversation('c2');
      await settle(fixture);
      expect(userTexts()).toEqual(['About Budget review']);
      expect(pageComposer().value).toBe('Is the budget approved for 2027?');
    });

    it('asks the host in the drawer as well, and holds the message when the host\'s function fails', async () => {
      const failure = new Error('The host could not decide.');
      await render(layout => layout.beforeSubmit.set(() => Promise.reject(failure)));
      panel.open('c1');
      await settle(fixture);
      const drawerComposer = overlay().querySelector<HTMLTextAreaElement>('nh-assistant-composer textarea')!;

      await sendFrom(drawerComposer, 'Move the planning to Friday');

      expect(errorHandler.handleError).toHaveBeenCalledOnceWith(failure);
      expect(userTexts()).toEqual(['About Weekly planning']);
      expect(drawerComposer.value).toBe('Move the planning to Friday');
      expect(overlay().querySelector<HTMLButtonElement>('nh-assistant-composer button.send')!.disabled).toBeFalse();
    });
  });

  describe('the conversation list from the host (FB47, FB49)', () => {
    it('opens and closes the list two-way with conversationsOpen', async () => {
      await render(layout => {
        layout.chatPage.set(true);
        layout.conversationsOpen.set(true);
      });
      // The first conversation the panel shows leaves the host's value alone.
      const list = () => page().querySelector('.body nh-assistant-conversation-list.overlay-view');
      const toggle = () => page().querySelector<HTMLButtonElement>('.conversations-toggle')!;
      expect(list()).not.toBeNull();
      expect(toggle().getAttribute('aria-expanded')).toBe('true');

      // Choosing a conversation returns to the thread, and the host hears it.
      list()!.querySelector<HTMLButtonElement>('.item')!.click();
      await settle(fixture);
      expect(list()).toBeNull();
      expect(layout.conversationsOpen()).toBeFalse();

      toggle().click();
      await settle(fixture);
      expect(layout.conversationsOpen()).toBeTrue();
      expect(list()).not.toBeNull();

      layout.conversationsOpen.set(false);
      await settle(fixture);
      expect(list()).toBeNull();
      expect(toggle().getAttribute('aria-expanded')).toBe('false');
    });

    it('leaves the list and its button out of the page with conversationList="none"', async () => {
      await render(layout => {
        layout.chatPage.set(true);
        layout.pageList.set('none');
      });

      expect(page().querySelector('nh-assistant-conversation-list')).toBeNull();
      expect(page().querySelector('.conversations-toggle')).toBeNull();
      expect(page().querySelector('.drawer.page')!.classList).not.toContain('with-sidebar');

      layout.conversationsOpen.set(true);
      await settle(fixture);
      expect(page().querySelector('nh-assistant-conversation-list')).toBeNull();

      // The host opens conversations through the panel service instead.
      panel.open('c2');
      await settle(fixture);
      expect(store.activeConversation()?.id).toBe('c2');
      expect(panel.isOpen()).toBeFalse();
    });

    it('leaves the list and its button out of the drawer with conversationList="none"', async () => {
      await render(layout => layout.drawerList.set('none'));
      panel.open();
      await settle(fixture);

      expect(overlay().querySelector('.drawer')).not.toBeNull();
      expect(overlay().querySelector('.conversations-toggle')).toBeNull();
      expect(overlay().querySelector('nh-assistant-conversation-list')).toBeNull();
      // The new conversation button stays.
      expect(overlay().querySelector('button[aria-label="New conversation"]')).not.toBeNull();
    });
  });
});
