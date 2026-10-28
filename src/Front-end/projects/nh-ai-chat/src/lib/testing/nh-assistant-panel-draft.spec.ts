import { OverlayContainer } from '@angular/cdk/overlay';
import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { NhAssistantPanelComponent } from '../components/panel/nh-assistant-panel.component';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantApiService } from '../services/nh-assistant-api.service';
import { NhAssistantPanelService } from '../services/nh-assistant-panel.service';
import { NhAssistantStore } from '../services/nh-assistant.store';

/** The application layout with its one drawer panel. */
@Component({
  standalone: true,
  imports: [NhAssistantPanelComponent],
  template: '<nh-assistant-panel />'
})
class LayoutComponent {}

/** A page of the application that shows the chat in place. */
@Component({
  standalone: true,
  imports: [NhAssistantPanelComponent],
  template: '<div style="height: 640px"><nh-assistant-panel mode="page" /></div>'
})
class ChatPageComponent {}

/** A brief of 46 characters, within the scenario's limit of 60. */
const brief = 'Write a short status brief on Alpha migration.';

function scenario(): NhAssistantMockScenario {
  return {
    agents: [
      { id: 'projects', version: 1, displayNameKey: 'agents.projects', descriptionKey: 'agents.projects', canMutate: true },
      { id: 'catalog', version: 1, displayNameKey: 'agents.catalog', descriptionKey: 'agents.catalog', canMutate: false }
    ],
    limits: { maxMessageChars: 60 },
    conversations: [{
      id: 'c1',
      agentId: 'projects',
      agentVersion: 1,
      title: 'Weekly planning',
      status: 'idle',
      createdAt: '2026-10-28T09:00:00Z',
      updatedAt: '2026-10-28T09:00:00Z',
      messages: [],
      pendingApproval: null
    }],
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

async function render<T>(component: new () => T): Promise<ComponentFixture<T>> {
  TestBed.configureTestingModule({
    providers: [
      provideTranslateService({ fallbackLang: 'en' }),
      provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
      provideNhAssistantMockApi(scenario())
    ]
  });
  await firstValueFrom(TestBed.inject(TranslateService).use('en'));
  const fixture = TestBed.createComponent(component);
  await settle(fixture);
  return fixture;
}

function type(textarea: HTMLTextAreaElement, text: string): void {
  textarea.value = text;
  textarea.dispatchEvent(new Event('input'));
}

describe('A prepared draft in the assistant panel (FB27)', () => {
  describe('in the drawer', () => {
    let fixture: ComponentFixture<LayoutComponent>;
    let panel: NhAssistantPanelService;
    let store: NhAssistantStore;
    let sendMessage: jasmine.Spy;
    let createConversation: jasmine.Spy;

    function composer(): HTMLTextAreaElement {
      return TestBed.inject(OverlayContainer).getContainerElement().querySelector<HTMLTextAreaElement>('nh-assistant-composer textarea')!;
    }

    beforeEach(async () => {
      fixture = await render(LayoutComponent);
      panel = TestBed.inject(NhAssistantPanelService);
      store = TestBed.inject(NhAssistantStore);
      const api = TestBed.inject(NhAssistantApiService);
      sendMessage = spyOn(api, 'sendMessage').and.callThrough();
      createConversation = spyOn(api, 'createConversation').and.callThrough();
    });

    it('opens a new chat with the agent and the draft in the composer, without sending it', async () => {
      panel.open('c1');
      await settle(fixture);
      expect(store.activeConversation()?.id).toBe('c1');

      panel.open({ agentId: 'catalog', draft: brief });
      await settle(fixture);

      expect(panel.isOpen()).toBeTrue();
      expect(store.activeConversation()).toBeNull();
      expect(store.selectedAgentId()).toBe('catalog');
      expect(composer().value).toBe(brief);
      // The composer took the draft; the store holds it no longer.
      expect(store.pendingDraft()).toBeNull();
      expect(createConversation).not.toHaveBeenCalled();
      expect(sendMessage).not.toHaveBeenCalled();
    });

    it('keeps what the person typed and replaces it only with replaceDraft', async () => {
      panel.open();
      await settle(fixture);
      type(composer(), 'My own question');

      panel.open({ draft: brief });
      await settle(fixture);
      expect(composer().value).toBe('My own question');

      panel.open({ draft: brief, replaceDraft: true });
      await settle(fixture);
      expect(composer().value).toBe(brief);
      expect(sendMessage).not.toHaveBeenCalled();
    });

    it('caps the draft at the server limit, so the person can send it as it is', async () => {
      const long = `${brief} Mention the risks and the next three steps.`;
      panel.open({ draft: long });
      await settle(fixture);

      expect(composer().value).toBe(long.slice(0, 60).trimEnd());
      expect(composer().value.length).toBeLessThanOrEqual(60);
      const container = TestBed.inject(OverlayContainer).getContainerElement();
      expect(container.querySelector<HTMLButtonElement>('nh-assistant-composer button.send')!.disabled).toBeFalse();
      expect(composer().getAttribute('aria-invalid')).toBeNull();
    });
  });

  describe('in a page (mode="page")', () => {
    it('shows a draft from the store in the page\'s composer and keeps what the person typed', async () => {
      const fixture = await render(ChatPageComponent);
      const store = TestBed.inject(NhAssistantStore);
      const element = fixture.nativeElement as HTMLElement;
      const textarea = element.querySelector<HTMLTextAreaElement>('nh-assistant-composer textarea')!;

      expect(store.setDraft(brief)).toBeTrue();
      await settle(fixture);
      expect(textarea.value).toBe(brief);

      type(textarea, 'My own question');
      expect(store.setDraft('Another brief')).toBeTrue();
      await settle(fixture);
      expect(textarea.value).toBe('My own question');

      expect(store.setDraft('Another brief', { replace: true })).toBeTrue();
      await settle(fixture);
      expect(textarea.value).toBe('Another brief');
      expect(store.activeConversation()).toBeNull();
      // Nothing opened in the layout's drawer.
      expect(TestBed.inject(OverlayContainer).getContainerElement().querySelector('nh-assistant-composer')).toBeNull();
    });
  });
});
