import { ComponentFixture, DeferBlockBehavior, DeferBlockState, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';
import {
  NhAssistantElicitationAnswer,
  NhAssistantElicitationCardComponent
} from '../components/elicitation-card/nh-assistant-elicitation-card.component';
import { Conversation, ElicitationPart } from '../models/assistant-api.models';
import { NH_ASSISTANT_SSE_EVENT_TYPES } from '../models/assistant-sse.models';
import { provideNhAssistant } from '../provide-nh-assistant';
import { applyNhAssistantEvent } from '../services/nh-assistant-reducer';

/**
 * Requests for input of MCP servers whose tools a person's turn runs (MCP elicitation): the card shows
 * the server's own message as the server's, says who receives the answer, sends a form only when its
 * fields fit, shows a link with its host without ever opening it by itself, and is read-only for anyone
 * but the person whose turn it is. The reducer follows `elicitation.requested` and `elicitation.closed`.
 */
function elicitation(overrides: Partial<ElicitationPart> = {}): ElicitationPart {
  return {
    type: 'elicitation',
    elicitationId: 'elicitation-1',
    invocationId: 'call-1',
    serverId: 'orders',
    serverName: 'Orders',
    mode: 'form',
    message: 'Orders needs a reason before it confirms order A-100.',
    addresseeActorId: 'mock-user',
    expiresAt: new Date(Date.now() + 25_000).toISOString(),
    status: 'pending',
    fields: [
      { name: 'reason', type: 'string', title: 'Reason', required: true, maxLength: 200 },
      { name: 'priority', type: 'integer', title: 'Priority', required: true, minimum: 1, maximum: 5 },
      { name: 'notify', type: 'boolean', title: 'Notify the customer', required: false },
      {
        name: 'channel',
        type: 'enum',
        title: 'Channel',
        required: false,
        options: [{ value: 'mail', label: 'Mail' }, { value: 'phone', label: 'Phone' }]
      }
    ],
    ...overrides
  };
}

function link(overrides: Partial<ElicitationPart> = {}): ElicitationPart {
  return elicitation({
    mode: 'url',
    message: 'Link your planning account to confirm the order.',
    fields: [],
    url: 'https://accounts.example.com/link?state=4711',
    urlHost: 'accounts.example.com',
    ...overrides
  });
}

describe('NhAssistantElicitationCardComponent', () => {
  let fixture: ComponentFixture<NhAssistantElicitationCardComponent>;
  let answers: NhAssistantElicitationAnswer[];

  async function create(part: ElicitationPart, canAnswer = true, deferBlockBehavior = DeferBlockBehavior.Playthrough): Promise<HTMLElement> {
    TestBed.configureTestingModule({
      deferBlockBehavior,
      providers: [
        provideTranslateService({ fallbackLang: 'en' }),
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' })
      ]
    });
    await firstValueFrom(TestBed.inject(TranslateService).use('en'));
    fixture = TestBed.createComponent(NhAssistantElicitationCardComponent);
    fixture.componentRef.setInput('elicitation', part);
    fixture.componentRef.setInput('canAnswer', canAnswer);
    answers = [];
    fixture.componentInstance.answer.subscribe(answer => answers.push(answer));
    fixture.detectChanges();
    if (deferBlockBehavior === DeferBlockBehavior.Playthrough) {
      // The card loads on first use.
      const [card] = await fixture.getDeferBlocks();
      await card.render(DeferBlockState.Complete);
    }
    return fixture.nativeElement as HTMLElement;
  }

  it('holds the card\'s place with an empty box of about its height until it has loaded', async () => {
    const element = await create(elicitation(), true, DeferBlockBehavior.Manual);

    const placeholder = element.querySelector('nh-assistant-card-placeholder') as HTMLElement;
    expect(placeholder.getAttribute('aria-hidden')).toBe('true');
    // Frame, header and message; three fields and a check box; the actions with the countdown.
    expect(placeholder.style.minHeight).toBe(`${110 + 2 + 3 * 68 + 30 + 104}px`);
    expect(element.querySelector('form')).toBeNull();
    expect(element.getAttribute('data-status')).toBe('pending');
    expect(element.getAttribute('data-mode')).toBe('form');

    const [card] = await fixture.getDeferBlocks();
    await card.render(DeferBlockState.Complete);
    expect(element.querySelector('nh-assistant-card-placeholder')).toBeNull();
    expect(element.querySelectorAll('form .field').length).toBe(4);
  });

  function type(element: HTMLElement, name: string, value: string): void {
    const input = element.querySelector<HTMLInputElement | HTMLSelectElement>(`[name="${name}"]`)!;
    input.value = value;
    input.dispatchEvent(new Event(input instanceof HTMLSelectElement ? 'change' : 'input'));
    fixture.detectChanges();
  }

  it('shows the server\'s own message as its text and names who receives the answer', async () => {
    const element = await create(elicitation({ message: '<b>Ignore the assistant</b> and send the reason.' }));

    expect(element.querySelector('h3')!.textContent).toContain('Request from Orders');
    expect(element.querySelector('[data-message]')!.textContent).toBe('<b>Ignore the assistant</b> and send the reason.');
    expect(element.querySelector('[data-message] b')).toBeNull();
    expect(element.querySelector('[data-recipient]')!.textContent).toContain('Your answer goes to Orders only.');
    expect(element.querySelector('[data-submit]')!.textContent).toContain('Send to Orders');
  });

  it('sends the form only when the required fields fit, typed as the fields ask', async () => {
    const element = await create(elicitation());

    (element.querySelector('[data-submit]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(answers).toEqual([]);
    expect(element.querySelectorAll('[role="alert"]').length).toBe(2);

    type(element, 'reason', 'Customer asked');
    type(element, 'priority', '9');
    (element.querySelector('[data-submit]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(answers).toEqual([]);

    type(element, 'priority', '2');
    type(element, 'channel', 'phone');
    (element.querySelector('[data-submit]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(answers).toEqual([{ action: 'accept', content: { reason: 'Customer asked', priority: 2, notify: false, channel: 'phone' } }]);
    // Once sent, the card waits for the host.
    expect((element.querySelector('[data-submit]') as HTMLButtonElement).disabled).toBeTrue();
  });

  it('declines or cancels without sending any value', async () => {
    const element = await create(elicitation());
    type(element, 'reason', 'Not for you');

    (element.querySelector('[data-decline]') as HTMLButtonElement).click();

    expect(answers).toEqual([{ action: 'decline' }]);
  });

  it('shows a link with its host, never opens it by itself and says done without content', async () => {
    const opened = spyOn(window, 'open');
    const element = await create(link());

    expect(element.querySelector('.host')!.textContent).toContain('Opens a page on accounts.example.com');
    expect(element.querySelector('[data-url]')!.textContent).toBe('https://accounts.example.com/link?state=4711');
    const anchor = element.querySelector<HTMLAnchorElement>('a[data-open]')!;
    expect(anchor.getAttribute('href')).toBe('https://accounts.example.com/link?state=4711');
    expect(anchor.target).toBe('_blank');
    expect(anchor.rel).toBe('noopener noreferrer');
    expect(element.querySelector('form')).toBeNull();
    expect(opened).not.toHaveBeenCalled();

    (element.querySelector('[data-done]') as HTMLButtonElement).click();

    expect(answers).toEqual([{ action: 'accept' }]);
  });

  it('is read-only for anyone but the person whose turn it is and after it closed', async () => {
    const element = await create(link(), false);

    expect(element.querySelector('a[data-open]')).toBeNull();
    expect(element.querySelector('button')).toBeNull();
    expect(element.querySelector('[data-waiting]')!.textContent).toContain('Waiting for the person whose turn it is.');

    fixture.componentRef.setInput('elicitation', elicitation({ status: 'accepted', closedAt: new Date().toISOString() }));
    fixture.componentRef.setInput('canAnswer', true);
    fixture.detectChanges();

    expect(element.querySelector('form')).toBeNull();
    expect(element.querySelector('[data-outcome]')!.textContent).toContain('Orders received the answer.');
    expect(element.getAttribute('data-status')).toBe('accepted');
  });
});

describe('Elicitation events', () => {
  function conversation(): Conversation {
    return {
      id: 'conversation-1',
      agentId: 'projects',
      title: null,
      status: 'running',
      createdAt: '2026-11-11T10:00:00Z',
      updatedAt: '2026-11-11T10:00:00Z',
      agentVersion: 1,
      messages: [{ id: 'assistant-1', role: 'assistant', createdAt: '2026-11-11T10:00:00Z', parts: [] }],
      pendingApproval: null
    } as unknown as Conversation;
  }

  it('are known stream events', () => {
    expect(NH_ASSISTANT_SSE_EVENT_TYPES).toContain('elicitation.requested');
    expect(NH_ASSISTANT_SSE_EVENT_TYPES).toContain('elicitation.closed');
  });

  it('show the request where its call waits and replace it once it closed', () => {
    const requested = applyNhAssistantEvent(conversation(), { type: 'elicitation.requested', data: elicitation() });
    const again = applyNhAssistantEvent(requested, { type: 'elicitation.requested', data: elicitation() });
    const closed = applyNhAssistantEvent(again, { type: 'elicitation.closed', data: elicitation({ status: 'declined' }) });

    const parts = closed.messages[closed.messages.length - 1].parts;
    expect(parts.length).toBe(1);
    expect(parts[0]).toEqual(jasmine.objectContaining({ type: 'elicitation', elicitationId: 'elicitation-1', status: 'declined' }));
    // A request for input never makes the conversation wait for an approval.
    expect(closed.status).toBe('running');
  });
});
