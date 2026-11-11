import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom, toArray } from 'rxjs';
import { NhAssistantRunResultComponent } from '../../../agent-runs/src/public-api';
import { NhAssistantPlanComponent } from '../components/plan/nh-assistant-plan.component';
import { nhAssistantSupersededPlans } from '../internal/nh-assistant-part-groups';
import { AgentPlan, Conversation, Message, PlanPart } from '../models/assistant-api.models';
import { NhAssistantSseEvent, PlanUpdatedEvent } from '../models/assistant-sse.models';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantApiService } from '../services/nh-assistant-api.service';
import { applyNhAssistantEvent, applyNhAssistantLiveEvent } from '../services/nh-assistant-reducer';

/**
 * The live plan of agent work (`run.KeepPlan()` on the server): the checklist shows the agent's plan as
 * plain text with the current step marked and completed steps checked, the reducer keeps one plan per
 * message from `plan.updated` snapshots, the thread shows each turn's newest plan, the mock API plays a
 * plan step, and a run's result shows the run's last plan.
 */
const plan: AgentPlan = {
  steps: [
    { text: 'Find the open projects', status: 'completed' },
    { text: 'Check <b>dates</b> of [the plan](https://example.test)', status: 'in-progress' },
    { text: 'Write the summary', status: 'pending' }
  ],
  updatedAt: '2026-11-11T08:00:00Z'
};

