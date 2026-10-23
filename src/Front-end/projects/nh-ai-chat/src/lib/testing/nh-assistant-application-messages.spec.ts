import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { Message, NhAssistantStore, NhAssistantThreadComponent, ToolCallPart, provideNhAssistant } from '@newheap/platform-ai-chat';
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
    application: { title: null, senderName: null, link: null, trusted: false, ...details }
  };
}

function toolCall(invocationId: string, status: ToolCallPart['status']): ToolCallPart {
  return {
    type: 'tool-call',
    invocationId,
    toolId: `sample-api.${invocationId}`,
    toolVersion: 1,
    displayName: invocationId,
    status,
    argumentsPreview: null,
    resultPreview: null,
    resultCode: null
  };
}

/** The thread from top to bottom: the id of each message, and `working` for the working indicator. */
function order(element: HTMLElement, messages: readonly Message[]): string[] {
  const articles = Array.from(element.querySelectorAll('article.message'));
  return Array.from(element.querySelectorAll('article.message, .working')).map(node =>
    node.matches('.working') ? 'working' : messages[articles.indexOf(node)].id);
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

  it('labels a message the assistant follows as an instruction, in English and Dutch', async () => {
    const fixture = TestBed.createComponent(NhAssistantThreadComponent);
    fixture.componentRef.setInput('messages', [
      application('m-1', 'Offer a short summary of the analysis.', { senderName: 'Portfolio analysis', trusted: true }),
      application('m-2', 'Offer a short summary of the analysis.', { trusted: true }),
      application('m-3', 'The analysis finished.', { senderName: 'Portfolio analysis' })
    ]);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const [first, second, third] = Array.from(element.querySelectorAll<HTMLElement>('article.message'));

    expect(text(first.querySelector('.author'))).toBe('Portfolio analysis Application Instruction for the assistant');
    expect(text(first.querySelector('.tag.trusted'))).toBe('Instruction for the assistant');
    expect(text(second.querySelector('.author'))).toBe('Application Instruction for the assistant');
    // A message the assistant reads as information carries no label.
    expect(third.querySelector('.tag.trusted')).toBeNull();

    await firstValueFrom(TestBed.inject(TranslateService).use('nl'));
    fixture.detectChanges();
    expect(text(first.querySelector('.tag.trusted'))).toBe('Instructie voor de assistent');
  });

  it('keeps the working indicator under the answer being written when a message arrives after it', () => {
    const fixture = TestBed.createComponent(NhAssistantThreadComponent);
    const question: Message = { id: 'u-1', role: 'user', createdAt: '', sequence: 1, parts: [{ type: 'text', text: 'Export the projects.' }], authorActorId: 'me' };
    const answer: Message = { id: 'a-1', role: 'assistant', createdAt: '', parts: [toolCall('export', 'succeeded')] };
    const arrived = application('app-1', 'The export finished.');
    const colleague: Message = { id: 'u-2', role: 'user', createdAt: '', sequence: 4, parts: [{ type: 'text', text: 'I added the budget.' }], authorActorId: 'sam' };
    fixture.componentRef.setInput('currentActorId', 'me');
    fixture.componentRef.setInput('streaming', true);
    const element = fixture.nativeElement as HTMLElement;

    // Without a message that arrived, the indicator follows the answer at the end, as always.
    fixture.componentRef.setInput('messages', [question, answer]);
    fixture.detectChanges();
    expect(order(element, [question, answer])).toEqual(['u-1', 'a-1', 'working']);

    // The application's and a colleague's message sit after the answer; the indicator stays under it.
    const messages = [question, answer, arrived, colleague];
    fixture.componentRef.setInput('messages', messages);
    fixture.detectChanges();
    expect(order(element, messages)).toEqual(['u-1', 'a-1', 'working', 'app-1', 'u-2']);

    // While the answer streams text, it shows its own progress, also with the messages after it.
    const writing = [question, { ...answer, parts: [...answer.parts, { type: 'text' as const, text: 'Exporting' }] }, arrived, colleague];
    fixture.componentRef.setInput('messages', writing);
    fixture.detectChanges();
    expect(order(element, writing)).toEqual(['u-1', 'a-1', 'app-1', 'u-2']);

    // Before the turn started its answer, a message that arrived sits last and so does the indicator.
    const waiting = [question, arrived];
    fixture.componentRef.setInput('messages', waiting);
    fixture.detectChanges();
    expect(order(element, waiting)).toEqual(['u-1', 'app-1', 'working']);
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
    expect([posted.role, posted.authorKind, posted.sequence, posted.application?.title, posted.application?.trusted])
      .toEqual(['application', 'application', 3, 'Export finished', false]);
    // The mock API posts an instruction for the assistant as the server does.
    const instruction = backend.postApplicationMessage(conversationId, 'Offer a short summary.', { trusted: true })!;
    await until(() => store.activeConversation()!.messages.some(message => message.id === instruction.id));
    expect(store.activeConversation()!.messages.at(-1)!.application?.trusted).toBeTrue();
    const fixture = TestBed.createComponent(NhAssistantThreadComponent);
    fixture.componentRef.setInput('messages', store.activeConversation()!.messages);
    fixture.detectChanges();
    const [exported, instructed] = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('article.message')).slice(-2);
    expect(text(exported.querySelector('.author'))).toBe('Exports Application');
    expect(text(exported.querySelector('nh-assistant-message-link a'))).toBe('Open');
    expect(text(instructed.querySelector('.author'))).toBe('Application Instruction for the assistant');
  });
});
