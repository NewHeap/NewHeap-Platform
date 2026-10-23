import { TestBed } from '@angular/core/testing';
import { Subscription } from 'rxjs';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantPushService } from './nh-assistant-push.service';

describe('NhAssistantPushService', () => {
  let push: NhAssistantPushService;
  let conversations: string[];
  let subscriptions: Subscription;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideNhAssistant({
          apiBaseUrl: '/api/assistant',
          getAccessToken: () => null,
          push: { serviceWorkerUrl: '/nh-assistant/nh-assistant-push-worker.js' }
        })
      ]
    });
    push = TestBed.inject(NhAssistantPushService);
    conversations = [];
    subscriptions = push.openConversation$.subscribe(conversationId => conversations.push(conversationId));
  });

  afterEach(() => subscriptions.unsubscribe());

  it('opens the conversation of a clicked notification about a run while the application does not ask for runs', () => {
    push.openNotification('conversation-1', 'run-1');
    push.openNotification('conversation-2');

    expect(conversations).toEqual(['conversation-1', 'conversation-2']);
  });

  it('hands the run of a clicked notification to openRun$ while the application subscribes, and the conversation otherwise', () => {
    const runs: string[] = [];
    const runSubscription = push.openRun$.subscribe(runId => runs.push(runId));

    push.openNotification('conversation-1', 'run-1');
    // A notification without a run, such as a finished turn, still opens its conversation.
    push.openNotification('conversation-2');
    runSubscription.unsubscribe();
    push.openNotification('conversation-3', 'run-3');

    expect(runs).toEqual(['run-1']);
    expect(conversations).toEqual(['conversation-2', 'conversation-3']);
  });

  it('forwards the run of a click that the bundled worker posts', () => {
    if (!('serviceWorker' in navigator) || !globalThis.isSecureContext || !('PushManager' in globalThis) || !('Notification' in globalThis)) {
      pending('This browser offers no Web Push.');
      return;
    }
    const runs: string[] = [];
    subscriptions.add(push.openRun$.subscribe(runId => runs.push(runId)));

    navigator.serviceWorker.dispatchEvent(new MessageEvent('message', {
      data: { type: 'nh-assistant.open-conversation', conversationId: 'conversation-1', runId: 'run-1' }
    }));
    navigator.serviceWorker.dispatchEvent(new MessageEvent('message', {
      data: { type: 'nh-assistant.open-conversation', conversationId: 'conversation-2' }
    }));

    expect(runs).toEqual(['run-1']);
    expect(conversations).toEqual(['conversation-2']);
  });
});
