import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  HostListener,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
  viewChild
} from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { NhAssistantIconComponent } from '../../internal/nh-assistant-icon.component';

let nextId = 0;

/**
 * Message input. Enter sends when sending is available; Shift+Enter inserts a new line.
 * Drafting remains available while the assistant runs or waits for approval.
 */
@Component({
  selector: 'nh-assistant-composer',
  standalone: true,
  imports: [TranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-composer.component.html',
  styleUrl: './nh-assistant-composer.component.scss'
})
export class NhAssistantComposerComponent {
  /** Disables the editor only when the conversation itself cannot accept a draft. */
  readonly disabled = input(false);
  /** Blocks sending without disabling the editor or clearing its draft. */
  readonly sendDisabled = input(false);
  /** Shows the stop button instead of the send button. */
  readonly busy = input(false);
  /** Announces why sending is temporarily unavailable. */
  readonly status = input<'running' | 'waiting-for-approval' | null>(null);
  readonly maxLength = input<number | null>(null);
  /** Text to put back into the input, for example a message the server did not accept. */
  readonly restore = input<string | null>(null);

  readonly send = output<string>();
  readonly cancel = output<void>();
  readonly restored = output<void>();

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly textarea = viewChild.required<ElementRef<HTMLTextAreaElement>>('textarea');
  private ownedFocus = false;
  private previousBusy = false;

  readonly inputId = `nh-assistant-composer-${nextId++}`;
  readonly hintId = `${this.inputId}-hint`;
  readonly text = signal('');
  readonly length = computed(() => this.text().trim().length);
  readonly tooLong = computed(() => {
    const max = this.maxLength();
    return max !== null && this.length() > max;
  });
  readonly showCounter = computed(() => {
    const max = this.maxLength();
    return max !== null && this.length() >= max * 0.8;
  });
  readonly canSend = computed(() =>
    !this.disabled() && !this.sendDisabled() && this.length() > 0 && !this.tooLong());

  constructor() {
    effect(() => {
      const draft = this.restore();
      if (draft !== null) {
        untracked(() => {
          this.text.set(draft);
          this.restored.emit();
        });
      }
    });

    effect(() => {
      const busy = this.busy();
      if (this.previousBusy && !busy) {
        queueMicrotask(() => this.restoreOwnedFocus());
      }
      this.previousBusy = busy;
    });
  }

  @HostListener('focusin')
  onFocusIn(): void {
    this.ownedFocus = true;
  }

  @HostListener('focusout', ['$event'])
  onFocusOut(event: FocusEvent): void {
    const next = event.relatedTarget;
    if (next instanceof Node && !this.host.nativeElement.contains(next)) {
      this.ownedFocus = false;
    }
  }

  onInput(event: Event): void {
    this.text.set((event.target as HTMLTextAreaElement).value);
  }

  onKeydown(event: KeyboardEvent): void {
    if (event.key !== 'Enter' || event.shiftKey || event.isComposing) {
      return;
    }

    event.preventDefault();
    this.submit();
  }

  onSubmit(event: SubmitEvent): void {
    event.preventDefault();
    this.submit();
    queueMicrotask(() => this.textarea().nativeElement.focus({ preventScroll: true }));
  }

  submit(): void {
    if (!this.canSend()) {
      return;
    }

    const text = this.text().trim();
    this.text.set('');
    this.send.emit(text);
  }

  private restoreOwnedFocus(): void {
    if (!this.ownedFocus || matchMedia('(pointer: coarse)').matches) {
      return;
    }

    const active = document.activeElement;
    if (active === document.body || active === null || !active.isConnected) {
      this.textarea().nativeElement.focus({ preventScroll: true });
    }
  }
}
