import { OverlayContainer } from '@angular/cdk/overlay';
import { Component, Injectable, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { BehaviorSubject, Observable, firstValueFrom, of } from 'rxjs';
import { NhAssistantPanelComponent } from '../components/panel/nh-assistant-panel.component';
import { NhAssistantSseEvent } from '../models/assistant-sse.models';
import { NhAssistantAccessPolicy } from '../nh-assistant.config';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantApiService } from '../services/nh-assistant-api.service';
import { NhAssistantPanelService } from '../services/nh-assistant-panel.service';
import { NhAssistantStore } from '../services/nh-assistant.store';

/** Whether the signed-in person may use the assistant; `false` is signing out. */
const access = new BehaviorSubject(true);
/** The signed-in person, as the host's state scope names them. */
let person = 'person-a';

@Injectable()
class TestAccessPolicy implements NhAssistantAccessPolicy {
  canUse(): Observable<boolean> {
    return access;
  }
}

/** The application layout with its drawer, and a chat page the person can leave and come back to. */
@Component({
  standalone: true,
  imports: [NhAssistantPanelComponent],
  template: `
    <nh-assistant-panel />
    @if (chatPage()) {
      <div class="chat-page" style="height: 640px"><nh-assistant-panel mode="page" /></div>
    }
  `
})
class LayoutComponent {
  readonly chatPage = signal(false);
}

const brief = 'Write a short status brief on Alpha migration.';

