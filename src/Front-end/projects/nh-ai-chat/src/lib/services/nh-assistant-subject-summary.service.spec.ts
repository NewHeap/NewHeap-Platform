import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed, fakeAsync, flushMicrotasks, tick } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NEVER, Subject, Subscription, of, throwError } from 'rxjs';
import { NhAssistantSubjectBadgeComponent } from '../components/subject-badge/nh-assistant-subject-badge.component';
import { NH_ASSISTANT_TRANSLATIONS } from '../i18n/nh-assistant-translations';
import { AgentSubjectSummary, AgentSubjectSummaryList } from '../models/assistant-agent-run.models';
import { LiveAgentWorkChanged } from '../models/assistant-live.models';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAgentRunApiService } from './nh-assistant-agent-run-api.service';
import { NhAssistantLiveService, NhAssistantLiveState } from './nh-assistant-live.service';
import { NhAssistantSubjectSummaryService } from './nh-assistant-subject-summary.service';
import { NhAssistantStore } from './nh-assistant.store';

/** A connected hub whose agent-work updates and reconnects the test sends. */
class FakeLiveService {
  readonly agentWork = new Subject<LiveAgentWorkChanged>();
  readonly resync = new Subject<void>();
  readonly changed$ = NEVER;
  readonly read$ = NEVER;
  readonly removed$ = NEVER;
  readonly events$ = NEVER;
  readonly agentWork$ = this.agentWork.asObservable();
  readonly resync$ = this.resync.asObservable();
  readonly state = signal<NhAssistantLiveState>('connected').asReadonly();
}

/** The parts of the store the summaries use: whether the assistant is available. */
class FakeStore {
  readonly enabled = signal(true);
  readonly agents = signal([]);
  initialize(): Promise<void> {
    return Promise.resolve();
  }
}

function counts(subjectId: string): AgentSubjectSummary {
  const numbers: Record<string, AgentSubjectSummary> = {
    'p-1': { subjectId, openProposals: 3, proposalsToDecide: 2, runActive: false },
    'p-2': { subjectId, openProposals: 3, proposalsToDecide: 0, runActive: true },
    'p-4': { subjectId, openProposals: 0, proposalsToDecide: 0, runActive: true },
    'p-5': { subjectId, openProposals: 120, proposalsToDecide: 0, runActive: false }
  };
  return numbers[subjectId] ?? { subjectId, openProposals: 0, proposalsToDecide: 0, runActive: false };
}

function respond(subjectType: string, subjectIds: readonly string[]): AgentSubjectSummaryList {
  return { subjectType, items: subjectIds.map(counts) };
}

function work(subjectType: string, subjectId: string): LiveAgentWorkChanged {
  return { conversationId: `thread-${subjectId}`, reason: 'proposal', approvalId: 'approval-1', runId: null, subjectType, subjectId };
}

describe('NhAssistantSubjectSummaryService', () => {
  let api: jasmine.SpyObj<NhAssistantAgentRunApiService>;
  let live: FakeLiveService;
  let store: FakeStore;
  let service: NhAssistantSubjectSummaryService;
  let subscriptions: Subscription;

  beforeEach(() => {
    live = new FakeLiveService();
    store = new FakeStore();
    api = jasmine.createSpyObj<NhAssistantAgentRunApiService>('NhAssistantAgentRunApiService', ['summarizeSubjects']);
    api.summarizeSubjects.and.callFake((subjectType, subjectIds) => of(respond(subjectType, subjectIds)));
    TestBed.configureTestingModule({
      providers: [
        provideTranslateService(),
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token', translations: 'host' }),
        { provide: NhAssistantAgentRunApiService, useValue: api },
        { provide: NhAssistantLiveService, useValue: live },
        { provide: NhAssistantStore, useValue: store }
      ]
    });
    service = TestBed.inject(NhAssistantSubjectSummaryService);
    subscriptions = new Subscription();
  });

  afterEach(() => subscriptions.unsubscribe());

  function show(subjectType: string, subjectId: string): (AgentSubjectSummary | null)[] {
    const values: (AgentSubjectSummary | null)[] = [];
    subscriptions.add(service.summary(subjectType, subjectId).subscribe(value => values.push(value)));
    return values;
  }

  it('asks for every summary of one tick in one request per subject type', fakeAsync(() => {
    const first = show('project', 'p-1');
    const second = show('project', 'p-2');
    show('task', 't-1');
    expect(api.summarizeSubjects).not.toHaveBeenCalled();

    flushMicrotasks();

    expect(api.summarizeSubjects.calls.allArgs()).toEqual([
      ['project', ['p-1', 'p-2']],
      ['task', ['t-1']]
    ]);
    expect(first).toEqual([null, counts('p-1')]);
    expect(second).toEqual([null, counts('p-2')]);
  }));

  it('splits more than 200 subjects into requests of at most 200', fakeAsync(() => {
    for (let index = 0; index < 450; index++) {
      show('project', `p-${index}`);
    }

    flushMicrotasks();

    expect(api.summarizeSubjects.calls.allArgs().map(([type, ids]) => [type, ids.length])).toEqual([
      ['project', 200],
      ['project', 200],
      ['project', 50]
    ]);
  }));

  it('shares the cached summary of a subject with everyone who shows it', fakeAsync(() => {
    show('project', 'p-1');
    flushMicrotasks();

    const later = show('project', 'p-1');
    flushMicrotasks();

    expect(api.summarizeSubjects).toHaveBeenCalledTimes(1);
    expect(later).toEqual([counts('p-1')]);
  }));

  it('refetches only the shown subjects that live updates name, once per burst', fakeAsync(() => {
    show('project', 'p-1');
    show('project', 'p-2');
    flushMicrotasks();
    api.summarizeSubjects.calls.reset();

    live.agentWork.next(work('project', 'p-1'));
    live.agentWork.next(work('project', 'p-1'));
    live.agentWork.next(work('project', 'p-9'));
    live.agentWork.next(work('task', 'p-2'));
    tick(299);
    expect(api.summarizeSubjects).not.toHaveBeenCalled();

    tick(1);
    flushMicrotasks();

    expect(api.summarizeSubjects.calls.allArgs()).toEqual([['project', ['p-1']]]);
  }));

  it('refetches every shown subject after a reconnect and forgets the others', fakeAsync(() => {
    show('project', 'p-1');
    const hidden = service.summary('project', 'p-2').subscribe();
    flushMicrotasks();
    hidden.unsubscribe();
    api.summarizeSubjects.calls.reset();

    live.resync.next();
    flushMicrotasks();
    expect(api.summarizeSubjects.calls.allArgs()).toEqual([['project', ['p-1']]]);

    show('project', 'p-2');
    flushMicrotasks();
    expect(api.summarizeSubjects.calls.mostRecent().args).toEqual(['project', ['p-2']]);
  }));

  it('waits while the assistant is unavailable and clears the counts when it becomes unavailable', fakeAsync(() => {
    store.enabled.set(false);
    const values = show('project', 'p-1');
    TestBed.tick();
    flushMicrotasks();
    expect(api.summarizeSubjects).not.toHaveBeenCalled();

    store.enabled.set(true);
    TestBed.tick();
    flushMicrotasks();
    expect(api.summarizeSubjects).toHaveBeenCalledOnceWith('project', ['p-1']);
    expect(values).toEqual([null, counts('p-1')]);

    store.enabled.set(false);
    TestBed.tick();
    expect(values).toEqual([null, counts('p-1'), null]);
  }));

  it('shows no count after a failed request and tries again for the next subscriber', fakeAsync(() => {
    api.summarizeSubjects.and.returnValue(throwError(() => new Error('offline')));
    const values = show('project', 'p-1');
    flushMicrotasks();
    expect(values).toEqual([null]);

    api.summarizeSubjects.and.callFake((subjectType, subjectIds) => of(respond(subjectType, subjectIds)));
    show('project', 'p-1');
    flushMicrotasks();

    expect(api.summarizeSubjects).toHaveBeenCalledTimes(2);
    expect(values).toEqual([null, counts('p-1')]);
  }));
});

