import { Injectable, inject } from '@angular/core';
import { TranslateService } from '@ngx-translate/core';
import { Observable, Subscriber } from 'rxjs';
import {
  AgentSummary,
  AssistantStatus,
  Conversation,
  ConversationPage,
  CreateConversationRequest,
  DecideApprovalRequest,
  SendMessageRequest
} from '../models/assistant-api.models';
import {
  NH_ASSISTANT_SSE_EVENT_TYPES,
  NhAssistantClientErrorCodes,
  NhAssistantSseEvent,
  NhAssistantSseEventType
} from '../models/assistant-sse.models';
import { NH_ASSISTANT_CONFIG, NH_ASSISTANT_FETCH } from '../nh-assistant.config';
import { NhAssistantSseParser } from './nh-assistant-sse-parser';

/** A failed JSON request of the assistant API. Carries a stable code, never response text. */
export class NhAssistantApiError extends Error {
  constructor(
    readonly status: number,
    readonly code: string,
    readonly messageKey: string
  ) {
    super(code);
    this.name = 'NhAssistantApiError';
  }
}

/** Translation key that belongs to a client-side failure code. */
export function nhAssistantErrorMessageKey(code: string): string {
  return `nh-assistant.errors.${code}`;
}

/** Maps an HTTP status of the assistant API to a stable client failure code. */
export function nhAssistantCodeForStatus(status: number): string {
  if (status === 0) {
    return NhAssistantClientErrorCodes.network;
  }
  if (status === 401) {
    return NhAssistantClientErrorCodes.unauthenticated;
  }
  if (status === 403) {
    return NhAssistantClientErrorCodes.forbidden;
  }
  if (status === 404) {
    return NhAssistantClientErrorCodes.notFound;
  }
  if (status === 409) {
    return NhAssistantClientErrorCodes.conversationBusy;
  }

  return NhAssistantClientErrorCodes.server;
}

const knownEventTypes = new Set<string>(NH_ASSISTANT_SSE_EVENT_TYPES);

/**
 * Client for the assistant HTTP API. JSON endpoints return cold observables that abort
 * their request on unsubscribe. Streaming endpoints emit every contract event and complete
 * after `turn.completed` or `error`; transport failures become an `error` event with a
 * client failure code instead of an observable error.
 */
@Injectable()
export class NhAssistantApiService {
  private readonly config = inject(NH_ASSISTANT_CONFIG);
  private readonly fetchFn = inject(NH_ASSISTANT_FETCH);
  private readonly translate = inject(TranslateService, { optional: true });

  status(): Observable<AssistantStatus> {
    return this.requestJson<AssistantStatus>('GET', 'status');
  }

  agents(): Observable<AgentSummary[]> {
    return this.requestJson<AgentSummary[]>('GET', 'agents');
  }

  listConversations(page = 1, itemsPerPage = 20): Observable<ConversationPage> {
    const query = `page=${encodeURIComponent(page)}&itemsPerPage=${encodeURIComponent(itemsPerPage)}`;
    return this.requestJson<ConversationPage>('GET', `conversations?${query}`);
  }

  createConversation(request: CreateConversationRequest): Observable<Conversation> {
    return this.requestJson<Conversation>('POST', 'conversations', request);
  }

  getConversation(conversationId: string): Observable<Conversation> {
    return this.requestJson<Conversation>('GET', `conversations/${encodeURIComponent(conversationId)}`);
  }

  deleteConversation(conversationId: string): Observable<void> {
    return this.requestJson<void>('DELETE', `conversations/${encodeURIComponent(conversationId)}`);
  }

  sendMessage(conversationId: string, request: SendMessageRequest): Observable<NhAssistantSseEvent> {
    return this.requestStream(`conversations/${encodeURIComponent(conversationId)}/messages`, request);
  }

  decideApproval(conversationId: string, approvalId: string, request: DecideApprovalRequest): Observable<NhAssistantSseEvent> {
    const path = `conversations/${encodeURIComponent(conversationId)}/approvals/${encodeURIComponent(approvalId)}/decide`;
    return this.requestStream(path, request);
  }

  cancel(conversationId: string): Observable<void> {
    return this.requestJson<void>('POST', `conversations/${encodeURIComponent(conversationId)}/cancel`);
  }

  private requestJson<T>(method: string, path: string, body?: unknown): Observable<T> {
    return new Observable<T>(subscriber => {
      const abort = new AbortController();

      void this.executeJson<T>(method, path, body, abort.signal, subscriber);

      return () => abort.abort();
    });
  }

