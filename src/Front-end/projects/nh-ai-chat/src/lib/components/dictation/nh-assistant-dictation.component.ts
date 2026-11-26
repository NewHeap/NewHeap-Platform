import { Component, DestroyRef, Injector, effect, inject, input, runInInjectionContext, signal } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { NH_ASSISTANT_CONFIG } from '../../nh-assistant.config';
import { NhAssistantStore } from '../../services/nh-assistant.store';
import { normalizeNhAssistantClientContext } from '../../services/nh-assistant-page-context';

/** Optional microphone control. The host owns authentication, transcription and usage accounting. */
@Component({
  selector: 'nh-assistant-dictation',
  standalone: true,
  imports: [TranslatePipe],
  template: `
    @if (config.dictation) {
      @if (state() === 'idle') {
        <button type="button" [disabled]="disabled()" (click)="start()">{{ 'nh-assistant.dictation.start' | translate }}</button>
      } @else {
        @if (state() === 'recording') {
          <button type="button" (click)="stop()">{{ 'nh-assistant.dictation.stop' | translate }}</button>
        }
        <button type="button" (click)="cancel()">{{ 'nh-assistant.dictation.cancel' | translate }}</button>
        <span role="status">{{ ('nh-assistant.dictation.' + state()) | translate }}</span>
      }
      @if (error()) { <span role="alert">{{ 'nh-assistant.dictation.failed' | translate }}</span> }
    }
  `,
  styles: [`:host { display: inline-flex; flex-wrap: wrap; gap: .5rem; align-items: center; }
    button { font: inherit; color: inherit; background: transparent; border: 1px solid currentColor; border-radius: .5rem; padding: .4rem .6rem; min-height: 32px; cursor: pointer; }
    button:disabled { opacity: .5; cursor: default; }`]
})
export class NhAssistantDictationComponent {
  readonly config = inject(NH_ASSISTANT_CONFIG);
  private readonly store = inject(NhAssistantStore);
  private readonly injector = inject(Injector);
  readonly disabled = input(false);
  readonly state = signal<'idle' | 'opening' | 'recording' | 'transcribing'>('idle');
  readonly error = signal(false);
  private controller: AbortController | null = null;
  private recorder: MediaRecorder | null = null;
  private stream: MediaStream | null = null;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private conversation: string | null = null;
  private agent: string | null = null;

  constructor() {
    inject(DestroyRef).onDestroy(() => this.cancel());
    effect(() => {
      const conversation = this.store.activeConversation()?.id ?? null;
      const agent = this.store.selectedAgentId();
      if (this.disabled() || !this.store.enabled() || conversation !== this.conversation || agent !== this.agent) {
        this.cancel();
      }
    });
  }

  async start(): Promise<void> {
    if (!this.config.dictation || this.disabled() || this.state() !== 'idle') {
      return;
    }
    this.error.set(false);
    const controller = new AbortController();
    this.controller = controller;
    this.conversation = this.store.activeConversation()?.id ?? null;
    this.agent = this.store.selectedAgentId();
    const conversationId = this.conversation;
    this.state.set('opening');
    try {
      if (typeof MediaRecorder === 'undefined' || !navigator.mediaDevices?.getUserMedia) {
        throw new Error('unsupported');
      }
      const stateScope = await this.scope();
      if (await runInInjectionContext(this.injector, () => this.config.dictation?.isAvailable?.(controller.signal)) === false) {
        throw new Error('unavailable');
      }
      const pageContext = normalizeNhAssistantClientContext(await runInInjectionContext(this.injector, () => this.config.getPageContext?.()));
      const mimeType = ['audio/webm', 'audio/mp4', 'audio/ogg'].find(type => MediaRecorder.isTypeSupported(type));
      if (!mimeType) {
        throw new Error('unsupported');
      }
      if (controller.signal.aborted) {
        return;
      }
      if (stateScope !== await this.scope()) {
        throw new Error('scope-changed');
      }
      const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
      if (controller.signal.aborted) { stream.getTracks().forEach(track => track.stop()); return; }
      this.stream = stream;
      const recorder = new MediaRecorder(stream, { mimeType, audioBitsPerSecond: 64000 });
      this.recorder = recorder;
      const chunks: Blob[] = [];
      let bytes = 0;
      recorder.ondataavailable = event => {
        bytes += event.data.size;
        if (bytes > 4 * 1024 * 1024) { this.cancel(); this.error.set(true); return; }
        chunks.push(event.data);
      };
      recorder.onerror = () => { this.cancel(); this.error.set(true); };
      recorder.onstop = async () => {
        if (this.controller !== controller) { stream.getTracks().forEach(track => track.stop()); return; }
        this.releaseMicrophone();
        if (controller.signal.aborted) {
          return;
        }
        this.state.set('transcribing');
        try {
          if (stateScope !== await this.scope()) {
            throw new Error('scope-changed');
          }
          const text = await runInInjectionContext(this.injector, () => this.config.dictation!.transcribe({
            audio: new Blob(chunks, { type: mimeType }), requestId: crypto.randomUUID(), conversationId, pageContext, signal: controller.signal
          }));
          if (controller.signal.aborted || stateScope !== await this.scope() || conversationId !== (this.store.activeConversation()?.id ?? null)) {
            return;
          }
          const current = this.store.unsentMessage()?.text ?? '';
          this.store.keepUnsentMessage({ text: [current, text.trim()].filter(Boolean).join('\n'), prepared: false });
        } catch {
          if (!controller.signal.aborted) {
            this.error.set(true);
          }
        } finally {
          if (this.controller === controller) { this.controller = null; this.state.set('idle'); }
        }
      };
      recorder.start(1000);
      this.state.set('recording');
      this.timer = setTimeout(() => this.stop(), 120000);
    } catch {
      if (!controller.signal.aborted) { this.cancel(); this.error.set(true); }
    }
  }

  stop(): void {
    if (this.recorder?.state === 'recording') {
      this.recorder.stop();
    }
  }

  cancel(): void {
    this.controller?.abort();
    this.controller = null;
    this.stop();
    this.releaseMicrophone();
    this.state.set('idle');
  }

  private releaseMicrophone(): void {
    if (this.timer) {
      clearTimeout(this.timer);
    }
    this.timer = null;
    this.stream?.getTracks().forEach(track => track.stop());
    this.stream = null;
    this.recorder = null;
  }

  private async scope(): Promise<string | null> {
    return await runInInjectionContext(this.injector, () => this.config.getStateScope?.()) ?? null;
  }
}