@Component({
  standalone: true,
  imports: [NhAssistantSubjectBadgeComponent],
  template: `
    @for (id of ids(); track id) {
      <nh-assistant-subject-badge subjectType="project" [subjectId]="id" [attr.data-subject]="id" />
    }
  `
})
class BadgesHostComponent {
  readonly ids = signal(['p-1', 'p-2', 'p-3', 'p-4', 'p-5']);
}

describe('nh-assistant-subject-badge', () => {
  let api: jasmine.SpyObj<NhAssistantAgentRunApiService>;
  let fixture: ComponentFixture<BadgesHostComponent>;

  beforeEach(() => {
    api = jasmine.createSpyObj<NhAssistantAgentRunApiService>('NhAssistantAgentRunApiService', ['summarizeSubjects']);
    api.summarizeSubjects.and.callFake((subjectType, subjectIds) => of(respond(subjectType, subjectIds)));
    TestBed.configureTestingModule({
      providers: [
        provideTranslateService(),
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token', translations: 'host' }),
        { provide: NhAssistantAgentRunApiService, useValue: api },
        { provide: NhAssistantLiveService, useValue: new FakeLiveService() },
        { provide: NhAssistantStore, useValue: new FakeStore() }
      ]
    });
    const translate = TestBed.inject(TranslateService);
    translate.setTranslation('en', NH_ASSISTANT_TRANSLATIONS.en);
    translate.use('en');
    fixture = TestBed.createComponent(BadgesHostComponent);
  });

  function badge(subjectId: string): HTMLElement | null {
    return (fixture.nativeElement as HTMLElement).querySelector(`[data-subject="${subjectId}"] .subject-badge`);
  }

  it('asks for the badges of one page in one request', fakeAsync(() => {
    fixture.detectChanges();
    flushMicrotasks();

    expect(api.summarizeSubjects).toHaveBeenCalledOnceWith('project', ['p-1', 'p-2', 'p-3', 'p-4', 'p-5']);
  }));

  it('shows the proposals to decide, otherwise the open ones, an agent at work, and nothing without work', fakeAsync(() => {
    fixture.detectChanges();
    flushMicrotasks();
    fixture.detectChanges();

    const decide = badge('p-1')!;
    expect(decide.dataset['state']).toBe('decide');
    expect(decide.querySelector('.count')?.textContent?.trim()).toBe('2');
    expect(decide.querySelector('.working')).toBeNull();
    expect(decide.title).toBe('Proposals to decide: 2 of 3 open.');
    expect(decide.querySelector('.visually-hidden')?.textContent?.trim()).toBe('Proposals to decide: 2 of 3 open.');

    const open = badge('p-2')!;
    expect(open.dataset['state']).toBe('open');
    expect(open.querySelector('.count')?.textContent?.trim()).toBe('3');
    expect(open.querySelector('.working')).not.toBeNull();
    expect(open.title).toBe('Open proposals: 3. An agent is working on this.');

    expect(badge('p-3')).toBeNull();

    const working = badge('p-4')!;
    expect(working.dataset['state']).toBe('working');
    expect(working.querySelector('.count')).toBeNull();
    expect(working.title).toBe('An agent is working on this.');

    expect(badge('p-5')!.querySelector('.count')?.textContent?.trim()).toBe('99+');
  }));
});
