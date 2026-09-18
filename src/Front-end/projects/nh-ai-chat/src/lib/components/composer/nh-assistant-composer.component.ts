import { ChangeDetectionStrategy, Component, computed, effect, input, output, signal, untracked } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { NhAssistantIconComponent } from '../../internal/nh-assistant-icon.component';

let nextId = 0;

/**
 * Message input. Enter sends, Shift+Enter inserts a new line. While the assistant runs
 * the send button becomes a stop button.
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
  /** Disables typing and sending, for example while a turn runs or waits for approval. */
  readonly disabled = input(false);
  /** Shows the stop button instead of the send button. */
  readonly busy = input(false);
  readonly maxLength = input<number | null>(null);
  /** Text to put back into the input, for example a message the server did not accept. */
  readonly restore = input<string | null>(null);

  readonly send = output<string>();
  readonly cancel = output<void>();
  readonly restored = output<void>();

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
  readonly canSend = computed(() => !this.disabled() && this.length() > 0 && !this.tooLong());

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

  submit(): void {
    if (!this.canSend()) {
      return;
    }

    const text = this.text().trim();
    this.text.set('');
    this.send.emit(text);
  }
}
