import { OverlayContainer } from '@angular/cdk/overlay';
import { TemplatePortal } from '@angular/cdk/portal';
import { Component, TemplateRef, ViewContainerRef, inject, viewChild } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { AgentThreadLookup } from '../models/assistant-agent-run.models';
import { AssistantStatus, Conversation } from '../models/assistant-api.models';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAgentRunApiService } from './nh-assistant-agent-run-api.service';
import { NhAssistantApiError, NhAssistantApiService } from './nh-assistant-api.service';
import { NhAssistantPanelService } from './nh-assistant-panel.service';
import { NhAssistantStore } from './nh-assistant.store';

@Component({
  standalone: true,
  template: `
    <button id="opener" type="button">Open</button>
    <ng-template #drawer><section class="drawer"><textarea data-nh-assistant-autofocus></textarea></section></ng-template>
  `
})
class PanelHostComponent {
  readonly drawer = viewChild.required<TemplateRef<unknown>>('drawer');
  readonly viewContainerRef = inject(ViewContainerRef);
}

const lookup: AgentThreadLookup = {
  thread: {
    conversationId: 'thread-1',
    agentId: 'intake',
    subjectType: 'project',
    subjectId: 'p-1',
    subjectDisplayName: 'Project One',
    accountability: 'agent',
    accountableOwnerId: 'agent:intake',
    role: 'participant',
    activeRun: null,
    waitingInputs: 0
  },
  canStart: true,
  role: 'participant'
};

const status: AssistantStatus = {
  enabled: true,
  agents: [
    { id: 'general', version: 1, displayNameKey: 'agents.general', descriptionKey: 'agents.general', canMutate: false },
    { id: 'projects', version: 1, displayNameKey: 'agents.projects', descriptionKey: 'agents.projects', canMutate: true }
  ],
  limits: { maxMessageChars: 40, maxToolCallsPerTurn: 1 },
  canAdminister: false
};

function conversation(id: string, agentId = 'general'): Conversation {
  return {
    id,
    agentId,
    agentVersion: 1,
    title: null,
    status: 'idle',
    createdAt: '2026-10-28T09:00:00Z',
    updatedAt: '2026-10-28T09:00:00Z',
    messages: [],
    pendingApproval: null
  };
}

/** Lets the panel's detached status reload and conversation opening finish. */
async function settle(): Promise<void> {
  for (let round = 0; round < 5; round++) {
    await new Promise(resolve => setTimeout(resolve));
  }
}

