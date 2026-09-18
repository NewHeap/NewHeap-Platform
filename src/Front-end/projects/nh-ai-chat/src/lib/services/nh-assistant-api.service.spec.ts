import { TestBed } from '@angular/core/testing';
import { firstValueFrom, toArray } from 'rxjs';
import { NhAssistantSseEvent } from '../models/assistant-sse.models';
import { NH_ASSISTANT_FETCH, NhAssistantFetch } from '../nh-assistant.config';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantApiError, NhAssistantApiService } from './nh-assistant-api.service';

function sseResponse(chunks: string[], status = 200): Response {
  const encoder = new TextEncoder();
  const body = new ReadableStream<Uint8Array>({
    start(controller) {
      for (const chunk of chunks) {
        controller.enqueue(encoder.encode(chunk));
      }
      controller.close();
    }
  });

  return new Response(body, { status, headers: { 'content-type': 'text/event-stream; charset=utf-8' } });
}

function jsonResponse(value: unknown, status = 200): Response {
  return new Response(JSON.stringify(value), { status, headers: { 'content-type': 'application/json' } });
}

describe('NhAssistantApiService', () => {
  let fetchSpy: jasmine.Spy<NhAssistantFetch>;
  let token: string | null;

  function setup(): NhAssistantApiService {
    TestBed.configureTestingModule({
      providers: [
        provideNhAssistant({ apiBaseUrl: '/api/assistant/', getAccessToken: () => Promise.resolve(token) }),
        { provide: NH_ASSISTANT_FETCH, useFactory: () => fetchSpy }
      ]
    });

    return TestBed.inject(NhAssistantApiService);
  }

  beforeEach(() => {
    token = 'token-1';
    fetchSpy = jasmine.createSpy<NhAssistantFetch>('fetch');
  });

  it('streams messages with the bearer token and the event-stream Accept header', async () => {
    fetchSpy.and.returnValue(Promise.resolve(sseResponse([
      'event: turn.started\ndata: {"turnId":"t1","userMessageId":"u1","assistantMessageId":"a1"}\n\n',
      'event: turn.completed\ndata: {"turnId":"t1","status":"completed","usage":{"inputTokens":1,"outputTokens":2,"toolCalls":0},"errorCode":null}\n\n'
    ])));
    const service = setup();

    await firstValueFrom(service.sendMessage('c 1', { text: 'Hello', clientMessageId: 'client-1' }).pipe(toArray()));

    const [url, init] = fetchSpy.calls.mostRecent().args;
    const headers = init.headers as Record<string, string>;
    expect(url).toBe('/api/assistant/conversations/c%201/messages');
    expect(init.method).toBe('POST');
    expect(headers['Authorization']).toBe('Bearer token-1');
    expect(headers['Accept']).toBe('text/event-stream');
    expect(headers['Content-Type']).toBe('application/json');
    expect(JSON.parse(init.body as string)).toEqual({ text: 'Hello', clientMessageId: 'client-1' });
  });

  it('omits the Authorization header when the host has no token', async () => {
    token = null;
    fetchSpy.and.returnValue(Promise.resolve(jsonResponse({ enabled: false, agents: [], limits: { maxMessageChars: 1, maxToolCallsPerTurn: 1 } })));
    const service = setup();

    const status = await firstValueFrom(service.status());

    const headers = fetchSpy.calls.mostRecent().args[1].headers as Record<string, string>;
    expect(status.enabled).toBeFalse();
    expect(headers['Authorization']).toBeUndefined();
    expect(headers['Accept']).toBe('application/json');
  });

  it('completes the stream at turn.completed and ignores events after it', async () => {
    fetchSpy.and.returnValue(Promise.resolve(sseResponse([
      ': keep-alive\n\n',
      'event: turn.started\ndata: {"turnId":"t1","userMessageId":"u1","assistantMessageId":"a1"}\n\n',
      'event: message.delta\ndata: {"messageId":"a1","text":"Hel"}\n\nevent: message.delta\ndata: {"messageId":"a1",',
      '"text":"lo"}\n\n: keep-alive\n\n',
      'event: turn.completed\ndata: {"turnId":"t1","status":"completed","usage":{"inputTokens":1,"outputTokens":2,"toolCalls":0},"errorCode":null}\n\n',
      'event: message.delta\ndata: {"messageId":"a1","text":"late"}\n\n'
    ])));
    const service = setup();

    const events = await firstValueFrom(service.sendMessage('c1', { text: 'Hi', clientMessageId: 'x' }).pipe(toArray()));

    expect(events.map(event => event.type)).toEqual(['turn.started', 'message.delta', 'message.delta', 'turn.completed']);
    expect(events[2]).toEqual({ type: 'message.delta', data: { messageId: 'a1', text: 'lo' } });
  });

  it('completes the stream at an error event and keeps its code', async () => {
    fetchSpy.and.returnValue(Promise.resolve(sseResponse([
      'event: error\ndata: {"code":"assistant-budget-exceeded","messageKey":"nh-assistant.errors.assistant-budget-exceeded"}\n\n',
      'event: turn.completed\ndata: {}\n\n'
    ])));
    const service = setup();

    const events = await firstValueFrom(service.sendMessage('c1', { text: 'Hi', clientMessageId: 'x' }).pipe(toArray()));

    expect(events).toEqual([{
      type: 'error',
      data: { code: 'assistant-budget-exceeded', messageKey: 'nh-assistant.errors.assistant-budget-exceeded' }
    }]);
  });

  it('turns a 409 response into a conversation-busy error event', async () => {
    fetchSpy.and.returnValue(Promise.resolve(new Response('busy', { status: 409 })));
    const service = setup();

    const events = await firstValueFrom(service.sendMessage('c1', { text: 'Hi', clientMessageId: 'x' }).pipe(toArray()));

    expect(events).toEqual([{
      type: 'error',
      data: { code: 'assistant-conversation-busy', messageKey: 'nh-assistant.errors.assistant-conversation-busy' }
    }]);
  });

  it('turns a network failure into a network error event', async () => {
    fetchSpy.and.returnValue(Promise.reject(new TypeError('Failed to fetch')));
    const service = setup();

    const events = await firstValueFrom(service.decideApproval('c1', 'a1', {
      decision: 'approve',
      expectedProposalHash: 'hash'
    }).pipe(toArray()));

    expect(events.map(event => event.data)).toEqual([{ code: 'assistant-network', messageKey: 'nh-assistant.errors.assistant-network' }]);
  });

  it('reports a stream that ends before a terminal event as interrupted', async () => {
    fetchSpy.and.returnValue(Promise.resolve(sseResponse([
      'event: turn.started\ndata: {"turnId":"t1","userMessageId":"u1","assistantMessageId":"a1"}\n\n',
      'event: message.delta\ndata: {"messageId":"a1","text":"cut'
    ])));
    const service = setup();

    const events = await firstValueFrom(service.sendMessage('c1', { text: 'Hi', clientMessageId: 'x' }).pipe(toArray()));

    const last = events[events.length - 1] as Extract<NhAssistantSseEvent, { type: 'error' }>;
    expect(events.length).toBe(2);
    expect(last.data.code).toBe('assistant-stream-interrupted');
  });

  it('rejects a non-stream response as invalid', async () => {
    fetchSpy.and.returnValue(Promise.resolve(jsonResponse({})));
    const service = setup();

    const events = await firstValueFrom(service.sendMessage('c1', { text: 'Hi', clientMessageId: 'x' }).pipe(toArray()));

    expect(events.map(event => event.type)).toEqual(['error']);
    expect((events[0].data as { code: string }).code).toBe('assistant-invalid-response');
  });

  it('aborts the request when the consumer unsubscribes', async () => {
    let signal: AbortSignal | undefined;
    fetchSpy.and.callFake((_url, init) => {
      signal = init.signal ?? undefined;
      return new Promise<Response>(() => undefined);
    });
    const service = setup();

    const subscription = service.sendMessage('c1', { text: 'Hi', clientMessageId: 'x' }).subscribe();
    await new Promise(resolve => setTimeout(resolve));
    subscription.unsubscribe();

    expect(signal?.aborted).toBeTrue();
  });

  it('maps a failed JSON request to an NhAssistantApiError with a stable code', async () => {
    fetchSpy.and.returnValue(Promise.resolve(new Response('<html>stack trace</html>', { status: 403 })));
    const service = setup();

    const error = await firstValueFrom(service.getConversation('c1')).catch(caught => caught as NhAssistantApiError);

    expect(error).toEqual(jasmine.any(NhAssistantApiError));
    expect((error as NhAssistantApiError).code).toBe('assistant-forbidden');
    expect((error as NhAssistantApiError).message).not.toContain('stack');
  });

  it('builds the conversation list and delete requests', async () => {
    fetchSpy.and.returnValues(
      Promise.resolve(jsonResponse({ items: [], total: 0 })),
      Promise.resolve(new Response(null, { status: 204 }))
    );
    const service = setup();

    await firstValueFrom(service.listConversations(2, 10));
    await firstValueFrom(service.deleteConversation('c1'), { defaultValue: undefined });

    expect(fetchSpy.calls.argsFor(0)[0]).toBe('/api/assistant/conversations?page=2&itemsPerPage=10');
    expect(fetchSpy.calls.argsFor(1)[0]).toBe('/api/assistant/conversations/c1');
    expect(fetchSpy.calls.argsFor(1)[1].method).toBe('DELETE');
  });
});
