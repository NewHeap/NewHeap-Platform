import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';
import {
  LiveAgentWorkChanged,
  LiveConversationChanged,
  LiveConversationEvent,
  LiveConversationRead,
  LiveConversationRemoved,
  NhAssistantLiveService,
  NhAssistantLiveState
} from '@newheap/platform-ai-chat';
import { Observable, Subject, Subscription } from 'rxjs';
import { NhAssistantMockBackend } from './nh-assistant-mock-backend';

type NhAssistantLiveSurface = Pick<
  NhAssistantLiveService,
  'changed$' | 'read$' | 'removed$' | 'events$' | 'agentWork$' | 'resync$' | 'state' | 'connect' | 'stop' | 'watchAgentWork'
>;

/**
 * Stands in for the SignalR live service: it delivers the mock back-end's live updates in
 * memory, so shared conversations, other tabs and parallel turns behave as with a real hub. The work
 * of agents for the whole application arrives only while a screen watches it, as on the hub.
 */
@Injectable()
export class NhAssistantMockLiveService implements NhAssistantLiveSurface {
  private readonly backend = inject(NhAssistantMockBackend);
  private readonly changedSubject = new Subject<LiveConversationChanged>();
  private readonly readSubject = new Subject<LiveConversationRead>();
  private readonly removedSubject = new Subject<LiveConversationRemoved>();
  private readonly eventSubject = new Subject<LiveConversationEvent>();
  private readonly agentWorkSubject = new Subject<LiveAgentWorkChanged>();
  private readonly resyncSubject = new Subject<void>();
  private readonly watchers = signal(0);
  private readonly stateSignal = signal<NhAssistantLiveState>('off');
  private subscription?: Subscription;

  readonly changed$: Observable<LiveConversationChanged> = this.changedSubject.asObservable();
  readonly read$: Observable<LiveConversationRead> = this.readSubject.asObservable();
  readonly removed$: Observable<LiveConversationRemoved> = this.removedSubject.asObservable();
  readonly events$: Observable<LiveConversationEvent> = this.eventSubject.asObservable();
  /** The work of agents that changed: runs by hand, decisions and `NhAssistantMockBackend.publishAgentWork`. */
  readonly agentWork$: Observable<LiveAgentWorkChanged> = this.agentWorkSubject.asObservable();
  /** Whether a screen watches the work of agents for the whole application. */
  readonly watching = computed(() => this.watchers() > 0);
  readonly resync$: Observable<void> = this.resyncSubject.asObservable();
  readonly state = this.stateSignal.asReadonly();

  constructor() {
    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());
  }

  async connect(hubPath: string | null): Promise<void> {
    await this.stop();
    if (!hubPath) {
      return;
    }
    this.subscription = this.backend.live$.subscribe(message => {
      switch (message.kind) {
        case 'changed':
          this.changedSubject.next(message.data);
          break;
        case 'read':
          this.readSubject.next(message.data);
          break;
        case 'removed':
          this.removedSubject.next(message.data);
          break;
        case 'event':
          this.eventSubject.next(message.data);
          break;
        case 'agent-work':
          if (!message.wholeApplication || this.watchers() > 0) {
            this.agentWorkSubject.next(message.data);
          }
          break;
      }
    });
    this.stateSignal.set('connected');
    this.resyncSubject.next();
  }

  /** Follows the work of agents for the whole application until the returned function is called. */
  watchAgentWork(): () => void {
    this.watchers.update(count => count + 1);
    let released = false;
    return () => {
      if (!released) {
        released = true;
        this.watchers.update(count => Math.max(0, count - 1));
      }
    };
  }

  async stop(): Promise<void> {
    this.subscription?.unsubscribe();
    this.subscription = undefined;
    this.stateSignal.set('off');
  }
}
