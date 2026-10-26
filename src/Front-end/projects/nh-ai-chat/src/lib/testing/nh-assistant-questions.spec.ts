import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom, toArray } from 'rxjs';
import { NhAssistantQuestionCardComponent } from '../components/question-card/nh-assistant-question-card.component';
import { Conversation, QuestionPart } from '../models/assistant-api.models';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantApiService } from '../services/nh-assistant-api.service';
import { applyNhAssistantEvent, applyNhAssistantQuestionAnswer } from '../services/nh-assistant-reducer';
import { NhAssistantStore } from '../services/nh-assistant.store';

/**
 * Structured questions (LS21, `run.AllowQuestions()` on the server): the agent asks a question with
 * choices; the card offers the choices to the person it is addressed to and is read-only for everyone
 * else, the reducer follows `question.asked` and `question.answered`, and the store answers through the
 * mock API, which refuses another person and an expired question as the server does.
 */
const colleague = 'mock-colleague';

function scenario(): NhAssistantMockScenario {
  return {
    agents: [{ id: 'projects', version: 1, displayNameKey: 'agents.projects', descriptionKey: 'agents.projects-description', canMutate: true }],
    timing: { firstEventDelayMs: 0, eventDelayMs: 0, chunkSize: 9 },
    turns: [
      {
        match: 'colleague',
        steps: [{
          question: {
            question: 'Which status should Alpha get?',
            choices: ['Active', 'On hold'],
            addresseeActorId: colleague,
            answered: [{ text: 'Done.' }]
          }
        }]
      },
      {
        match: 'expire',
        steps: [{
          question: {
            question: 'Which status should Alpha get?',
            choices: ['Active', 'On hold'],
            expiresInSeconds: 0,
            answered: [{ text: 'Done.' }]
          }
        }]
      },
      {
        steps: [
          { text: 'One question first.' },
          {
            question: {
              question: 'Which status should Alpha get?',
              choices: ['Active', 'On hold', 'Closed'],
              answered: answer => [{ text: `Alpha is ${answer.toLowerCase()} now.` }]
            }
          }
        ]
      }
    ]
  };
}

function question(overrides: Partial<QuestionPart> = {}): QuestionPart {
  return {
    type: 'question',
    questionId: 'question-1',
    question: 'Which status should Alpha get?',
    choices: ['Active', 'On hold', 'Closed'],
    addresseeActorId: 'mock-user',
    expiresAt: new Date(Date.now() + 125_000).toISOString(),
    status: 'pending',
    ...overrides
  };
}

function waitFor(condition: () => boolean, timeoutMs = 4_000): Promise<void> {
  const started = Date.now();
  return new Promise((resolve, reject) => {
    const check = () => {
      if (condition()) {
        resolve();
      } else if (Date.now() - started > timeoutMs) {
        reject(new Error('The condition was not met in time.'));
      } else {
        setTimeout(check, 5);
      }
    };
    check();
  });
}

async function configure(language: 'en' | 'nl' = 'en'): Promise<void> {
  TestBed.configureTestingModule({
    providers: [
      provideTranslateService({ fallbackLang: 'en' }),
      provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
      provideNhAssistantMockApi(scenario())
    ]
  });
  await firstValueFrom(TestBed.inject(TranslateService).use(language));
}

