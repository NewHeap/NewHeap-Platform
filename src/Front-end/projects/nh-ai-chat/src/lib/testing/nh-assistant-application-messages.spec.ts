import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { Message, NhAssistantStore, NhAssistantThreadComponent, provideNhAssistant } from '@newheap/platform-ai-chat';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';

/**
 * Messages the application posted into a conversation: the thread shows them as the application's,
 * apart from people and the agent, with the sender, the title, the text as Markdown and a link that
 * opens a route of the application or an https address and nothing else; the store shows one live.
 */
const scenario: NhAssistantMockScenario = {
  agents: [{ id: 'projects', version: 1, displayNameKey: 'agents.projects', descriptionKey: 'agents.projects', canMutate: false }],
  timing: { firstEventDelayMs: 0, eventDelayMs: 0, chunkSize: 64 },
  turns: [{ steps: [{ text: 'The export runs in the background.' }] }]
};

function application(id: string, text: string, details: Partial<NonNullable<Message['application']>> = {}): Message {
  return {
    id,
    role: 'application',
    createdAt: '2026-10-23T10:00:00Z',
    sequence: 3,
    parts: [{ type: 'text', text }],
    authorActorId: null,
    authorKind: 'application',
    application: { title: null, senderName: null, link: null, ...details }
  };
}

function text(element: Element | null | undefined): string {
  return element?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

async function until(condition: () => boolean): Promise<void> {
  for (let attempt = 0; attempt < 200 && !condition(); attempt++) {
    TestBed.tick();
    await new Promise(resolve => setTimeout(resolve, 5));
  }
  expect(condition()).withContext('the condition was never met').toBeTrue();
}

describe('Application messages', () => {
  beforeEach(async () => {
    spyOn(document, 'hasFocus').and.returnValue(true);
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideTranslateService({ fallbackLang: 'en' }),
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token', defaultAgentId: 'projects' }),
        provideNhAssistantMockApi(scenario)
      ]
    });
    await firstValueFrom(TestBed.inject(TranslateService).use('en'));
  });

  it('reads as the application\'s, with its sender, title, Markdown text and a link within the application', () => {
    const fixture = TestBed.createComponent(NhAssistantThreadComponent);
    fixture.componentRef.setInput('messages', [
      application('m-1', 'The export of **12 projects** finished.', {
        title: 'Export finished',
        senderName: 'Exports',
        link: { url: '/exports/42', label: 'Open the export' }
      }),
      application('m-2', 'Maintenance tonight.')
    ]);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const [first, second] = Array.from(element.querySelectorAll<HTMLElement>('article.message'));

    expect(first.dataset['role']).toBe('application');
    expect(first.dataset['author']).toBe('application');
    expect(first.getAttribute('aria-label')).toBe('Message from the application');
    // The sender names who in the application posted it; the tag says it is the application's.
    expect(text(first.querySelector('.author'))).toBe('Exports Application');
    expect(text(first.querySelector('.application-title'))).toBe('Export finished');
    expect(first.querySelector('.bubble.markdown strong')?.textContent).toBe('12 projects');
    const link = first.querySelector<HTMLAnchorElement>('nh-assistant-message-link a')!;
    expect(text(link)).toBe('Open the export');
    expect(link.getAttribute('href')).toBe('/exports/42');
    // Without a sender, title or link it shows the application and its text only.
    expect(text(second.querySelector('.author'))).toBe('Application');
    expect(second.querySelector('.tag')).toBeNull();
    expect(second.querySelector('.application-title')).toBeNull();
    expect(second.querySelector('nh-assistant-message-link')).toBeNull();

    // A plain click opens the route through the router.
    const navigate = spyOn(TestBed.inject(Router), 'navigateByUrl').and.resolveTo(true);
    link.dispatchEvent(new MouseEvent('click', { button: 0, bubbles: true, cancelable: true }));
    expect(navigate).toHaveBeenCalledOnceWith('/exports/42');
  });

  it('shows an https link with its own text, and no link for any other address', () => {
    const fixture = TestBed.createComponent(NhAssistantThreadComponent);
    fixture.componentRef.setInput('messages', [
      application('m-1', 'Done.', { link: { url: 'https://files.example.com/exports/42', label: null } }),
      application('m-2', 'Done.', { link: { url: 'javascript:alert(1)', label: 'Open' } }),
      application('m-3', 'Done.', { link: { url: '//evil.example/export', label: 'Open' } })
    ]);
    fixture.detectChanges();
    const articles = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('article.message'));

    const external = articles[0].querySelector<HTMLAnchorElement>('nh-assistant-message-link a')!;
    expect(external.getAttribute('href')).toBe('https://files.example.com/exports/42');
    expect(text(external)).toBe('Open');
    expect(articles[1].querySelector('nh-assistant-message-link a')).toBeNull();
    expect(articles[2].querySelector('nh-assistant-message-link a')).toBeNull();
  });

  it('arrives live in the open conversation as the application posts it', async () => {
    const store = TestBed.inject(NhAssistantStore);
    const backend = TestBed.inject(NhAssistantMockBackend);
    await store.initialize();
    expect(await store.send('Export the projects.')).toBeTrue();
    await until(() => store.activeConversation()?.status === 'idle' && !store.streaming());
    const conversationId = store.activeConversation()!.id;

    backend.postApplicationMessage(conversationId, 'The export finished.', {
      title: 'Export finished',
      senderName: 'Exports',
      link: { url: '/exports/42', label: null }
    });
    await until(() => store.activeConversation()!.messages.some(message => message.role === 'application'));

    const posted = store.activeConversation()!.messages.at(-1)!;
    expect([posted.role, posted.authorKind, posted.sequence, posted.application?.title]).toEqual(['application', 'application', 3, 'Export finished']);
    const fixture = TestBed.createComponent(NhAssistantThreadComponent);
    fixture.componentRef.setInput('messages', store.activeConversation()!.messages);
    fixture.detectChanges();
    const last = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('article.message')).at(-1)!;
    expect(text(last.querySelector('.author'))).toBe('Exports Application');
    expect(text(last.querySelector('nh-assistant-message-link a'))).toBe('Open');
  });
});
