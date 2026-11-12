import { Injectable, inject } from '@angular/core';
import { Observable, Subscriber } from 'rxjs';
import {
  AssistantPreferences,
  McpAuthorizationStart,
  McpAuthorizeRequest,
  McpCompleteRequest,
  McpCompletion,
  McpConnection,
  UpdateMcpConnectionRequest
} from '../models/assistant-admin.models';
import { AgentTriggerOutcome, AgentTriggerReceipt } from '../models/assistant-agent-run.models';
import { Alert, AlertDismissOptions, AlertList, AlertListFilter, AlertSummary, AlertSummaryFilter } from '../models/assistant-alert.models';
import {
  AgentSummary,
  AnswerElicitationRequest,
  AnswerQuestionRequest,
  ClientToolResultRequest,
  AssistantStatus,
  Conversation,
  ConversationPage,
  ConversationSubjectRef,
  CreateConversationRequest,
  DecideApprovalRequest,
  DirectoryEntry,
  FileReference,
  NotificationSettings,
  PushSubscriptionRequest,
  ResolveApprovalRequest,
  SendMessageRequest,
  ShareLink,
  UpdateNotificationSettingsRequest
} from '../models/assistant-api.models';
import {
  NH_ASSISTANT_SSE_EVENT_TYPES,
  NhAssistantClientErrorCodes,
  NhAssistantMessageEvent,
  NhAssistantSseEvent,
  NhAssistantSseEventType
} from '../models/assistant-sse.models';
import { NhAssistantSseParser } from './nh-assistant-sse-parser';
import {
  NhAssistantTransport,
  nhAssistantErrorMessageKey,
  nhAssistantIsSafeCode,
  nhAssistantWithQuery as withQuery
} from './nh-assistant-transport';

export {
  NH_ASSISTANT_UPLOAD_HEADER,
  NhAssistantApiError,
  nhAssistantCodeForStatus,
  nhAssistantErrorMessageKey
} from './nh-assistant-transport';

const knownEventTypes = new Set<string>(NH_ASSISTANT_SSE_EVENT_TYPES);

/** The outcomes of a trigger receipt with which the next run takes a waiting message. */
const queuedOutcomes = new Set<string>(['started', 'queued', 'coalesced', 'superseded', 'waiting', 'deferred'] satisfies AgentTriggerOutcome[]);

/** The progress of one upload, then the file the application stored. */
export type NhAssistantUploadEvent =
  | { type: 'progress'; loaded: number; total: number }
  | { type: 'uploaded'; file: FileReference };

/**
 * Client for the assistant HTTP API. JSON endpoints return cold observables that abort
 * their request on unsubscribe. Streaming endpoints emit every contract event and complete
 * after `turn.completed` or `error`; transport failures become an `error` event with a
 * client failure code instead of an observable error.
 */
@Injectable()
export class NhAssistantApiService {
  private readonly transport = inject(NhAssistantTransport);

  status(): Observable<AssistantStatus> {
    return this.transport.json<AssistantStatus>('GET', 'status');
  }

  /**
   * The agents the caller may chat with (`GET agents`). With `subject`, the agents of a chat about that
   * subject: those that work on it, also those offered only for chats about a subject. Fails with
   * `assistant-subject-access-denied` or `assistant-subject-not-found` when the caller may not use a chat
   * about it.
   */
  agents(subject?: ConversationSubjectRef | null): Observable<AgentSummary[]> {
    if (!subject) {
      return this.transport.json<AgentSummary[]>('GET', 'agents');
    }

    const query = `subjectType=${encodeURIComponent(subject.subjectType)}&subjectId=${encodeURIComponent(subject.subjectId)}`;
    return this.transport.json<AgentSummary[]>('GET', `agents?${query}`);
  }

  listConversations(page = 1, itemsPerPage = 20): Observable<ConversationPage> {
    const query = `page=${encodeURIComponent(page)}&itemsPerPage=${encodeURIComponent(itemsPerPage)}`;
    return this.transport.json<ConversationPage>('GET', `conversations?${query}`);
  }