describe('NhAssistantQuestionCardComponent', () => {
  let fixture: ComponentFixture<NhAssistantQuestionCardComponent>;
  let answers: number[];

  async function create(part: QuestionPart, canAnswer: boolean, language: 'en' | 'nl' = 'en'): Promise<HTMLElement> {
    await configure(language);
    fixture = TestBed.createComponent(NhAssistantQuestionCardComponent);
    fixture.componentRef.setInput('question', part);
    fixture.componentRef.setInput('canAnswer', canAnswer);
    fixture.componentRef.setInput('addresseeName', 'Mia');
    answers = [];
    fixture.componentInstance.answer.subscribe(choice => answers.push(choice));
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  function choices(element: HTMLElement): HTMLButtonElement[] {
    return Array.from(element.querySelectorAll('button[data-choice]'));
  }

  it('offers the addressee one labelled button per choice and emits the answer once', async () => {
    const element = await create(question(), true);

    expect(element.querySelector('.question')!.textContent).toContain('Which status should Alpha get?');
    expect(choices(element).map(button => button.getAttribute('aria-label'))).toEqual(['Answer: Active', 'Answer: On hold', 'Answer: Closed']);
    expect(element.textContent).toMatch(/Answer within 2:0\d/);

    choices(element)[1].click();
    choices(element)[2].click();
    fixture.detectChanges();

    expect(answers).toEqual([1]);
    expect(choices(element).every(button => button.disabled)).toBeTrue();
  });

  it('is read-only for everyone else and names whom it waits for, in Dutch too', async () => {
    const element = await create(question(), false, 'nl');

    expect(choices(element).length).toBe(0);
    expect(element.querySelectorAll('.choices.read-only li').length).toBe(3);
    expect(element.querySelector('[data-waiting-for]')!.textContent).toContain('Wacht op het antwoord van Mia.');
    expect(element.querySelector('.status')!.textContent).toContain('Wacht op antwoord');
  });

  it('marks the chosen answer, and says when nobody answered or the task was stopped', async () => {
    const element = await create(question({ status: 'answered', answerIndex: 2 }), true);

    expect(choices(element).length).toBe(0);
    expect(element.querySelector('[data-chosen="true"]')!.textContent).toContain('Closed');
    expect(element.querySelector('[data-answer]')!.textContent).toContain('Answer: Closed');

    fixture.componentRef.setInput('question', question({ expiresAt: new Date(Date.now() - 1_000).toISOString() }));
    fixture.detectChanges();
    expect(fixture.nativeElement.getAttribute('data-status')).toBe('expired');
    expect(element.textContent).toContain('Nobody answered in time.');
    expect(choices(element).length).toBe(0);

    fixture.componentRef.setInput('question', question({ status: 'cancelled' }));
    fixture.detectChanges();
    expect(element.textContent).toContain('The question was withdrawn when the task was stopped.');
  });
});

describe('question events', () => {
  function conversation(): Conversation {
    return {
      id: 'chat-1',
      agentId: 'projects',
      agentVersion: 1,
      title: null,
      status: 'running',
      createdAt: '2026-10-25T08:00:00Z',
      updatedAt: '2026-10-25T08:00:00Z',
      messages: [{
        id: 'answer-1',
        role: 'assistant',
        createdAt: '2026-10-25T08:00:00Z',
        parts: [{
          type: 'tool-call',
          invocationId: 'question-1',
          toolId: 'assistant.ask-question',
          toolVersion: 1,
          displayName: 'ask_question',
          status: 'running',
          argumentsPreview: null,
          resultPreview: null,
          resultCode: null
        }]
      }],
      pendingApproval: null
    };
  }

  it('lets the turn wait for an asked question and closes it with the answer', () => {
    const asked = applyNhAssistantEvent(conversation(), { type: 'question.asked', data: question() });

    expect(asked.status).toBe('waiting-for-approval');
    expect(asked.pendingQuestion?.questionId).toBe('question-1');
    expect(asked.messages[0].parts.map(part => part.type)).toEqual(['tool-call', 'question']);
    expect(asked.messages[0].parts[0]).toEqual(jasmine.objectContaining({ status: 'awaiting-answer' }));

    const local = applyNhAssistantQuestionAnswer(asked, 'question-1', 1);
    expect(local.status).toBe('running');
    expect(local.pendingQuestion).toBeNull();
    expect(local.messages[0].parts[1]).toEqual(jasmine.objectContaining({ status: 'answered', answerIndex: 1 }));

    const answered = applyNhAssistantEvent(asked, { type: 'question.answered', data: question({ status: 'answered', answerIndex: 1 }) });
    expect(answered.pendingQuestion).toBeNull();
    expect(answered.messages[0].parts[1]).toEqual(jasmine.objectContaining({ status: 'answered', answerIndex: 1 }));
    const completed = applyNhAssistantEvent(answered, {
      type: 'turn.completed',
      data: { turnId: 'turn-1', status: 'completed', usage: { inputTokens: 1, outputTokens: 1, toolCalls: 1 }, errorCode: null }
    });
    expect(completed.status).toBe('idle');
  });
});

describe('questions through the mock API', () => {
  beforeEach(async () => configure());

  it('lets the store answer the question addressed to the user and continues the turn', async () => {
    const store = TestBed.inject(NhAssistantStore);
    await store.initialize();

    await store.send('Change the status of Alpha');
    await waitFor(() => store.canAnswer() && !store.streaming());
    const asked = store.activeConversation()!.pendingQuestion!;
    expect(asked.choices).toEqual(['Active', 'On hold', 'Closed']);

    store.answerQuestion(asked.questionId, 1);
    await waitFor(() => !store.streaming() && store.activeConversation()!.status === 'idle');

    const conversation = store.activeConversation()!;
    expect(conversation.pendingQuestion).toBeNull();
    const part = conversation.messages.flatMap(message => message.parts).find(item => item.type === 'question');
    expect(part).toEqual(jasmine.objectContaining({ status: 'answered', answerIndex: 1 }));
    const text = conversation.messages.flatMap(message => message.parts).map(item => item.type === 'text' ? item.text : '').join('');
    expect(text).toContain('Alpha is on hold now.');
  });

  it('sends a message over an expired question, which closes it and starts the turn, as the server does', async () => {
    const store = TestBed.inject(NhAssistantStore);
    await store.initialize();

    await store.send('Let it expire');
    await waitFor(() => !store.streaming() && store.activeConversation()?.pendingQuestion != null);
    const expiring = store.activeConversation()!.pendingQuestion!;
    await waitFor(() => store.waitExpired());

    expect(await store.submit('Something else')).toBeTrue();
    const questions = () => store.activeConversation()!.messages
      .flatMap(message => message.parts)
      .filter(part => part.type === 'question') as QuestionPart[];
    // Once the turn ended, the client reads the closed question again from the server.
    await waitFor(() => !store.streaming() && questions()[0]?.status === 'expired');

    const conversation = store.activeConversation()!;
    expect(store.queuedMessages()).toEqual([]);
    expect(store.error()).toBeNull();
    expect(questions()[0].questionId).toBe(expiring.questionId);
    expect(conversation.messages.some(message => message.role === 'user'
      && message.parts.some(part => part.type === 'text' && part.text === 'Something else'))).toBeTrue();
  });

  it('refuses another person and an expired question as the server does', async () => {
    const api = TestBed.inject(NhAssistantApiService);
    const backend = TestBed.inject(NhAssistantMockBackend);
    const id = (await firstValueFrom(api.createConversation({ agentId: 'projects' }))).id;

    const asking = await firstValueFrom(api.sendMessage(id, { text: 'Ask my colleague', clientMessageId: 'c1' }).pipe(toArray()));
    const asked = asking.find(event => event.type === 'question.asked')!.data as QuestionPart;
    expect(asked.addresseeActorId).toBe(colleague);

    const refused = await firstValueFrom(api.answerQuestion(id, asked.questionId, { choice: 0 }).pipe(toArray()));
    expect(refused).toEqual([{ type: 'error', data: jasmine.objectContaining({ code: 'assistant-question-forbidden' }) }]);
    expect((await firstValueFrom(api.getConversation(id))).status).toBe('waiting-for-approval');

    await firstValueFrom(api.cancel(id), { defaultValue: undefined });
    const later = await firstValueFrom(api.sendMessage(id, { text: 'Let it expire', clientMessageId: 'c2' }).pipe(toArray()));
    const expiring = later.find(event => event.type === 'question.asked')!.data as QuestionPart;
    const expired = await firstValueFrom(api.answerQuestion(id, expiring.questionId, { choice: 0 }).pipe(toArray()));

    expect(expired).toEqual([{ type: 'error', data: jasmine.objectContaining({ code: 'assistant-question-expired' }) }]);
    const stored = await firstValueFrom(api.getConversation(id));
    expect(stored.status).toBe('idle');
    expect(stored.messages.flatMap(message => message.parts).filter(part => part.type === 'question').map(part => (part as QuestionPart).status))
      .toEqual(['cancelled', 'expired']);
    expect(backend.requests.some(request => request.path.endsWith(`/questions/${expiring.questionId}/answer`))).toBeTrue();
  });
});
