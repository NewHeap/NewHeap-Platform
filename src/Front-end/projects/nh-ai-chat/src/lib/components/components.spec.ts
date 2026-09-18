import { OverlayContainer } from '@angular/cdk/overlay';
import { Component, Injectable } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { BehaviorSubject, Observable, Subject, firstValueFrom, of } from 'rxjs';
import { ApprovalDecision, ApprovalPart, AssistantStatus, Message, ToolCallPart } from '../models/assistant-api.models';
import { NhAssistantSseEvent } from '../models/assistant-sse.models';
import { NhAssistantAccessPolicy } from '../nh-assistant.config';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantApiService } from '../services/nh-assistant-api.service';
import { NhAssistantPanelService } from '../services/nh-assistant-panel.service';
import { NhAssistantStore } from '../services/nh-assistant.store';
import { NhAssistantApprovalCardComponent, formatNhAssistantCountdown } from './approval-card/nh-assistant-approval-card.component';
import { NhAssistantComposerComponent } from './composer/nh-assistant-composer.component';
import { NhAssistantLauncherComponent } from './launcher/nh-assistant-launcher.component';
import { NhAssistantPanelComponent } from './panel/nh-assistant-panel.component';
import { NhAssistantThreadComponent } from './thread/nh-assistant-thread.component';
import { NhAssistantToolCallCardComponent } from './tool-call-card/nh-assistant-tool-call-card.component';

const status: AssistantStatus = {
  enabled: true,
  agents: [{ id: 'projects', version: 1, displayNameKey: 'agents.projects', descriptionKey: 'agents.projects-description', canMutate: true }],
  limits: { maxMessageChars: 20, maxToolCallsPerTurn: 4 },
  canAdminister: false
};

const approval: ApprovalPart = {
  type: 'approval',
  approvalId: 'approval-1',
  proposalId: 'proposal-1',
  proposalHash: 'hash-1',
  toolId: 'sample-api.project.update-status',
  summary: 'Set Alpha on hold',
  argumentsPreview: '{"status":"on-hold"}',
  targets: ['Project Alpha'],
  expiresAt: new Date(Date.now() + 125_000).toISOString(),
  status: 'pending'
};

const access = new BehaviorSubject(true);

@Injectable()
class TestAccessPolicy implements NhAssistantAccessPolicy {
  canUse(): Observable<boolean> {
    return access;
  }
}

function flush(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve));
}

let api: jasmine.SpyObj<NhAssistantApiService>;

async function configure(): Promise<void> {
  api = jasmine.createSpyObj<NhAssistantApiService>('NhAssistantApiService', [
    'status', 'listConversations', 'createConversation', 'getConversation', 'deleteConversation',
    'sendMessage', 'decideApproval', 'cancel'
  ]);
  api.status.and.returnValue(of(status));
  api.listConversations.and.returnValue(of({ items: [], total: 0 }));

  TestBed.configureTestingModule({
    providers: [
      provideTranslateService({ fallbackLang: 'en' }),
      provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token', accessPolicy: TestAccessPolicy }),
      { provide: NhAssistantApiService, useValue: api }
    ]
  });
  await firstValueFrom(TestBed.inject(TranslateService).use('en'));
}

describe('NhAssistantLauncherComponent', () => {
  beforeEach(async () => {
    access.next(true);
    await configure();
  });

  async function render(): Promise<ComponentFixture<NhAssistantLauncherComponent>> {
    const fixture = TestBed.createComponent(NhAssistantLauncherComponent);
    fixture.detectChanges();
    await TestBed.inject(NhAssistantStore).initialize();
    await flush();
    fixture.detectChanges();
    await flush();
    fixture.detectChanges();
    return fixture;
  }

  it('shows an accessible button when the assistant is enabled and allowed', async () => {
    const fixture = await render();

    const button = fixture.nativeElement.querySelector('button') as HTMLButtonElement;
    expect(button).not.toBeNull();
    expect(button.getAttribute('aria-label')).toBe('Open assistant');
    expect(button.getAttribute('aria-expanded')).toBe('false');
  });

  it('is hidden when the server reports enabled false', async () => {
    api.status.and.returnValue(of({ ...status, enabled: false, agents: [] }));

    const fixture = await render();

    expect(fixture.nativeElement.querySelector('button')).toBeNull();
  });

  it('is hidden when the access policy returns false, also after a later change', async () => {
    const fixture = await render();
    expect(fixture.nativeElement.querySelector('button')).not.toBeNull();

    access.next(false);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('button')).toBeNull();
  });

  it('shows a badge while a conversation waits for approval', async () => {
    api.listConversations.and.returnValue(of({
      items: [{ id: 'c1', agentId: 'projects', title: 'Alpha', status: 'waiting-for-approval', createdAt: '', updatedAt: '' }],
      total: 1
    }));

    const fixture = await render();

    expect(fixture.nativeElement.querySelector('.badge')).not.toBeNull();
  });
});