  createConversation(request: CreateConversationRequest): Observable<Conversation> {
    return this.transport.json<Conversation>('POST', 'conversations', request);
  }

  getConversation(conversationId: string): Observable<Conversation> {
    return this.transport.json<Conversation>('GET', `conversations/${encodeURIComponent(conversationId)}`);
  }

  deleteConversation(conversationId: string): Observable<void> {
    return this.transport.json<void>('DELETE', `conversations/${encodeURIComponent(conversationId)}`);
  }

  sendMessage(conversationId: string, request: SendMessageRequest): Observable<NhAssistantMessageEvent> {
    return this.requestStream(`conversations/${encodeURIComponent(conversationId)}/messages`, request, true);
  }

  /**
   * Uploads one file to a conversation as `multipart/form-data` (`POST conversations/{id}/files`):
   * emits the progress while the file is sent and then the stored file, whose reference a message
   * carries in `files`. Fails with `NhAssistantApiError`; unsubscribing aborts the upload. The
   * media type of `file` is the one the upload declares.
   */
  uploadFile(conversationId: string, file: Blob, name: string): Observable<NhAssistantUploadEvent> {
    return new Observable<NhAssistantUploadEvent>(subscriber => {
      const abort = new AbortController();
      const form = new FormData();
      form.append('file', file, name);

      this.transport.upload<FileReference>(
        `conversations/${encodeURIComponent(conversationId)}/files`,
        form,
        (loaded, total) => subscriber.next({ type: 'progress', loaded, total }),
        abort.signal
      ).then(
        reference => {
          subscriber.next({ type: 'uploaded', file: reference });
          subscriber.complete();
        },
        error => {
          if (!abort.signal.aborted) {
            subscriber.error(error);
          }
        }
      );

      return () => abort.abort();
    });
  }

  decideApproval(conversationId: string, approvalId: string, request: DecideApprovalRequest): Observable<NhAssistantSseEvent> {
    const path = `conversations/${encodeURIComponent(conversationId)}/approvals/${encodeURIComponent(approvalId)}/decide`;
    return this.requestStream(path, request, false);
  }

  /**
   * Answers the question the chat's waiting turn asked with `ask_question` with the index of one of
   * its choices, and streams the resumed turn like a decision. Only the question's addressee answers;
   * the server refuses others with `assistant-question-forbidden` and a question whose wait ended with
   * `assistant-question-expired`.
   */
  answerQuestion(conversationId: string, questionId: string, request: AnswerQuestionRequest): Observable<NhAssistantSseEvent> {
    const path = `conversations/${encodeURIComponent(conversationId)}/questions/${encodeURIComponent(questionId)}/answer`;
    return this.requestStream(path, request, false);
  }

  /**
   * Reports what the browser's handler of a client tool did and streams the resumed turn: the report is
   * the result of the agent's call. Only the person whose turn called the tool reports; the server refuses
   * others with `assistant-client-tool-forbidden`, a call that no longer waits with
   * `assistant-client-tool-not-pending` and one whose wait ended with `assistant-client-tool-expired`.
   */
  reportClientTool(conversationId: string, invocationId: string, request: ClientToolResultRequest): Observable<NhAssistantSseEvent> {
    const path = `conversations/${encodeURIComponent(conversationId)}/client-tools/${encodeURIComponent(invocationId)}/result`;
    return this.requestStream(path, request, false);
  }

  /**
   * Answers the request for input of an MCP server whose tool the caller's running turn calls
   * (`POST conversations/{id}/elicitations/{elicitationId}/answer`). The answer goes to the server
   * through the waiting tool call; the turn's own stream carries what follows. Fails with
   * `assistant-elicitation-forbidden` for anyone but the person of the turn,
   * `assistant-elicitation-answer-invalid` for values that do not fit, and
   * `assistant-elicitation-not-pending` once the request no longer waits.
   */
  answerElicitation(conversationId: string, elicitationId: string, request: AnswerElicitationRequest): Observable<void> {
    const path = `conversations/${encodeURIComponent(conversationId)}/elicitations/${encodeURIComponent(elicitationId)}/answer`;
    return this.transport.json<void>('POST', path, request);
  }