  private async executeJson<T>(
    method: string,
    path: string,
    body: unknown,
    signal: AbortSignal,
    subscriber: Subscriber<T>
  ): Promise<void> {
    let response: Response;
    try {
      const init = await this.createInit(method, 'application/json', body, signal);
      response = await this.fetchFn(this.url(path), init);
    } catch {
      if (!signal.aborted) {
        subscriber.error(this.createError(0));
      }
      return;
    }

    if (!response.ok) {
      subscriber.error(this.createError(response.status));
      return;
    }

    try {
      const text = response.status === 204 ? '' : await response.text();
      subscriber.next((text.length > 0 ? JSON.parse(text) : undefined) as T);
      subscriber.complete();
    } catch {
      if (!signal.aborted) {
        subscriber.error(new NhAssistantApiError(
          response.status,
          NhAssistantClientErrorCodes.invalidResponse,
          nhAssistantErrorMessageKey(NhAssistantClientErrorCodes.invalidResponse)
        ));
      }
    }
  }

  private requestStream(path: string, body: unknown): Observable<NhAssistantSseEvent> {
    return new Observable<NhAssistantSseEvent>(subscriber => {
      const abort = new AbortController();

      void this.executeStream(path, body, abort.signal, subscriber);

      return () => abort.abort();
    });
  }

  private async executeStream(
    path: string,
    body: unknown,
    signal: AbortSignal,
    subscriber: Subscriber<NhAssistantSseEvent>
  ): Promise<void> {
    let response: Response;
    try {
      const init = await this.createInit('POST', 'text/event-stream', body, signal);
      response = await this.fetchFn(this.url(path), init);
    } catch {
      this.emitClientError(subscriber, signal, NhAssistantClientErrorCodes.network);
      return;
    }

    if (!response.ok) {
      this.emitClientError(subscriber, signal, nhAssistantCodeForStatus(response.status));
      return;
    }

    const contentType = response.headers.get('content-type') ?? '';
    if (!response.body || !contentType.toLowerCase().startsWith('text/event-stream')) {
      this.emitClientError(subscriber, signal, NhAssistantClientErrorCodes.invalidResponse);
      return;
    }

    await this.readStream(response.body, signal, subscriber);
  }

  private async readStream(
    body: ReadableStream<Uint8Array>,
    signal: AbortSignal,
    subscriber: Subscriber<NhAssistantSseEvent>
  ): Promise<void> {
    const reader = body.getReader();
    const decoder = new TextDecoder();
    const parser = new NhAssistantSseParser();

    try {
      while (true) {
        const { done, value } = await reader.read();
        const text = done ? decoder.decode() : decoder.decode(value, { stream: true });
        const messages = done ? [...parser.push(text), ...parser.end()] : parser.push(text);

        for (const message of messages) {
          if (!knownEventTypes.has(message.event)) {
            continue;
          }

          let data: unknown;
          try {
            data = JSON.parse(message.data);
          } catch {
            this.emitClientError(subscriber, signal, NhAssistantClientErrorCodes.invalidResponse);
            return;
          }

          const event = { type: message.event as NhAssistantSseEventType, data } as NhAssistantSseEvent;
          subscriber.next(event);

          if (event.type === 'turn.completed' || event.type === 'error') {
            subscriber.complete();
            return;
          }
        }

        if (done) {
          this.emitClientError(subscriber, signal, NhAssistantClientErrorCodes.streamInterrupted);
          return;
        }
      }
    } catch {
      this.emitClientError(subscriber, signal, NhAssistantClientErrorCodes.streamInterrupted);
    } finally {
      // Releases the connection once a terminal event arrived or the consumer unsubscribed.
      reader.cancel().catch(() => undefined);
    }
  }

  private emitClientError(subscriber: Subscriber<NhAssistantSseEvent>, signal: AbortSignal, code: string): void {
    if (signal.aborted || subscriber.closed) {
      return;
    }

    subscriber.next({ type: 'error', data: { code, messageKey: nhAssistantErrorMessageKey(code) } });
    subscriber.complete();
  }

  private createError(status: number): NhAssistantApiError {
    const code = nhAssistantCodeForStatus(status);
    return new NhAssistantApiError(status, code, nhAssistantErrorMessageKey(code));
  }

  private async createInit(method: string, accept: string, body: unknown, signal: AbortSignal): Promise<RequestInit> {
    const headers: Record<string, string> = { Accept: accept };

    const token = await this.config.getAccessToken();
    if (token) {
      headers['Authorization'] = `Bearer ${token}`;
    }

    const language = this.translate?.getCurrentLang();
    if (language) {
      headers['Accept-Language'] = language;
    }

    if (body !== undefined) {
      headers['Content-Type'] = 'application/json';
    }

    return {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
      signal,
      cache: 'no-store'
    };
  }

  private url(path: string): string {
    const base = this.config.apiBaseUrl.replace(/\/+$/, '');
    return `${base}/${path}`;
  }
}
