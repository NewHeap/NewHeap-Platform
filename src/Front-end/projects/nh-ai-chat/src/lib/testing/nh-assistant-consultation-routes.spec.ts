import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom, throwError, toArray } from 'rxjs';
import { NhAssistantAgentConsultationRoutesComponent } from '../../../admin/src/agents/nh-assistant-agent-consultation-routes.component';
import { startNhAssistantAdminTranslations } from '../../../admin/src/i18n/nh-assistant-admin-translations';
import { ApprovalPart } from '../models/assistant-api.models';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAdminApiService } from '../services/nh-assistant-admin-api.service';
import { NhAssistantAgentRunApiService } from '../services/nh-assistant-agent-run-api.service';
import { NhAssistantApiError, NhAssistantApiService } from '../services/nh-assistant-api.service';
import { NhAssistantStore } from '../services/nh-assistant.store';

/**
 * Consultation proposals: an assistant consults another agent about a subject, and that agent's
 * proposal is decided by the person who asked in their own chat (`asker-decides`), or waits for the
 * subject's approvers with the agent's rights (`subject-agent-rights`) or the asker's rights
 * (`subject-asker-rights`). The mock API plays all three, and administrators switch the routes of an
 * agent's consultations within what code allows.
 */
function scenario(): NhAssistantMockScenario {
  return {
    agents: [{ id: 'projects', version: 1, displayNameKey: 'agents.projects', descriptionKey: 'agents.projects', canMutate: true }],
    timing: { firstEventDelayMs: 0, eventDelayMs: 0 },
    turns: [{
      match: 'alpha',
      steps: [
        { text: 'I asked the planning agent.' },
        {
          approval: {
            toolId: 'projects.change-status',
            displayName: 'Change project status',
            summary: 'Put Alpha on hold',
            presentation: { toolDisplayName: 'Change project status', summary: 'Put Alpha on hold', fields: [], notice: null },
            argumentsPreview: '{"status":"on-hold"}',
            targets: ['Project Alpha'],
            approved: [{ text: 'Alpha is on hold.' }],
            rejected: [{ text: 'Nothing changed.' }],
            left: [{ text: 'The approvers of Alpha decide.' }],
            consultation: {
              consultationId: 'consultation-1',
              agentId: 'planner',
              subjectType: 'project',
              subjectId: 'p-1',
              subjectDisplayName: 'Alpha',
              leaveRoute: 'subject-agent-rights',
              answer: 'Alpha waits for the supplier.'
            }
          }
        }
      ]
    }],
    proposals: [
      {
        approvalId: 'agent-rights', agentId: 'planner', subjectType: 'project', subjectId: 'p-1', conversationId: 'planner-p-1',
        toolId: 'projects.change-status', consultationId: 'consultation-1', consultationRoute: 'subject-agent-rights',
        initiatorActorId: 'mock-user'
      },
      {
        approvalId: 'asker-decides', agentId: 'planner', subjectType: 'project', subjectId: 'p-1', conversationId: 'planner-p-1',
        toolId: 'projects.change-status', consultationId: 'consultation-1', consultationRoute: 'asker-decides',
        initiatorActorId: 'colleague'
      },
      {
        approvalId: 'asker-rights', agentId: 'planner', subjectType: 'project', subjectId: 'p-1', conversationId: 'planner-p-1',
        toolId: 'projects.change-status', consultationId: 'consultation-2', consultationRoute: 'subject-asker-rights',
        initiatorActorId: 'colleague'
      }
    ],
    consultations: [
      { id: 'consultation-1', conversationId: 'planner-p-1', askerActorId: 'mock-user', askingAgentId: 'projects', createdAt: '2026-10-22T10:00:00Z' },
      { id: 'consultation-2', conversationId: 'planner-p-1', askerActorId: 'colleague', createdAt: '2026-10-22T11:00:00Z' }
    ],
    admin: {
      agents: [
        {
          source: 'code', id: 'planner', displayName: 'Planning agent', description: 'Plans projects.', instructions: 'Plan.',
          toolSelectors: [], mcpServerIds: [], requiredPolicy: null, autonomy: 'propose', isEnabled: true,
          consultations: { 'asker-decides': ['project'], 'subject-asker-rights': ['project', 'task'] }
        },
        {
          source: 'code', id: 'projects', displayName: 'Project assistant', description: 'Projects.', instructions: 'Help.',
          toolSelectors: [], mcpServerIds: [], requiredPolicy: null, autonomy: 'execute', isEnabled: true
        }
      ]
    }
  };
}

