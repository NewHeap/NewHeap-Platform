import { OverlayContainer } from '@angular/cdk/overlay';
import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { NhAssistantPanelComponent } from '../components/panel/nh-assistant-panel.component';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantApiError, NhAssistantApiService } from '../services/nh-assistant-api.service';
import { NhAssistantPanelService } from '../services/nh-assistant-panel.service';
import { NhAssistantStore } from '../services/nh-assistant.store';

/** The application layout with its one drawer panel. */
@Component({
  standalone: true,
  imports: [NhAssistantPanelComponent],
  template: '<nh-assistant-panel />'
})
class LayoutComponent {}

function scenario(): NhAssistantMockScenario {
  return {
    agents: [
      { id: 'projects', version: 1, displayNameKey: 'agents.projects', descriptionKey: 'agents.projects', canMutate: true },
      { id: 'catalog', version: 1, displayNameKey: 'agents.catalog', descriptionKey: 'agents.catalog', canMutate: false }
    ],
    subjectTypes: [{
      id: 'project',
      names: { en: 'Project', nl: 'Project' },
      link: '/projects/{id}',
      subjects: [
        { id: 'p-1', displayName: 'Alpha migration' },
        { id: 'p-2', displayName: 'Hidden project', readable: false }
      ]
    }],
    timing: { firstEventDelayMs: 0, eventDelayMs: 0, chunkSize: 64 },
    turns: [{ match: 'status', steps: [{ text: 'Alpha is on track.' }] }]
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

async function render(): Promise<ComponentFixture<LayoutComponent>> {
  TestBed.configureTestingModule({
    providers: [
      provideTranslateService({ fallbackLang: 'en' }),
      provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
      provideNhAssistantMockApi(scenario())
    ]
  });
  await firstValueFrom(TestBed.inject(TranslateService).use('en'));
  const fixture = TestBed.createComponent(LayoutComponent);
  await settle(fixture);
  return fixture;
}

describe('NhAssistantStore chats about a subject (FB25)', () => {
  it('creates the next new chat about the subject the host set, and shows what it is about', async () => {
    const fixture = await render();
    const store = TestBed.inject(NhAssistantStore);
    await store.initialize();

    TestBed.inject(NhAssistantPanelService).open();
    expect(await store.setChatSubject({ subjectType: 'project', subjectId: 'p-1', subjectDisplayName: 'Alpha migration' })).toBeTrue();
    await settle(fixture);
    expect(store.newChatSubject()).toEqual({ subjectType: 'project', subjectId: 'p-1', subjectDisplayName: 'Alpha migration' });
    expect(store.chatAgents().map(agent => agent.id)).toEqual(['projects', 'catalog']);
    expect(store.activeConversation()).toBeNull();
    expect(subjectLine()?.textContent).toContain('Alpha migration');

    expect(await store.send('What is the status?')).toBeTrue();
    await settle(fixture);

    const conversation = store.activeConversation();
    expect(conversation?.subject).toEqual({
      subjectType: 'project',
      subjectId: 'p-1',
      subjectDisplayName: 'Alpha migration',
      subjectTypeName: 'Project',
      subjectLink: '/projects/p-1'
    });
    expect(store.conversations().find(item => item.id === conversation?.id)?.subject?.subjectId).toBe('p-1');
    expect(store.chatSubject()?.subjectId).toBe('p-1');
    expect(subjectLine()?.textContent).toContain('Project: Alpha migration');

    // The subject was for that chat: the next new chat is about nothing in particular.
    expect(store.newChatSubject()).toBeNull();
    store.startNewConversation();
    await settle(fixture);
    expect(store.chatSubject()).toBeNull();
    expect(subjectLine()).toBeNull();

    // A host that leaves its page clears a subject nobody used.
    await store.setChatSubject({ subjectType: 'project', subjectId: 'p-1' });
    expect(await store.setChatSubject(null)).toBeTrue();
    expect(store.newChatSubject()).toBeNull();
    expect(store.chatAgents().map(agent => agent.id)).toEqual(['projects', 'catalog']);
  });

  it('keeps the previous subject and shows the refusal when the person may not use a chat about it', async () => {
    const fixture = await render();
    const store = TestBed.inject(NhAssistantStore);
    await store.initialize();
    await store.setChatSubject({ subjectType: 'project', subjectId: 'p-1' });

    expect(await store.setChatSubject({ subjectType: 'project', subjectId: 'p-2' })).toBeFalse();
    await settle(fixture);

    expect(store.newChatSubject()?.subjectId).toBe('p-1');
    expect(store.error()?.code).toBe('assistant-subject-not-found');
  });

  it('asks the agents of a chat about a subject with the subject in the query', async () => {
    await render();
    const api = TestBed.inject(NhAssistantApiService);

    const agents = await firstValueFrom(api.agents({ subjectType: 'project', subjectId: 'p-1' }));
    expect(agents.map(agent => agent.id)).toEqual(['projects', 'catalog']);

    let refused: unknown = null;
    try {
      await firstValueFrom(api.agents({ subjectType: 'nh-system', subjectId: 'global' }));
    } catch (error) {
      refused = error;
    }
    expect(refused instanceof NhAssistantApiError && refused.code).toBe('assistant-subject-invalid');
  });
});

/** The panel's line that says what the open or next chat is about, in the drawer's overlay. */
function subjectLine(): HTMLElement | null {
  return TestBed.inject(OverlayContainer).getContainerElement().querySelector<HTMLElement>('.chat-subject');
}