describe('NhAssistantComposerComponent', () => {
  let fixture: ComponentFixture<NhAssistantComposerComponent>;
  let sent: string[];

  beforeEach(async () => {
    await configure();
    fixture = TestBed.createComponent(NhAssistantComposerComponent);
    fixture.componentRef.setInput('maxLength', 10);
    sent = [];
    fixture.componentInstance.send.subscribe(text => sent.push(text));
    fixture.detectChanges();
  });

  function type(text: string): HTMLTextAreaElement {
    const textarea = fixture.nativeElement.querySelector('textarea') as HTMLTextAreaElement;
    textarea.value = text;
    textarea.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    return textarea;
  }

  it('sends on Enter and clears the input', () => {
    const textarea = type('Hello');

    textarea.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', cancelable: true }));
    fixture.detectChanges();

    expect(sent).toEqual(['Hello']);
    expect(textarea.value).toBe('');
  });

  it('keeps Shift+Enter as a new line', () => {
    const textarea = type('Hello');

    const event = new KeyboardEvent('keydown', { key: 'Enter', shiftKey: true, cancelable: true });
    textarea.dispatchEvent(event);

    expect(sent).toEqual([]);
    expect(event.defaultPrevented).toBeFalse();
  });

  it('refuses a message above the maximum length', () => {
    const textarea = type('x'.repeat(11));

    textarea.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', cancelable: true }));
    fixture.detectChanges();

    expect(sent).toEqual([]);
    expect(fixture.nativeElement.textContent).toContain('The message is too long.');
    expect((fixture.nativeElement.querySelector('button.send') as HTMLButtonElement).disabled).toBeTrue();
  });

  it('is disabled while the assistant runs and offers a stop button', () => {
    let cancelled = 0;
    fixture.componentInstance.cancel.subscribe(() => cancelled++);
    fixture.componentRef.setInput('disabled', true);
    fixture.componentRef.setInput('busy', true);
    fixture.detectChanges();

    const stop = fixture.nativeElement.querySelector('button.stop') as HTMLButtonElement;
    stop.click();

    expect((fixture.nativeElement.querySelector('textarea') as HTMLTextAreaElement).disabled).toBeTrue();
    expect(stop.getAttribute('aria-label')).toBe('Stop the assistant');
    expect(cancelled).toBe(1);
  });
});

describe('NhAssistantApprovalCardComponent', () => {
  let fixture: ComponentFixture<NhAssistantApprovalCardComponent>;
  let decisions: ApprovalDecision[];

  beforeEach(async () => {
    await configure();
    fixture = TestBed.createComponent(NhAssistantApprovalCardComponent);
    fixture.componentRef.setInput('approval', approval);
    decisions = [];
    fixture.componentInstance.decide.subscribe(decision => decisions.push(decision));
    fixture.detectChanges();
  });

  function buttons(): HTMLButtonElement[] {
    return Array.from(fixture.nativeElement.querySelectorAll('.actions button'));
  }

  it('shows summary, targets and a countdown and labels its buttons', () => {
    const text = fixture.nativeElement.textContent as string;

    expect(text).toContain('Set Alpha on hold');
    expect(text).toContain('Project Alpha');
    expect(text).toMatch(/Expires in 2:0\d/);
    expect(buttons().map(button => button.getAttribute('aria-label'))).toEqual([
      'Reject: Set Alpha on hold',
      'Approve: Set Alpha on hold'
    ]);
  });

  it('emits one decision on a double click', () => {
    const approve = buttons()[1];

    approve.click();
    fixture.detectChanges();
    approve.click();

    expect(decisions).toEqual(['approve']);
    expect(approve.disabled).toBeTrue();
  });

  it('allows a new attempt after a refused decision', () => {
    buttons()[0].click();
    fixture.componentRef.setInput('deciding', true);
    fixture.detectChanges();
    fixture.componentRef.setInput('deciding', false);
    fixture.detectChanges();

    buttons()[0].click();

    expect(decisions).toEqual(['reject', 'reject']);
  });

  it('disables the decision once the approval expired', () => {
    fixture.componentRef.setInput('approval', { ...approval, expiresAt: new Date(Date.now() - 1000).toISOString() });
    fixture.detectChanges();

    expect(buttons().length).toBe(0);
    expect(fixture.nativeElement.textContent).toContain('This approval has expired.');
  });

  it('formats the countdown', () => {
    expect(formatNhAssistantCountdown(65_000)).toBe('1:05');
    expect(formatNhAssistantCountdown(3_725_000)).toBe('1:02:05');
    expect(formatNhAssistantCountdown(-5)).toBe('0:00');
  });
});

describe('NhAssistantToolCallCardComponent', () => {
  const part: ToolCallPart = {
    type: 'tool-call',
    invocationId: 'i1',
    toolId: 'sample-api.project.get-by-id',
    toolVersion: 1,
    displayName: 'Get project',
    status: 'failed',
    argumentsPreview: '{"id":"p1"}',
    resultPreview: null,
    resultCode: 'api-bridge-forbidden'
  };

  beforeEach(configure);

  it('translates the result code and toggles the details', () => {
    const fixture = TestBed.createComponent(NhAssistantToolCallCardComponent);
    fixture.componentRef.setInput('part', part);
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;
    const toggle = element.querySelector('button.toggle') as HTMLButtonElement;
    expect(element.textContent).toContain('You do not have permission for this action.');
    expect(element.textContent).toContain('Failed');
    expect(toggle.getAttribute('aria-expanded')).toBe('false');

    toggle.click();
    fixture.detectChanges();

    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect((element.querySelector('.details') as HTMLElement).hidden).toBeFalse();
  });

  it('falls back to the raw code for an unknown result code', () => {
    const fixture = TestBed.createComponent(NhAssistantToolCallCardComponent);
    fixture.componentRef.setInput('part', { ...part, resultCode: 'custom-failure' });
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.result-code').textContent.trim()).toBe('custom-failure');
  });
});