  /**
   * Resolves an uncertain hand-off of a proposal the caller approved in this chat (`canResolve`):
   * `hand-to-application` hands it to the application again, `mark-resolved` confirms its outcome.
   * Returns the conversation as it is afterwards.
   */
  resolveApproval(conversationId: string, approvalId: string, request: ResolveApprovalRequest): Observable<Conversation> {
    const path = `conversations/${encodeURIComponent(conversationId)}/approvals/${encodeURIComponent(approvalId)}/resolve`;
    return this.transport.json<Conversation>('POST', path, request);
  }

  cancel(conversationId: string): Observable<void> {
    return this.transport.json<void>('POST', `conversations/${encodeURIComponent(conversationId)}/cancel`);
  }

  /** Raises the caller's read position to `sequence`, or to the latest message when omitted. */
  markRead(conversationId: string, sequence?: number): Observable<void> {
    return this.transport.json<void>(
      'POST',
      `conversations/${encodeURIComponent(conversationId)}/read`,
      sequence === undefined ? {} : { sequence }
    );
  }

  /** Owner only: creates a new invitation link; an earlier link stops working. */
  createShareLink(conversationId: string): Observable<ShareLink> {
    return this.transport.json<ShareLink>('POST', `conversations/${encodeURIComponent(conversationId)}/share-link`);
  }

  /** Owner only: the invitation link stops working; people who joined stay. */
  revokeShareLink(conversationId: string): Observable<void> {
    return this.transport.json<void>('DELETE', `conversations/${encodeURIComponent(conversationId)}/share-link`);
  }

  /** Joins a shared conversation with the token of its invitation link. */
  joinConversation(conversationId: string, token: string): Observable<Conversation> {
    return this.transport.json<Conversation>('POST', `conversations/${encodeURIComponent(conversationId)}/join`, { token });
  }

  /** Owner only: searches the application's directory for people to invite. */
  searchParticipantCandidates(conversationId: string, query: string): Observable<DirectoryEntry[]> {
    const path = `conversations/${encodeURIComponent(conversationId)}/participant-candidates?query=${encodeURIComponent(query)}`;
    return this.transport.json<DirectoryEntry[]>('GET', path);
  }

  /** Owner only: invites a person the directory returned. */
  inviteParticipant(conversationId: string, actorId: string): Observable<void> {
    return this.transport.json<void>('POST', `conversations/${encodeURIComponent(conversationId)}/participants`, { actorId });
  }

  /** The owner removes a participant, or a participant passes their own id to leave. */
  removeParticipant(conversationId: string, actorId: string): Observable<void> {
    const path = `conversations/${encodeURIComponent(conversationId)}/participants/${encodeURIComponent(actorId)}`;
    return this.transport.json<void>('DELETE', path);
  }

  /** The caller's notification choice and the server's Web Push key. */
  getNotificationSettings(): Observable<NotificationSettings> {
    return this.transport.json<NotificationSettings>('GET', 'notifications');
  }

  /** Turns push notifications on or off for every browser of the caller. */
  updateNotificationSettings(pushEnabled: boolean): Observable<NotificationSettings> {
    return this.transport.json<NotificationSettings>('PUT', 'notifications', { pushEnabled });
  }

  /**
   * Changes the caller's notification choices (`PUT notifications`): the push switch, per alert kind
   * mute, digest times, quiet hours and channels, and the mutes of a kind about a subject to lift, each
   * only where it is named. Returns every choice as it is stored. A change the kind does not allow fails
   * with `400` and a stable code.
   */
  updateNotificationPreferences(request: UpdateNotificationSettingsRequest): Observable<NotificationSettings> {
    return this.transport.json<NotificationSettings>('PUT', 'notifications', request);
  }