describe('NhAssistantPanelService', () => {
  let service: NhAssistantPanelService;
  let overlayElement: HTMLElement;
  let agentRuns: jasmine.SpyObj<NhAssistantAgentRunApiService>;
  let api: jasmine.SpyObj<NhAssistantApiService>;

  beforeEach(() => {
    api = jasmine.createSpyObj<NhAssistantApiService>('NhAssistantApiService', [
      'status', 'getConversation', 'createConversation', 'sendMessage'
    ]);
    api.status.and.returnValue(of(status));
    api.getConversation.and.callFake(id => id === 'missing'
      ? throwError(() => new NhAssistantApiError(404, 'assistant-conversation-not-found', 'nh-assistant.errors.assistant-conversation-not-found'))
      : of(conversation(id)));

    agentRuns = jasmine.createSpyObj<NhAssistantAgentRunApiService>('NhAssistantAgentRunApiService', ['openThread']);

    TestBed.configureTestingModule({
      imports: [PanelHostComponent],
      providers: [
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => null }),
        { provide: NhAssistantApiService, useValue: api },
        { provide: NhAssistantAgentRunApiService, useValue: agentRuns }
      ]
    });

    const fixture = TestBed.createComponent(PanelHostComponent);
    fixture.detectChanges();
    service = TestBed.inject(NhAssistantPanelService);
    service.registerPanel(new TemplatePortal(fixture.componentInstance.drawer(), fixture.componentInstance.viewContainerRef));
    overlayElement = TestBed.inject(OverlayContainer).getContainerElement();
  });

  it('opens a right-hand drawer of 420 px and closes it again', () => {
    service.open();

    const pane = overlayElement.querySelector<HTMLElement>('.nh-assistant-overlay-pane');
    expect(service.isOpen()).toBeTrue();
    expect(pane?.querySelector('.drawer')).not.toBeNull();
    expect(pane?.style.width).toBe('420px');

    service.close();

    expect(service.isOpen()).toBeFalse();
    expect(overlayElement.querySelector('.drawer')).toBeNull();
  });

  it('toggles', () => {
    service.toggle();
    expect(service.isOpen()).toBeTrue();

    service.toggle();
    expect(service.isOpen()).toBeFalse();
  });

  it('closes on Escape and returns focus to the opener', () => {
    const opener = document.getElementById('opener') as HTMLButtonElement;
    opener.focus();
    service.open();

    document.body.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));

    expect(service.isOpen()).toBeFalse();
    expect(document.activeElement).toBe(opener);
  });

  it('opens the conversation with an agent about a subject, creating the thread on the server first', async () => {
    agentRuns.openThread.and.returnValue(of(lookup));
    const opened = spyOn(TestBed.inject(NhAssistantStore), 'openConversation').and.resolveTo();

    const result = await service.openSubject('intake', { type: 'project', id: 'p-1' });
    await settle();

    expect(agentRuns.openThread).toHaveBeenCalledOnceWith('intake', 'project', 'p-1');
    expect(result).toEqual(lookup);
    expect(service.isOpen()).toBeTrue();
    expect(opened).toHaveBeenCalledOnceWith('thread-1');
  });

  it('rejects with the server code and leaves the panel closed when the subject refuses the caller', async () => {
    agentRuns.openThread.and.returnValue(throwError(() => new NhAssistantApiError(
      403,
      'assistant-subject-access-denied',
      'nh-assistant.errors.assistant-subject-access-denied')));

    await expectAsync(service.openSubject('intake', { type: 'project', id: 'p-1' }))
      .toBeRejectedWith(jasmine.objectContaining({ code: 'assistant-subject-access-denied' }));
    expect(service.isOpen()).toBeFalse();
  });

  describe('with a draft', () => {
    let store: NhAssistantStore;

    beforeEach(() => {
      store = TestBed.inject(NhAssistantStore);
    });

    it('opens the drawer with the draft for the composer and never sends it', async () => {
      service.open({ draft: '  Write a status brief on Alpha.  ' });
      await settle();

      expect(service.isOpen()).toBeTrue();
      expect(store.pendingDraft()).toEqual({ text: 'Write a status brief on Alpha.', replace: false });
      expect(api.createConversation).not.toHaveBeenCalled();
      expect(api.sendMessage).not.toHaveBeenCalled();
    });

    it('takes a draft while the drawer is already open and passes replaceDraft on', async () => {
      service.open();
      await settle();

      service.open({ draft: 'Replace what I typed', replaceDraft: true });
      await settle();

      expect(store.pendingDraft()).toEqual({ text: 'Replace what I typed', replace: true });
    });

    it('caps the draft at the longest message the server takes', async () => {
      service.open({ draft: `${'A'.repeat(39)} and more` });
      await settle();

      // Cut at 40 characters; the space the cut leaves at the end goes as well.
      expect(store.pendingDraft()).toEqual({ text: 'A'.repeat(39), replace: false });
    });

    it('starts a new conversation with the agent, as a new chat with it', async () => {
      service.open('c1');
      await settle();
      expect(store.activeConversation()?.id).toBe('c1');

      service.open({ agentId: 'projects', draft: 'Plan the next milestone.' });
      await settle();

      expect(store.selectedAgentId()).toBe('projects');
      expect(store.activeConversation()).toBeNull();
      expect(store.pendingDraft()).toEqual({ text: 'Plan the next milestone.', replace: false });
    });

    it('puts the draft for a new chat with another agent in its empty composer and leaves the conversation its unsent text', async () => {
      service.open('c1');
      await settle();
      store.keepUnsentMessage({ text: 'My question about c1', prepared: false });

      service.open({ agentId: 'projects', draft: 'Plan the next milestone.' });
      await settle();

      expect(store.activeConversation()).toBeNull();
      expect(store.unsentMessage()).toBeNull();
      expect(store.pendingDraft()).toEqual({ text: 'Plan the next milestone.', replace: false });
      store.consumeRestoredDraft();
      await store.openConversation('c1');
      expect(store.unsentMessage()).toEqual({ text: 'My question about c1', prepared: false });
    });

    it('keeps the person\'s unsent text in the conversation it opens and drops the draft', async () => {
      service.open('c1');
      await settle();
      store.keepUnsentMessage({ text: 'My question about c1', prepared: false });
      service.close();

      service.open({ conversationId: 'c1', draft: 'Continue here.' });
      await settle();

      expect(store.unsentMessage()).toEqual({ text: 'My question about c1', prepared: false });
      expect(store.pendingDraft()).toBeNull();
      service.open({ conversationId: 'c1', draft: 'Continue here.', replaceDraft: true });
      await settle();
      expect(store.pendingDraft()).toEqual({ text: 'Continue here.', replace: true });
    });

    it('starts a new conversation also when the agent is already chosen', async () => {
      service.open('c1');
      await settle();

      service.open({ agentId: 'general' });
      await settle();

      expect(store.selectedAgentId()).toBe('general');
      expect(store.activeConversation()).toBeNull();
      expect(store.pendingDraft()).toBeNull();
    });

    it('leaves the conversation and puts no draft for an agent the person may not use', async () => {
      service.open('c1');
      await settle();

      service.open({ agentId: 'payroll', draft: 'Raise every salary.' });
      await settle();

      expect(store.activeConversation()?.id).toBe('c1');
      expect(store.selectedAgentId()).toBe('general');
      expect(store.pendingDraft()).toBeNull();
    });

    it('opens a conversation with the draft, and drops the draft when the conversation does not open', async () => {
      service.open({ conversationId: 'c2', draft: 'Continue here.' });
      await settle();
      expect(store.activeConversation()?.id).toBe('c2');
      expect(store.pendingDraft()).toEqual({ text: 'Continue here.', replace: false });
      store.consumeRestoredDraft();

      service.open({ conversationId: 'missing', draft: 'Not for c2.' });
      await settle();

      expect(store.activeConversation()?.id).toBe('c2');
      expect(store.pendingDraft()).toBeNull();
    });

    it('drops the draft when the drawer closed before the assistant was ready', async () => {
      service.open({ draft: 'Too late' });
      service.close();
      await settle();

      expect(store.pendingDraft()).toBeNull();
    });
  });
});