describe('NhAssistantThreadComponent', () => {
  beforeEach(configure);

  it('is a polite live log and never renders a script tag from model text', () => {
    const fixture = TestBed.createComponent(NhAssistantThreadComponent);
    const messages: Message[] = [
      { id: 'u1', role: 'user', createdAt: '', parts: [{ type: 'text', text: '<b>user</b>' }] },
      { id: 'a1', role: 'assistant', createdAt: '', parts: [{ type: 'text', text: 'Hi <script>window.__nhThreadPwned = 1</script> **there**' }] }
    ];
    fixture.componentRef.setInput('messages', messages);
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;
    const log = element.querySelector('[role="log"]');
    expect(log?.getAttribute('aria-live')).toBe('polite');
    expect(element.querySelector('script')).toBeNull();
    expect(element.querySelector('.markdown strong')?.textContent).toBe('there');
    expect(element.querySelector('.plain')?.textContent).toBe('<b>user</b>');
    expect((window as unknown as Record<string, unknown>)['__nhThreadPwned']).toBeUndefined();
  });

  it('switches to CDK virtual scrolling above 200 messages', () => {
    const fixture = TestBed.createComponent(NhAssistantThreadComponent);
    const messages: Message[] = Array.from({ length: 201 }, (_, index) => ({
      id: `m${index}`,
      role: 'assistant',
      createdAt: '',
      parts: [{ type: 'text', text: `Message ${index}` }]
    }));
    fixture.componentRef.setInput('messages', messages);
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('cdk-virtual-scroll-viewport')).not.toBeNull();
    expect(element.querySelectorAll('.message').length).toBeLessThan(201);
  });
});

describe('NhAssistantPanelComponent', () => {
  @Component({
    standalone: true,
    imports: [NhAssistantPanelComponent],
    template: '<nh-assistant-panel />'
  })
  class HostComponent {}

  let stream: Subject<NhAssistantSseEvent>;
  let overlay: HTMLElement;
  let fixture: ComponentFixture<HostComponent>;

  beforeEach(async () => {
    access.next(true);
    await configure();
    stream = new Subject<NhAssistantSseEvent>();
    api.createConversation.and.returnValue(of({
      id: 'c1', agentId: 'projects', agentVersion: 1, title: null, status: 'idle',
      createdAt: '', updatedAt: '', messages: [], pendingApproval: null
    }));
    api.sendMessage.and.returnValue(stream);
    fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    overlay = TestBed.inject(OverlayContainer).getContainerElement();
  });

  async function open(): Promise<void> {
    TestBed.inject(NhAssistantPanelService).open();
    await TestBed.inject(NhAssistantStore).initialize();
    await flush();
    fixture.detectChanges();
  }

  it('opens as a labelled drawer with an empty state and focusable composer', async () => {
    await open();

    const drawer = overlay.querySelector('.drawer') as HTMLElement;
    expect(drawer.getAttribute('role')).toBe('dialog');
    expect(drawer.textContent).toContain('How can I help?');
    expect(overlay.querySelector('textarea[data-nh-assistant-autofocus]')).not.toBeNull();
    for (const button of Array.from(drawer.querySelectorAll('button'))) {
      expect(button.getAttribute('aria-label')).withContext(button.outerHTML).toBeTruthy();
    }
  });

  it('streams an answer into the thread and shows a translated error bar', async () => {
    await open();
    const store = TestBed.inject(NhAssistantStore);

    void store.send('Hello');
    await flush();
    stream.next({ type: 'turn.started', data: { turnId: 't1', userMessageId: 'u1', assistantMessageId: 'a1' } });
    stream.next({ type: 'message.delta', data: { messageId: 'a1', text: 'Hi **there**' } });
    fixture.detectChanges();
    expect(overlay.querySelector('.markdown strong')?.textContent).toBe('there');

    stream.next({ type: 'error', data: { code: 'assistant-server', messageKey: 'nh-assistant.errors.assistant-server' } });
    stream.complete();
    fixture.detectChanges();

    const alert = overlay.querySelector('[role="alert"]');
    expect(alert?.textContent).toContain('The assistant could not complete the request.');
  });

  it('shows the unavailable state when the assistant is switched off', async () => {
    api.status.and.returnValue(of({ ...status, enabled: false, agents: [] }));

    await open();

    expect(overlay.querySelector('.drawer')?.textContent).toContain('The assistant is not available');
    expect(overlay.querySelector('textarea')).toBeNull();
  });
});