  /**
   * The caller's own alerts (`GET alerts`), the most recently raised first or with `grouped` per parent,
   * with their titles and texts in the caller's language. Alerts about a subject the caller may no
   * longer read are left out. Fails with `assistant-alert-filter-invalid` for a malformed filter.
   */
  getAlerts(filter: AlertListFilter = {}): Observable<AlertList> {
    return this.transport.json<AlertList>('GET', withQuery('alerts', filter));
  }

  /** The counts of a badge (`GET alerts/summary`): the caller's unread open alerts, in total and per urgency. */
  getAlertSummary(filter: AlertSummaryFilter = {}): Observable<AlertSummary> {
    return this.transport.json<AlertSummary>('GET', withQuery('alerts/summary', filter));
  }

  /**
   * Marks the caller's alert read and returns it as the list now shows it; a delivery that still waits
   * is then not sent. Fails with `assistant-alert-not-found` for an alert the caller may not read.
   */
  markAlertRead(alertId: string): Observable<Alert> {
    return this.transport.json<Alert>('POST', `alerts/${encodeURIComponent(alertId)}/read`);
  }

  /**
   * Dismisses the caller's alert, also a closed one, and returns it as the list now shows it. With
   * `muteSubjectForDays` its kind also tells the caller nothing more about its subject for that many
   * days (FB87-11). Fails with `assistant-alert-not-found` for an alert the caller may not read, and with
   * `assistant-alert-subject-mute-not-allowed` or `assistant-alert-subject-mute-days-invalid` for a mute
   * that is refused, which dismisses nothing.
   */
  dismissAlert(alertId: string, options: AlertDismissOptions = {}): Observable<Alert> {
    const path = `alerts/${encodeURIComponent(alertId)}/dismiss`;
    return options.muteSubjectForDays === undefined
      ? this.transport.json<Alert>('POST', path)
      : this.transport.json<Alert>('POST', path, { muteSubjectForDays: options.muteSubjectForDays });
  }

  /** Registers this browser's push subscription for the caller. */
  subscribePush(subscription: PushSubscriptionRequest): Observable<void> {
    return this.transport.json<void>('PUT', 'notifications/push-subscription', subscription);
  }

  /** Removes this browser's push subscription. */
  unsubscribePush(endpoint: string): Observable<void> {
    return this.transport.json<void>('DELETE', 'notifications/push-subscription', { endpoint });
  }

  /** The caller's own assistant preferences. */
  getPreferences(): Observable<AssistantPreferences> {
    return this.transport.json<AssistantPreferences>('GET', 'preferences');
  }

  /** Replaces the caller's own assistant preferences and returns the stored values. */
  updatePreferences(preferences: AssistantPreferences): Observable<AssistantPreferences> {
    return this.transport.json<AssistantPreferences>('PUT', 'preferences', preferences);
  }

  /** MCP servers that agents may use in background runs with the caller's own account. */
  getMcpConnections(): Observable<McpConnection[]> {
    return this.transport.json<McpConnection[]>('GET', 'mcp-connections');
  }

  /** Allows or withdraws background use of an MCP server with the caller's own account. */
  updateMcpConnection(serverId: string, request: UpdateMcpConnectionRequest): Observable<McpConnection> {
    return this.transport.json<McpConnection>('PUT', `mcp-connections/${encodeURIComponent(serverId)}`, request);
  }

  /**
   * Starts the caller's authorization of a server each person connects to. Prefer
   * `NhAssistantMcpConnectService.connect`, which opens and completes it.
   */
  authorizeMcpConnection(serverId: string, request: McpAuthorizeRequest = {}): Observable<McpAuthorizationStart> {
    return this.transport.json<McpAuthorizationStart>('POST', `mcp-connections/${encodeURIComponent(serverId)}/authorize`, request);
  }

