import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, computed, effect, inject, input, signal, untracked, viewChild } from '@angular/core';
import { NavigationStart, Router } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { NhAssistantIconComponent } from '../../internal/nh-assistant-icon.component';
import { NhAssistantTokensDirective } from '../../internal/nh-assistant-tokens';
import { NH_ASSISTANT_CONFIG } from '../../nh-assistant.config';
import { NhAssistantStore } from '../../services/nh-assistant.store';
import { NhAssistantScreenshotCapture } from './nh-assistant-screenshot-capture';

let nextId = 0;

/** Internal, consent-first screenshot control shared by drawer and page panels. */
@Component({
  selector: 'nh-assistant-screenshot',
  standalone: true,
  hostDirectives: [NhAssistantTokensDirective],
  imports: [TranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-screenshot.component.html',
  styleUrl: './nh-assistant-screenshot.component.scss'
})
export class NhAssistantScreenshotComponent {
  readonly store = inject(NhAssistantStore);
  private readonly config = inject(NH_ASSISTANT_CONFIG);
  private readonly capture = inject(NhAssistantScreenshotCapture);
  private readonly dialog = viewChild<ElementRef<HTMLDialogElement>>('dialog');
  private controller?: AbortController;
  private file: File | null = null;
  private scope: string | null = null;
  readonly reason = signal<string | null>(null);
  readonly disabled = input(false);
  readonly titleId = `nh-assistant-screenshot-${nextId++}`;
  readonly phase = signal<'closed' | 'consent' | 'preparing' | 'capturing' | 'preview' | 'error'>('closed');
  readonly preview = signal<string | null>(null);
  readonly error = signal('failed');
  readonly available = computed(() => this.config.screenshots === true
    && this.store.enabled() && this.store.uploads()?.acceptedMediaTypes.includes('image/png') === true);
  readonly full = computed(() => this.store.attachments().filter(item => item.state !== 'failed').length
    >= (this.store.uploads()?.maxFiles ?? 0));
  readonly busy = computed(() => this.phase() === 'preparing' || this.phase() === 'capturing');

  constructor() {
    // A preview belongs to this exact conversation and account; never carry it to another one.
    effect(() => {
      const scope = this.currentScope();
      const available = this.available();
      untracked(() => {
        if (!available || (this.scope !== null && scope !== this.scope)) {
          this.close();
        }
      });
    });
    const requests = this.store.pageScreenshotRequests.subscribe(request => {
      if (request.conversationId === this.store.activeConversation()?.id && this.phase() === 'closed') {
        this.open();
        if (this.phase() === 'consent') {
          this.reason.set(request.reason);
        }
      }
    });
    const navigation = inject(Router, { optional: true })?.events.subscribe(event => {
      if (event instanceof NavigationStart) {
        this.close();
      }
    });
    inject(DestroyRef).onDestroy(() => {
      navigation?.unsubscribe();
      requests.unsubscribe();
      this.close();
    });
  }

  open(): void {
    if (!this.available() || this.disabled() || this.full()) {
      return;
    }
    this.scope = this.currentScope();
    this.phase.set('consent');
    this.dialog()?.nativeElement.showModal();
  }

  async take(): Promise<void> {
    if (!['consent', 'error'].includes(this.phase()) || !this.available() || this.disabled()) {
      return;
    }
    const controller = new AbortController();
    this.controller = controller;
    this.phase.set('preparing');
    // Import, resource loading and rendering are all bounded; cancellation discards late results.
    const timeout = setTimeout(() => controller.abort(new Error('Screenshot timed out.')), 20_000);
    try {
      const file = await Promise.race([
        this.capture.capture(() => {
          if (!controller.signal.aborted) {
            this.phase.set('capturing');
          }
        }, controller.signal),
        new Promise<never>((_, reject) => controller.signal.addEventListener('abort', () => reject(controller.signal.reason), { once: true }))
      ]);
      if (this.controller !== controller) {
        return;
      }
      if (file.size > (this.store.uploads()?.maxFileBytes ?? 0)) {
        this.error.set('too-large');
        this.phase.set('error');
        return;
      }
      this.file = file;
      this.preview.set(URL.createObjectURL(file));
      this.phase.set('preview');
    } catch {
      if (this.controller === controller) {
        this.error.set('failed');
        this.phase.set('error');
      }
    } finally {
      clearTimeout(timeout);
    }
  }

  share(): void {
    if (this.phase() !== 'preview' || !this.file || !this.available() || this.disabled() || this.full()) {
      return;
    }
    const file = this.file;
    this.close();
    // The existing store owns upload progress, errors, limits and the conversation binding.
    void this.store.attach([file]);
  }

  close(): void {
    this.scope = null;
    this.reason.set(null);
    const controller = this.controller;
    this.controller = undefined;
    controller?.abort();
    const preview = this.preview();
    if (preview) {
      URL.revokeObjectURL(preview);
    }
    this.preview.set(null);
    this.file = null;
    this.phase.set('closed');
    this.dialog()?.nativeElement.close();
  }

  private currentScope(): string {
    return JSON.stringify([this.store.activeConversation()?.id, this.store.activeConversation()?.currentActorId, this.store.selectedAgentId()]);
  }
}
