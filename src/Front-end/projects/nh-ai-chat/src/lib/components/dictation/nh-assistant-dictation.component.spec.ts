import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideTranslateService } from '@ngx-translate/core';
import { NH_ASSISTANT_CONFIG } from '../../nh-assistant.config';
import { NhAssistantStore } from '../../services/nh-assistant.store';
import { NhAssistantDictationComponent } from './nh-assistant-dictation.component';

describe('Host dictation', () => {
  const originalRecorder = window.MediaRecorder;
  let recorder: FakeRecorder;
  let stopTrack: jasmine.Spy;
  let scope: string;
  let finish: (value: string) => void;
  let transcribe: jasmine.Spy;
  const draft = signal({ text: 'Already typed', prepared: false });
  const keep = jasmine.createSpy('keep').and.callFake(value => draft.set(value));

  class FakeRecorder {
    static isTypeSupported(): boolean { return true; }
    state = 'inactive';
    ondataavailable?: (event: { data: Blob }) => void;
    onstop?: () => Promise<void>;
    onerror?: () => void;
    finished?: Promise<void>;
    constructor() { recorder = this; }
    start(): void { this.state = 'recording'; }
    stop(): void {
      this.state = 'inactive';
      this.ondataavailable?.({ data: new Blob(['audio']) });
      this.finished = this.onstop?.();
    }
  }

  beforeEach(() => {
    Object.defineProperty(window, 'MediaRecorder', { value: FakeRecorder, configurable: true, writable: true });
    scope = 'user:organisation';
    stopTrack = jasmine.createSpy('stopTrack');
    spyOn(navigator.mediaDevices, 'getUserMedia').and.resolveTo({ getTracks: () => [{ stop: stopTrack }] } as unknown as MediaStream);
    draft.set({ text: 'Already typed', prepared: false });
    keep.calls.reset();
    transcribe = jasmine.createSpy('transcribe').and.callFake(() => new Promise<string>(resolve => finish = resolve));
    TestBed.configureTestingModule({ providers: [provideTranslateService(),
      { provide: NH_ASSISTANT_CONFIG, useValue: { dictation: { transcribe }, getStateScope: () => scope,
        getPageContext: () => ({ route: '/record', tab: 'materials', entities: [{ type: 'record', id: '1' }] }) } },
      { provide: NhAssistantStore, useValue: { activeConversation: signal(null), selectedAgentId: signal('agent'),
        enabled: signal(true), unsentMessage: draft, keepUnsentMessage: keep } }
    ] });
  });

  afterEach(() => { Object.defineProperty(window, 'MediaRecorder', { value: originalRecorder, configurable: true, writable: true }); });

  async function recording(): Promise<NhAssistantDictationComponent> {
    const fixture = TestBed.createComponent(NhAssistantDictationComponent);
    fixture.detectChanges();
    await fixture.componentInstance.start();
    expect(fixture.componentInstance.state()).toBe('recording');
    return fixture.componentInstance;
  }

  it('captures context, releases the microphone and appends editable text without sending', async () => {
    const component = await recording();
    component.stop();
    await Promise.resolve();
    await Promise.resolve();
    expect(stopTrack).toHaveBeenCalled();
    expect(transcribe).toHaveBeenCalledOnceWith(jasmine.objectContaining({ pageContext: jasmine.objectContaining({ tab: 'materials' }) }));
    draft.set({ text: 'Changed while waiting', prepared: false });
    finish(' Dictated text ');
    await recorder.finished;
    expect(draft().text).toBe('Changed while waiting\nDictated text');
    expect(component.state()).toBe('idle');
  });

  it('aborts transport and ignores a late result after cancellation', async () => {
    const component = await recording();
    component.stop();
    await Promise.resolve();
    await Promise.resolve();
    component.cancel();
    expect(transcribe.calls.mostRecent().args[0].signal.aborted).toBeTrue();
    finish('Must not appear');
    await recorder.finished;
    expect(keep).not.toHaveBeenCalled();
  });

  it('never uploads into a different account or organisation', async () => {
    const component = await recording();
    scope = 'other:user';
    component.stop();
    await recorder.finished;
    expect(transcribe).not.toHaveBeenCalled();
    expect(stopTrack).toHaveBeenCalled();
    expect(keep).not.toHaveBeenCalled();
  });

  it('checks host availability before asking for microphone permission', async () => {
    TestBed.inject(NH_ASSISTANT_CONFIG).dictation!.isAvailable = async () => false;
    const fixture = TestBed.createComponent(NhAssistantDictationComponent);
    fixture.detectChanges();
    await fixture.componentInstance.start();
    expect(navigator.mediaDevices.getUserMedia).not.toHaveBeenCalled();
    expect(fixture.componentInstance.state()).toBe('idle');
    expect(fixture.componentInstance.error()).toBeTrue();
  });
});