  /** Removes the caller's own connection of a server and revokes its tokens. */
  disconnectMcpConnection(serverId: string): Observable<void> {
    return this.transport.json<void>('DELETE', `mcp-connections/${encodeURIComponent(serverId)}`);
  }

  /** Activates the connection of an authorization the caller started, with the callback's completion code. */
  completeMcpAuthorization(request: McpCompleteRequest): Observable<McpCompletion> {
    return this.transport.json<McpCompletion>('POST', 'mcp-connections/complete', request);
  }

  /**
   * Streams a turn. With `queuedReceipt` a `202` answer with a trigger receipt becomes
   * `message.queued`; without it, a stream is required.
   */
  private requestStream(path: string, body: unknown, queuedReceipt: true): Observable<NhAssistantMessageEvent>;
  private requestStream(path: string, body: unknown, queuedReceipt: false): Observable<NhAssistantSseEvent>;
  private requestStream(path: string, body: unknown, queuedReceipt: boolean): Observable<NhAssistantMessageEvent> {
    return new Observable<NhAssistantMessageEvent>(subscriber => {
      const abort = new AbortController();

      void this.executeStream(path, body, queuedReceipt, abort.signal, subscriber);

      return () => abort.abort();
    });
  }

  private async executeStream(
    path: string,
    body: unknown,
    queuedReceipt: boolean,
    signal: AbortSignal,
    subscriber: Subscriber<NhAssistantMessageEvent>
  ): Promise<void> {
    let response: Response;
    try {
      response = await this.transport.fetch('POST', path, 'text/event-stream', body, signal);
    } catch {
      this.emitClientError(subscriber, signal, NhAssistantClientErrorCodes.network);
      return;
    }

    if (!response.ok) {
      this.emitClientError(subscriber, signal, await this.transport.failureCode(response));
      return;
    }
    if (queuedReceipt && response.status === 202) {
      await this.readQueuedReceipt(response, signal, subscriber);
      return;
    }

    const contentType = response.headers.get('content-type') ?? '';
    if (!response.body || !contentType.toLowerCase().startsWith('text/event-stream')) {
      this.emitClientError(subscriber, signal, NhAssistantClientErrorCodes.invalidResponse);
      return;
    }

    await this.readStream(response.body, signal, subscriber);
  }

  /**
   * A message to an agent's thread whose run works waits for the next run: the server answers `202`
   * with the trigger receipt instead of a stream. A receipt the run takes the message with becomes
   * `message.queued`; one that refused it becomes an `error` with its code.
   */
  private async readQueuedReceipt(
    response: Response,
    signal: AbortSignal,
    subscriber: Subscriber<NhAssistantMessageEvent>
  ): Promise<void> {
    let receipt: AgentTriggerReceipt | null;
    try {
      receipt = JSON.parse(await response.text()) as AgentTriggerReceipt | null;
    } catch {
      receipt = null;
    }
    if (signal.aborted || subscriber.closed) {
      return;
    }
    if (!receipt || typeof receipt.outcome !== 'string') {
      this.emitClientError(subscriber, signal, NhAssistantClientErrorCodes.invalidResponse);
      return;
    }
    if (!queuedOutcomes.has(receipt.outcome)) {
      this.emitClientError(
        subscriber,
        signal,
        nhAssistantIsSafeCode(receipt.reasonCode) ? receipt.reasonCode : NhAssistantClientErrorCodes.conversationBusy);
      return;
    }

    subscriber.next({ type: 'message.queued', data: receipt });
    subscriber.complete();
  }

  private async readStream(
    body: ReadableStream<Uint8Array>,
    signal: AbortSignal,
    subscriber: Subscriber<NhAssistantMessageEvent>
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

  private emitClientError(subscriber: Subscriber<NhAssistantMessageEvent>, signal: AbortSignal, code: string): void {
    if (signal.aborted || subscriber.closed) {
      return;
    }

    subscriber.next({ type: 'error', data: { code, messageKey: nhAssistantErrorMessageKey(code) } });
    subscriber.complete();
  }
}
