import { OverlayContainer } from '@angular/cdk/overlay';
import { TemplatePortal } from '@angular/cdk/portal';
import { Component, TemplateRef, ViewContainerRef, inject, viewChild } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { AgentThreadLookup } from '../models/assistant-agent-run.models';
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

  beforeEach(() => {
    const api = jasmine.createSpyObj<NhAssistantApiService>('NhAssistantApiService', ['status', 'getConversation']);
    api.status.and.returnValue(of({ enabled: true, agents: [], limits: { maxMessageChars: 10, maxToolCallsPerTurn: 1 }, canAdminister: false }));

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
});