function scenario(): NhAssistantMockScenario {
  return {
    agents: [{ id: 'projects', version: 1, displayNameKey: 'Project assistant', descriptionKey: 'Helps with projects.', canMutate: true }],
    timing: { firstEventDelayMs: 0, eventDelayMs: 0, chunkSize: 9 },
    turns: [{
      steps: [
        { plan: { steps: [{ text: 'Find the open projects', status: 'in-progress' }, { text: 'Summarize', status: 'pending' }] } },
        { plan: { steps: [{ text: 'Find the open projects', status: 'completed' }, { text: 'Summarize', status: 'in-progress' }] } },
        { text: 'Two projects are open.' }
      ]
    }],
    subjectTypes: [{ id: 'project', names: { en: 'Project' }, pluralNames: { en: 'Projects' }, subjects: [{ id: 'p-1', displayName: 'PRJ-1' }] }],
    runs: [
      { id: 'run-planned', agentId: 'projects', subjectType: 'project', subjectId: 'p-1', result: 'Reviewed.', createdAt: '2026-11-11T07:00:00Z', plan },
      { id: 'run-plain', agentId: 'projects', subjectType: 'project', subjectId: 'p-1', result: 'All fine.', createdAt: '2026-11-10T07:00:00Z' }
    ],
    admin: {
      agents: [{
        source: 'code', id: 'projects', displayName: 'Project assistant', description: 'Helps with projects.', instructions: 'Code.',
        toolSelectors: [], mcpServerIds: [], requiredPolicy: null, autonomy: 'execute', isEnabled: true,
        worksOnSubjectTypes: ['project'], worksOnAllSubjects: false, worksOnApplication: false
      }]
    }
  };
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

async function settle(fixture: ComponentFixture<unknown>): Promise<HTMLElement> {
  for (let round = 0; round < 6; round++) {
    fixture.detectChanges();
    await new Promise(resolve => setTimeout(resolve));
  }
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

function text(element: Element | null | undefined): string {
  return element?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

function conversation(messages: Message[]): Conversation {
  return {
    id: 'chat-1',
    agentId: 'projects',
    agentVersion: 1,
    title: null,
    status: 'running',
    createdAt: '2026-11-11T08:00:00Z',
    updatedAt: '2026-11-11T08:00:00Z',
    messages,
    pendingApproval: null
  };
}

function updated(steps: AgentPlan['steps'], updatedAt: string, messageId = 'answer-1'): NhAssistantSseEvent {
  const data: PlanUpdatedEvent = { turnId: 'turn-1', messageId, plan: { steps, updatedAt } };
  return { type: 'plan.updated', data };
}

describe('NhAssistantPlanComponent', () => {
  async function create(value: AgentPlan, language: 'en' | 'nl' = 'en'): Promise<HTMLElement> {
    await configure(language);
    const fixture = TestBed.createComponent(NhAssistantPlanComponent);
    fixture.componentRef.setInput('plan', value);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('shows the agent\'s plan as an accessible checklist with the current step marked', async () => {
    const element = await create(plan);
    const steps = Array.from(element.querySelectorAll('li.step'));
    const section = element.querySelector('section')!;

    expect(text(element.querySelector('#' + section.getAttribute('aria-labelledby')))).toBe('Agent\'s plan');
    expect(text(element.querySelector('.progress'))).toBe('1 of 3 done');
    expect(steps.map(step => step.getAttribute('data-status'))).toEqual(['completed', 'in-progress', 'pending']);
    expect(steps.map(step => step.getAttribute('aria-current'))).toEqual([null, 'step', null]);
    expect(steps[0].querySelector('nh-assistant-icon')).not.toBeNull();
    expect(steps.map(step => text(step.querySelector('.visually-hidden')))).toEqual(['Done', 'In progress', 'Not started']);
    expect(element.getAttribute('data-finished')).toBeNull();
  });

  it('renders every step as plain text, never as markup or a link', async () => {
    const element = await create(plan);
    const step = element.querySelectorAll('li.step .text')[1];

    expect(step.textContent).toBe('Check <b>dates</b> of [the plan](https://example.test)');
    expect(element.querySelector('b, a')).toBeNull();
  });

  it('says in Dutch when every step is done', async () => {
    const element = await create({ steps: plan.steps.map(step => ({ ...step, status: 'completed' })), updatedAt: plan.updatedAt }, 'nl');

    expect(text(element.querySelector('.title'))).toBe('Plan van de agent');
    expect(text(element.querySelector('.progress'))).toBe('3 van 3 klaar');
    expect(element.getAttribute('data-finished')).toBe('true');
    expect(element.querySelector('[aria-current]')).toBeNull();
  });
});

describe('plan events', () => {
  const answer: Message = { id: 'answer-1', role: 'assistant', createdAt: '2026-11-11T08:00:00Z', parts: [{ type: 'text', text: 'Working on it.' }] };

  it('adds the plan to its message and replaces it with each newer snapshot', () => {
    const first = applyNhAssistantEvent(conversation([answer]), updated([{ text: 'Read', status: 'in-progress' }], '2026-11-11T08:00:01Z'));
    const second = applyNhAssistantEvent(first, updated([{ text: 'Read', status: 'completed' }, { text: 'Write', status: 'in-progress' }], '2026-11-11T08:00:02Z'));
    // A late live event with an older snapshot changes nothing.
    const late = applyNhAssistantEvent(second, updated([{ text: 'Read', status: 'in-progress' }], '2026-11-11T08:00:01Z'));

    expect(first.messages[0].parts.map(part => part.type)).toEqual(['text', 'plan']);
    const plans = second.messages[0].parts.filter((part): part is PlanPart => part.type === 'plan');
    expect(plans.length).toBe(1);
    expect(plans[0].steps.map(step => step.status)).toEqual(['completed', 'in-progress']);
    expect(late).toEqual(second);
  });

  it('creates the message a plan names and follows live updates like the stream', () => {
    const shared = applyNhAssistantLiveEvent(conversation([]), {
      conversationId: 'chat-1',
      actorId: 'mock-colleague',
      type: 'plan.updated',
      data: { turnId: 'turn-1', messageId: 'answer-2', plan: { steps: [{ text: 'Read', status: 'pending' }], updatedAt: '2026-11-11T08:00:00Z' } }
    });

    expect(shared.messages.map(message => message.id)).toEqual(['answer-2']);
    expect(shared.messages[0].parts[0]).toEqual({ type: 'plan', steps: [{ text: 'Read', status: 'pending' }], updatedAt: '2026-11-11T08:00:00Z' });
  });

  it('shows each turn\'s newest plan: a later message of the turn replaces an earlier one', () => {
    const planPart: PlanPart = { type: 'plan', ...plan };
    const messages: Message[] = [
      { id: 'q-1', role: 'user', createdAt: '', parts: [{ type: 'text', text: 'Go.' }] },
      { id: 'a-1', role: 'assistant', createdAt: '', parts: [planPart] },
      { id: 'a-2', role: 'assistant', createdAt: '', parts: [planPart] },
      { id: 'q-2', role: 'user', createdAt: '', parts: [{ type: 'text', text: 'Again.' }] },
      { id: 'a-3', role: 'assistant', createdAt: '', parts: [planPart] }
    ];

    expect([...nhAssistantSupersededPlans(messages)]).toEqual(['a-1']);
  });

  it('come from the mock API as the server streams them', async () => {
    await configure();
    const api = TestBed.inject(NhAssistantApiService);
    const created = await firstValueFrom(api.createConversation({ agentId: 'projects' }));

    const events = await firstValueFrom(api.sendMessage(created.id, { text: 'Which projects are open?', clientMessageId: 'client-1' }).pipe(toArray()));
    const plans = events.filter(event => event.type === 'plan.updated').map(event => (event.data as PlanUpdatedEvent).plan.steps);
    const reloaded = await firstValueFrom(api.getConversation(created.id));

    expect(plans.length).toBe(2);
    expect(plans[1].map(step => step.status)).toEqual(['completed', 'in-progress']);
    expect(events.some(event => event.type === 'tool.started')).toBeFalse();
    const part = reloaded.messages.flatMap(message => message.parts).find(item => item.type === 'plan') as PlanPart;
    expect(part.steps.map(step => step.text)).toEqual(['Find the open projects', 'Summarize']);
  });
});

@Component({
  standalone: true,
  imports: [NhAssistantRunResultComponent],
  template: `
    <div class="page" style="width: 375px">
      <nh-assistant-run-result [runId]="runId()" />
    </div>
  `
})
class RunResultHost {
  readonly runId = signal('run-planned');
}

describe('A run\'s plan', () => {
  it('shows on the run\'s result, without sideways scrolling at 375px', async () => {
    await configure();
    const fixture = TestBed.createComponent(RunResultHost);
    const element = await settle(fixture);

    const shown = element.querySelector('[data-run-plan]');
    expect(shown).not.toBeNull();
    expect(text(shown?.querySelector('.progress'))).toBe('1 of 3 done');
    expect(text(shown?.querySelector('[aria-current="step"] .text'))).toBe('Check <b>dates</b> of [the plan](https://example.test)');
    const page = element.querySelector('.page') as HTMLElement;
    expect(page.scrollWidth).toBeLessThanOrEqual(page.clientWidth + 1);

    fixture.componentInstance.runId.set('run-plain');
    await settle(fixture);
    expect(element.querySelector('[data-run-plan]')).toBeNull();
  });
});