function scenario(): NhAssistantMockScenario {
  const conversation = (id: string, title: string) => ({
    id,
    agentId: 'projects',
    agentVersion: 1,
    title,
    status: 'idle' as const,
    createdAt: '2026-10-28T09:00:00Z',
    updatedAt: '2026-10-28T09:00:00Z',
    messages: [],
    pendingApproval: null
  });
  return {
    agents: [
      { id: 'projects', version: 1, displayNameKey: 'agents.projects', descriptionKey: 'agents.projects', canMutate: true },
      { id: 'catalog', version: 1, displayNameKey: 'agents.catalog', descriptionKey: 'agents.catalog', canMutate: false }
    ],
    limits: { maxMessageChars: 120 },
    conversations: [conversation('c1', 'Weekly planning'), conversation('c2', 'Budget review')],
    turns: []
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


function browserStorage(): string {
  const values: string[] = [];
  for (const storage of [localStorage, sessionStorage]) {
    for (let index = 0; index < storage.length; index++) {
      values.push(storage.getItem(storage.key(index)!) ?? '');
    }
  }
  return values.join('\n');
}

/** FB41: a closed drawer, another conversation or a page left behind keeps the unsent message. */
describe('The unsent message in the assistant panel (FB41)', () => {
  let fixture: ComponentFixture<LayoutComponent>;
  let panel: NhAssistantPanelService;
  let store: NhAssistantStore;
  let api: NhAssistantApiService;

  beforeEach(async () => {
    access.next(true);
    person = `person-a-${crypto.randomUUID()}`;
    TestBed.configureTestingModule({
      providers: [
        provideTranslateService({ fallbackLang: 'en' }),
        provideNhAssistant({
          apiBaseUrl: '/api/assistant',
          getAccessToken: () => 'token',
          accessPolicy: TestAccessPolicy,
          getStateScope: () => person
        }),
        provideNhAssistantMockApi(scenario())
      ]
    });
    await firstValueFrom(TestBed.inject(TranslateService).use('en'));
    fixture = TestBed.createComponent(LayoutComponent);
    await settle(fixture);
    panel = TestBed.inject(NhAssistantPanelService);
    store = TestBed.inject(NhAssistantStore);
    api = TestBed.inject(NhAssistantApiService);
  });

  /** Types as a person does: the input event, then the render that follows it in the browser. */
  async function type(textarea: HTMLTextAreaElement, text: string): Promise<void> {
    textarea.value = text;
    textarea.dispatchEvent(new Event('input'));
    await settle(fixture);
  }

  function drawerComposer(): HTMLTextAreaElement | null {
    return TestBed.inject(OverlayContainer).getContainerElement().querySelector<HTMLTextAreaElement>('nh-assistant-composer textarea');
  }

  function pageComposer(): HTMLTextAreaElement | null {
    return (fixture.nativeElement as HTMLElement).querySelector<HTMLTextAreaElement>('.chat-page nh-assistant-composer textarea');
  }

  async function reopen(): Promise<void> {
    panel.close();
    await settle(fixture);
    expect(drawerComposer()).toBeNull();
    panel.open();
    await settle(fixture);
  }

  it('keeps what the person typed when the drawer closes and shows it when it opens again', async () => {
    panel.open();
    await settle(fixture);
    await type(drawerComposer()!, 'Which projects are late? ');

    await reopen();

    expect(drawerComposer()!.value).toBe('Which projects are late? ');
    const send = TestBed.inject(OverlayContainer).getContainerElement().querySelector<HTMLButtonElement>('nh-assistant-composer button.send')!;
    expect(send.disabled).toBeFalse();
    // In memory only.
    expect(browserStorage()).not.toContain('Which projects are late?');
  });

  it('keeps the text of each conversation when the person opens another one and comes back', async () => {
    panel.open('c1');
    await settle(fixture);
    await type(drawerComposer()!, 'About the planning');

    await store.openConversation('c2');
    await settle(fixture);
    expect(drawerComposer()!.value).toBe('');
    await type(drawerComposer()!, 'About the budget');

    await store.openConversation('c1');
    await settle(fixture);
    expect(drawerComposer()!.value).toBe('About the planning');
    await store.openConversation('c2');
    await settle(fixture);
    expect(drawerComposer()!.value).toBe('About the budget');

    // A new chat has text of its own.
    const newChat = TestBed.inject(OverlayContainer).getContainerElement()
      .querySelector<HTMLButtonElement>('button[aria-label="New conversation"]')!;
    newChat.click();
    await settle(fixture);
    expect(drawerComposer()!.value).toBe('');
  });

  it('shows the same text in the drawer and on the chat page', async () => {
    fixture.componentInstance.chatPage.set(true);
    await settle(fixture);
    await type(pageComposer()!, 'Started on the chat page');

    // The person leaves the page and opens the drawer elsewhere.
    fixture.componentInstance.chatPage.set(false);
    panel.open();
    await settle(fixture);
    expect(drawerComposer()!.value).toBe('Started on the chat page');
    await type(drawerComposer()!, 'Started on the chat page, finished in the drawer');

    panel.close();
    fixture.componentInstance.chatPage.set(true);
    await settle(fixture);
    expect(pageComposer()!.value).toBe('Started on the chat page, finished in the drawer');
  });

  it('clears the text once it is sent', async () => {
    panel.open();
    await settle(fixture);
    const input = drawerComposer()!;
    await type(input, 'What am I looking at?');
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', cancelable: true }));
    await settle(fixture);

    expect(store.activeConversation()?.messages.some(message => message.role === 'user')).toBeTrue();
    expect(store.unsentMessage()).toBeNull();
    await reopen();
    expect(drawerComposer()!.value).toBe('');
  });

  it('keeps a message the server refused as the person\'s own text', async () => {
    const refusal: NhAssistantSseEvent = {
      type: 'error',
      data: { code: 'assistant-conversation-busy', messageKey: 'nh-assistant.errors.assistant-conversation-busy' }
    };
    spyOn(api, 'sendMessage').and.returnValue(of(refusal));
    panel.open('c1');
    await settle(fixture);
    const input = drawerComposer()!;
    await type(input, 'Pause Alpha');
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', cancelable: true }));
    await settle(fixture);
    expect(drawerComposer()!.value).toBe('Pause Alpha');

    await reopen();
    expect(drawerComposer()!.value).toBe('Pause Alpha');
    panel.open({ draft: brief });
    await settle(fixture);
    expect(drawerComposer()!.value).toBe('Pause Alpha');
  });

  it('forgets the text when the person signs out and when another person signs in', async () => {
    panel.open('c1');
    await settle(fixture);
    await type(drawerComposer()!, 'The first person\'s question');
    panel.close();
    access.next(false);
    await settle(fixture);
    access.next(true);
    await settle(fixture);
    panel.open('c1');
    await settle(fixture);
    expect(drawerComposer()!.value).toBe('');

    await type(drawerComposer()!, 'Typed before the other person signs in');
    person = `person-b-${crypto.randomUUID()}`;
    await store.reloadStatus();
    await settle(fixture);
    expect(drawerComposer()!.value).toBe('');
    panel.open('c1');
    await settle(fixture);
    expect(drawerComposer()!.value).toBe('');
  });

  describe('with a prepared draft (FB27)', () => {
    it('lets an unchanged draft give way to the next one after the drawer closed, and keeps a changed one', async () => {
      panel.open({ draft: brief });
      await settle(fixture);
      expect(drawerComposer()!.value).toBe(brief);

      await reopen();
      expect(drawerComposer()!.value).toBe(brief);
      panel.open({ draft: 'Write a brief on the budget review.' });
      await settle(fixture);
      expect(drawerComposer()!.value).toBe('Write a brief on the budget review.');

      await type(drawerComposer()!, 'Write a brief on the budget review, in Dutch.');
      await reopen();
      panel.open({ draft: 'Write a brief on the risks.' });
      await settle(fixture);
      expect(drawerComposer()!.value).toBe('Write a brief on the budget review, in Dutch.');
      panel.open({ draft: 'Write a brief on the risks.', replaceDraft: true });
      await settle(fixture);
      expect(drawerComposer()!.value).toBe('Write a brief on the risks.');
    });

    it('puts a draft for a new chat in its empty composer and leaves the conversation its own text', async () => {
      panel.open('c1');
      await settle(fixture);
      await type(drawerComposer()!, 'About the planning');

      panel.open({ agentId: 'catalog', draft: brief });
      await settle(fixture);
      expect(store.activeConversation()).toBeNull();
      expect(drawerComposer()!.value).toBe(brief);

      await store.openConversation('c1');
      await settle(fixture);
      expect(drawerComposer()!.value).toBe('About the planning');
      store.startNewConversation();
      await settle(fixture);
      expect(drawerComposer()!.value).toBe(brief);
    });
  });

  it('takes the text along when the person chooses another agent in the composer', async () => {
    panel.open('c1');
    await settle(fixture);
    await type(drawerComposer()!, 'Which products are new?');

    const picker = TestBed.inject(OverlayContainer).getContainerElement()
      .querySelector<HTMLSelectElement>('.prompt-bar nh-assistant-agent-picker select')!;
    picker.value = 'catalog';
    picker.dispatchEvent(new Event('change'));
    await settle(fixture);

    expect(store.activeConversation()).toBeNull();
    expect(store.selectedAgentId()).toBe('catalog');
    expect(drawerComposer()!.value).toBe('Which products are new?');
    await store.openConversation('c1');
    await settle(fixture);
    expect(drawerComposer()!.value).toBe('');
  });
});