async function configure(script = scenario()): Promise<void> {
  TestBed.configureTestingModule({
    providers: [
      provideTranslateService({ fallbackLang: 'en' }),
      provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
      provideNhAssistantMockApi(script)
    ]
  });
  await firstValueFrom(TestBed.inject(TranslateService).use('en'));
  // The exported admin component that hosts this part starts the administration's texts; the
  // spec renders the part on its own.
  TestBed.runInInjectionContext(() => startNhAssistantAdminTranslations());
}

async function failure(promise: Promise<unknown>): Promise<NhAssistantApiError> {
  return promise.then(() => { throw new Error('Expected a failure.'); }, error => error as NhAssistantApiError);
}

async function waitFor(condition: () => boolean, timeoutMs = 2000): Promise<void> {
  const start = Date.now();
  while (!condition()) {
    if (Date.now() - start > timeoutMs) {
      throw new Error('Condition not met in time.');
    }
    await new Promise(resolve => setTimeout(resolve, 5));
  }
}

function flush(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve));
}

async function settle(fixture: ComponentFixture<unknown>): Promise<HTMLElement> {
  for (let round = 0; round < 6; round++) {
    fixture.detectChanges();
    await flush();
  }
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

function requestBodies(method: string, path: string): unknown[] {
  return TestBed.inject(NhAssistantMockBackend).requests
    .filter(item => item.method === method && item.path === path)
    .map(item => item.body);
}

describe('consultation proposals of the mock API', () => {
  beforeEach(async () => configure());

  it('lets the person who asked decide a consulted agent\'s proposal in their chat and leave it for the approvers', async () => {
    const api = TestBed.inject(NhAssistantApiService);
    const id = (await firstValueFrom(api.createConversation({ agentId: 'projects' }))).id;

    const turn = await firstValueFrom(api.sendMessage(id, { text: 'Put alpha on hold', clientMessageId: 'c-1' }).pipe(toArray()));
    const approval = turn.find(event => event.type === 'approval.required')!.data as ApprovalPart;
    expect(approval.consultation).toEqual({
      consultationId: 'consultation-1', agentId: 'planner', subjectType: 'project', subjectId: 'p-1', subjectDisplayName: 'Alpha',
      leaveRoute: 'subject-agent-rights', answer: 'Alpha waits for the supplier.', movedToApprovalId: null
    });

    const left = await firstValueFrom(api.decideApproval(id, approval.approvalId, {
      decision: 'leave',
      expectedProposalHash: approval.proposalHash
    }).pipe(toArray()));

    expect(left.map(event => event.type)).toContain('turn.started');
    expect(left.at(-1)).toEqual(jasmine.objectContaining({ type: 'turn.completed' }));
    const stored = await firstValueFrom(api.getConversation(id));
    const part = stored.messages.flatMap(message => message.parts).find(item => item.type === 'approval') as ApprovalPart;
    expect(part.status).toBe('rejected');
    expect(part.consultation?.leaveRoute).toBeNull();
    expect(part.consultation?.movedToApprovalId).toEqual(jasmine.any(String));
    expect(stored.pendingApproval).toBeNull();
  });

  it('accepts leave only for a consulted agent\'s proposal with a route at the subject', async () => {
    const script = scenario();
    const step = script.turns[0].steps[1] as { approval: { consultation?: { leaveRoute?: string | null } } };
    step.approval.consultation!.leaveRoute = null;
    TestBed.resetTestingModule();
    await configure(script);
    const api = TestBed.inject(NhAssistantApiService);
    const id = (await firstValueFrom(api.createConversation({ agentId: 'projects' }))).id;
    const turn = await firstValueFrom(api.sendMessage(id, { text: 'Put alpha on hold', clientMessageId: 'c-1' }).pipe(toArray()));
    const approval = turn.find(event => event.type === 'approval.required')!.data as ApprovalPart;

    const refused = await firstValueFrom(api.decideApproval(id, approval.approvalId, {
      decision: 'leave',
      expectedProposalHash: approval.proposalHash
    }).pipe(toArray()));

    expect(refused).toEqual([{
      type: 'error',
      data: { code: 'assistant-consult-proposal-refused', messageKey: 'nh-assistant.errors.assistant-consult-proposal-refused' }
    }]);
    expect((await firstValueFrom(api.getConversation(id))).pendingApproval?.approvalId).toBe(approval.approvalId);
  });

  it('drives the store: the card leaves the proposal and the conversation then names the one at the subject', async () => {
    const store = TestBed.inject(NhAssistantStore);
    await store.initialize();

    await store.send('Put alpha on hold');
    // The turn waits for the decision once its stream ended.
    await waitFor(() => store.pendingApproval() !== null && !store.streaming());
    expect(store.canDecide()).toBeTrue();
    store.decide('leave');
    expect(store.deciding()).toBeTrue();
    await waitFor(() => !store.streaming() && !store.deciding());
    await waitFor(() => (store.activeConversation()!.messages
      .flatMap(message => message.parts)
      .find(part => part.type === 'approval') as ApprovalPart).consultation?.movedToApprovalId != null);

    const conversation = store.activeConversation()!;
    expect(conversation.status).toBe('idle');
    expect(conversation.messages.at(-1)!.parts.at(-1)).toEqual({ type: 'text', text: 'The approvers of Alpha decide.' });
  });

  it('serves the routes of a consulted agent\'s proposals in its thread with the caller\'s right to decide each', async () => {
    const runs = TestBed.inject(NhAssistantAgentRunApiService);

    const proposals = await firstValueFrom(runs.getProposals('planner-p-1'));
    const byId = new Map(proposals.map(proposal => [proposal.approvalId, proposal]));
    expect(byId.get('agent-rights')).toEqual(jasmine.objectContaining({
      consultationId: 'consultation-1', consultationRoute: 'subject-agent-rights', grantSource: 'agent', initiatorActorId: 'mock-user', mode: 'deferred'
    }));
    expect(byId.get('asker-decides')).toEqual(jasmine.objectContaining({ consultationRoute: 'asker-decides', mode: 'inline' }));
    expect(byId.get('asker-rights')).toEqual(jasmine.objectContaining({ consultationRoute: 'subject-asker-rights', grantSource: 'delegated' }));

    const lookup = await firstValueFrom(runs.lookupProposals(['agent-rights', 'asker-decides', 'asker-rights']));
    expect(lookup.items.map(item => [item.proposal.approvalId, item.canDecide, item.reasonCode])).toEqual([
      // The person who asked may not decide a proposal that runs with the agent's own rights.
      ['agent-rights', false, 'assistant-four-eyes-required'],
      // The person who asked decides it in their own chat; nobody decides it here.
      ['asker-decides', false, 'assistant-consult-asker-decides'],
      ['asker-rights', true, null]
    ]);
    const refused = await failure(firstValueFrom(runs.decide(byId.get('asker-decides')!, 'approve')));
    expect([refused.status, refused.code]).toEqual([409, 'assistant-consult-asker-decides']);
  });

  it('counts the proposals of each consultation per route', async () => {
    const runs = TestBed.inject(NhAssistantAgentRunApiService);

    const list = await firstValueFrom(runs.getConsultations('planner-p-1'));

    expect(list.items.map(item => [item.id, item.proposalCount, item.proposalsByRoute])).toEqual([
      ['consultation-2', 1, { 'subject-asker-rights': 1 }],
      ['consultation-1', 2, { 'subject-agent-rights': 1, 'asker-decides': 1 }]
    ]);
  });
});

describe('consultation routes of an agent in the administration', () => {
  beforeEach(async () => configure());

  it('reports the routes code allows and switches them within that ceiling', async () => {
    const admin = TestBed.inject(NhAssistantAdminApiService);

    const state = await firstValueFrom(admin.getAgentState('planner'));
    expect(state.consultationRoutes).toEqual({
      routes: [
        { route: 'asker-decides', allowed: true, override: null, on: true, subjectTypes: ['project'] },
        { route: 'subject-agent-rights', allowed: false, override: null, on: false, subjectTypes: [] },
        { route: 'subject-asker-rights', allowed: true, override: null, on: true, subjectTypes: ['project', 'task'] }
      ],
      changedAt: null,
      changedBy: null,
      reason: null
    });
    // Nobody can consult an agent without consultations in code.
    expect((await firstValueFrom(admin.getAgentState('projects'))).consultationRoutes).toBeNull();

    const off = await firstValueFrom(admin.setConsultationRoutes('planner', { askerDecides: 'off', reason: ' Approvers first ' }));
    expect(off.consultationRoutes!.routes[0]).toEqual(jasmine.objectContaining({ override: false, on: false }));
    expect(off.consultationRoutes!.routes[2]).toEqual(jasmine.objectContaining({ override: null, on: true }));
    expect(off.consultationRoutes).toEqual(jasmine.objectContaining({ changedBy: 'current-user', reason: 'Approvers first' }));

    const notAllowed = await failure(firstValueFrom(admin.setConsultationRoutes('planner', { subjectAgentRights: 'on' })));
    expect([notAllowed.status, notAllowed.code]).toEqual([409, 'assistant-consult-route-not-allowed']);
    const noConsultations = await failure(firstValueFrom(admin.setConsultationRoutes('projects', { askerDecides: 'off' })));
    expect(noConsultations.code).toBe('assistant-consult-route-not-allowed');
    const invalid = await failure(firstValueFrom(admin.setConsultationRoutes('planner', { askerDecides: 'maybe' as 'on' })));
    expect([invalid.status, invalid.code]).toEqual([400, 'assistant-validation']);

    const back = await firstValueFrom(admin.setConsultationRoutes('planner', { askerDecides: 'code' }));
    expect(back.consultationRoutes!.routes[0]).toEqual(jasmine.objectContaining({ override: null, on: true }));
  });

  describe('nh-assistant-agent-consultation-routes', () => {
    /** Each route walks a reason form; the flow takes longer than Jasmine's default. */
    const formFlowTimeout = 20_000;

    async function render(agentId = 'planner'): Promise<{ fixture: ComponentFixture<NhAssistantAgentConsultationRoutesComponent>; element: HTMLElement }> {
      const fixture = TestBed.createComponent(NhAssistantAgentConsultationRoutesComponent);
      fixture.componentRef.setInput('agentId', agentId);
      fixture.componentRef.setInput('subjectTypes', [
        { id: 'project', name: 'Project', pluralName: 'Projects' },
        { id: 'task', name: 'Task', pluralName: null }
      ]);
      return { fixture, element: await settle(fixture) };
    }

    function route(element: HTMLElement, name: string): HTMLElement {
      return element.querySelector(`[data-route="${name}"]`) as HTMLElement;
    }

    function button(scope: HTMLElement, text: string): HTMLButtonElement {
      const match = Array.from(scope.querySelectorAll('button')).find(item => item.textContent?.trim() === text);
      if (!match) {
        throw new Error(`Button "${text}" not found.`);
      }
      return match;
    }

    function type(scope: HTMLElement, selector: string, value: string): void {
      const input = scope.querySelector(selector) as HTMLInputElement;
      input.value = value;
      input.dispatchEvent(new Event('input'));
    }

    it('explains each route, shows its state and keeps a route code does not allow off', async () => {
      const { element } = await render();

      expect(element.querySelector('h5')!.textContent).toContain('Consultation routes');
      const askerDecides = route(element, 'asker-decides');
      expect(askerDecides.querySelector('[data-route-explain]')!.textContent)
        .toContain('The person who asked decides in their own chat; it runs with their own rights.');
      expect(route(element, 'subject-agent-rights').querySelector('[data-route-explain]')!.textContent)
        .toContain('The subject\'s approvers other than the person who asked decide; it runs with the agent\'s own rights.');
      expect(route(element, 'subject-asker-rights').querySelector('[data-route-explain]')!.textContent)
        .toContain('The subject\'s approvers decide; it runs later with the rights of the person who asked.');
      expect(askerDecides.querySelector('[data-route-state]')!.textContent).toContain('On');
      expect(askerDecides.querySelector('[data-route-source]')!.textContent).toContain('As the consultations allow');
      expect(askerDecides.querySelector('[data-route-types]')!.textContent).toContain('For: Projects');
      expect(route(element, 'subject-asker-rights').querySelector('[data-route-types]')!.textContent).toContain('For: Projects, Task');
      expect(askerDecides.querySelector('[data-route-action="code"]')).toBeNull();

      const notAllowed = route(element, 'subject-agent-rights');
      expect(notAllowed.querySelector('[data-route-state]')!.textContent).toContain('Off');
      expect(notAllowed.querySelector('[data-route-source]')!.textContent).toContain('Not allowed');
      const switchOn = notAllowed.querySelector('[data-route-action="on"]') as HTMLButtonElement;
      expect(switchOn.disabled).toBeTrue();
      expect(switchOn.getAttribute('aria-describedby')).toBe(notAllowed.querySelector('[data-route-not-allowed]')!.id);
    });

    it('shows nothing for an agent nobody can consult', async () => {
      const { element } = await render('projects');

      expect(element.querySelector('[data-consultation-routes]')).toBeNull();
    });

    it('switches a route off with a reason and back to the code setting', async () => {
      const { fixture, element } = await render();

      (route(element, 'asker-decides').querySelector('[data-route-action="off"]') as HTMLButtonElement).click();
      await settle(fixture);
      const form = () => route(element, 'asker-decides').querySelector('[data-route-form]') as HTMLElement;
      expect(form().textContent).toContain('Consultations no longer use this route for new proposals');
      expect(document.activeElement).toBe(form().querySelector('input'));
      type(form(), 'input', 'Approvers first');
      button(form(), 'Confirm switching off').click();
      await settle(fixture);

      expect(requestBodies('POST', 'admin/agents/planner/consultation-routes')).toEqual([{ askerDecides: 'off', reason: 'Approvers first' }]);
      expect(form()).toBeNull();
      expect(route(element, 'asker-decides').querySelector('[data-route-state]')!.textContent).toContain('Off');
      expect(route(element, 'asker-decides').querySelector('[data-route-source]')!.textContent).toContain('Overridden');
      expect(element.querySelector('[data-routes-changed]')!.textContent).toContain('Note: Approvers first');
      // Focus returns to the route's action.
      expect(document.activeElement).toBe(route(element, 'asker-decides').querySelector('[data-route-action="on"]'));

      (route(element, 'asker-decides').querySelector('[data-route-action="code"]') as HTMLButtonElement).click();
      await settle(fixture);
      expect(form().textContent).toContain('one of them allows this route, so it is on');
      button(form(), 'Confirm').click();
      await settle(fixture);

      expect(requestBodies('POST', 'admin/agents/planner/consultation-routes').at(-1)).toEqual({ askerDecides: 'code', reason: null });
      expect(route(element, 'asker-decides').querySelector('[data-route-source]')!.textContent).toContain('As the consultations allow');
      expect(route(element, 'asker-decides').querySelector('[data-route-action="code"]')).toBeNull();
    }, formFlowTimeout);

    it('shows a refused switch with its reason and keeps the form open', async () => {
      const { fixture, element } = await render();
      const admin = TestBed.inject(NhAssistantAdminApiService);
      spyOn(admin, 'setConsultationRoutes').and.returnValue(throwError(() => new NhAssistantApiError(
        409, 'assistant-consult-route-not-allowed', 'nh-assistant.errors.assistant-consult-route-not-allowed')));

      (route(element, 'subject-asker-rights').querySelector('[data-route-action="off"]') as HTMLButtonElement).click();
      await settle(fixture);
      button(route(element, 'subject-asker-rights'), 'Cancel').click();
      await settle(fixture);
      expect(route(element, 'subject-asker-rights').querySelector('[data-route-form]')).toBeNull();
      expect(document.activeElement).toBe(route(element, 'subject-asker-rights').querySelector('[data-route-action="off"]'));

      (route(element, 'subject-asker-rights').querySelector('[data-route-action="off"]') as HTMLButtonElement).click();
      await settle(fixture);
      button(route(element, 'subject-asker-rights'), 'Confirm switching off').click();
      await settle(fixture);

      expect(admin.setConsultationRoutes).toHaveBeenCalledOnceWith('planner', { subjectAskerRights: 'off', reason: null });
      const error = element.querySelector('[data-routes-error]') as HTMLElement;
      expect(error.getAttribute('role')).toBe('alert');
      expect(error.textContent).toContain('None of the agent\'s consultations allows this route, so it cannot be switched on.');
      expect(route(element, 'subject-asker-rights').querySelector('[data-route-form]')).not.toBeNull();
      expect(route(element, 'subject-asker-rights').querySelector('[data-route-state]')!.textContent).toContain('On');
    }, formFlowTimeout);
  });
});
