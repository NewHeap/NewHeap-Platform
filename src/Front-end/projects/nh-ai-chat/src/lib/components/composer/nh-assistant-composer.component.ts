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
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { AgentUploads } from '../../models/assistant-api.models';
import {
  nhAssistantAcceptAttribute,
  nhAssistantFileTypeLabel,
  nhAssistantFormatFileSize
} from '../../internal/nh-assistant-files';
import { NhAssistantIconComponent } from '../../internal/nh-assistant-icon.component';
import { NhAssistantTranslatePipe } from '../../internal/nh-assistant-translate.pipe';
import type { NhAssistantAttachment } from '../../services/nh-assistant.store';

let nextId = 0;

/**
 * Message input. Enter sends when sending is available; Shift+Enter inserts a new line.
 * Drafting remains available while the assistant runs or waits for approval. With `uploads`, an
 * attach button chooses files for the next message; the host uploads them and passes them back as
 * `attachments`, which show their progress, or why they cannot be attached, at the control. Sending
 * waits while a file uploads.
 */
@Component({
  selector: 'nh-assistant-composer',
  standalone: true,
  imports: [TranslatePipe, NhAssistantIconComponent, NhAssistantTranslatePipe],
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
  /** Keeps the send action available next to Stop so the host can queue messages. */
  readonly queueWhileBusy = input(false);
  /** Announces why sending is temporarily unavailable. */
  readonly status = input<'running' | 'waiting-for-approval' | null>(null);
  /** Name of the participant whose turn runs or waits, when it is someone else. */
  readonly statusActor = input<string | null>(null);
  /** False when the user may not stop the running turn, such as another participant's turn. */
  readonly canStop = input(true);
  readonly maxLength = input<number | null>(null);
  /** Text to put back into the input, for example a message the server did not accept. */
  readonly restore = input<string | null>(null);
  /**
   * What the person may attach, for example `NhAssistantStore.uploads()`; `null` hides the attach
   * button.
   */
  readonly uploads = input<AgentUploads | null>(null);
  /** The attachments of the next message, for example `NhAssistantStore.attachments()`. */
  readonly attachments = input<readonly NhAssistantAttachment[]>([]);

  readonly send = output<string>();
  readonly cancel = output<void>();
  readonly restored = output<void>();
  /** Files the person chose to attach, for example for `NhAssistantStore.attach`. */
  readonly attach = output<File[]>();
  /** The id of an attachment the person removed, for example for `NhAssistantStore.removeAttachment`. */
  readonly removeAttachment = output<string>();

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly translate = inject(TranslateService, { optional: true });
  private readonly textarea = viewChild.required<ElementRef<HTMLTextAreaElement>>('textarea');
  private readonly attachButton = viewChild<ElementRef<HTMLButtonElement>>('attachButton');
  private ownedFocus = false;
  private previousBusy = false;

  readonly inputId = `nh-assistant-composer-${nextId++}`;
  readonly hintId = `${this.inputId}-hint`;
  readonly attachHintId = `${this.inputId}-attach-hint`;
  readonly attachmentsId = `${this.inputId}-attachments`;
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
  /** True while a file of the next message still uploads. */
  readonly uploading = computed(() => this.attachments().some(attachment => attachment.state === 'uploading'));
  readonly canSend = computed(() =>
    !this.disabled() && !this.sendDisabled() && this.length() > 0 && !this.tooLong() && !this.uploading());
  /** The `accept` attribute of the file input, with the extensions the application mapped. */
  readonly accept = computed(() => {
    const uploads = this.uploads();
    return nhAssistantAcceptAttribute(uploads?.acceptedMediaTypes ?? [], uploads?.mappedExtensions);
  });
  /** The values the attachment texts name: the accepted types, the largest size and the most files. */
  readonly uploadParams = computed(() => {
    const uploads = this.uploads();
    return {
      types: (uploads?.acceptedMediaTypes ?? []).map(nhAssistantFileTypeLabel).join(', '),
      max: uploads ? this.size(uploads.maxFileBytes) : '',
      count: uploads?.maxFiles ?? 0
    };
  });

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

  /** Hands the chosen files to the host and empties the input, so the same file can be chosen again. */
  onFilesChosen(event: Event): void {
    const input = event.target as HTMLInputElement;
    const files = Array.from(input.files ?? []);
    input.value = '';
    if (files.length > 0) {
      this.attach.emit(files);
    }
  }

  /** Removes an attachment and keeps the keyboard in the composer, on the attach button. */
  remove(attachment: NhAssistantAttachment): void {
    this.removeAttachment.emit(attachment.id);
    queueMicrotask(() => (this.attachButton()?.nativeElement ?? this.textarea().nativeElement).focus({ preventScroll: true }));
  }

  /** The translation keys of why an attachment failed, from the composer's own text to the generic one. */
  errorKeys(attachment: NhAssistantAttachment): string[] {
    const error = attachment.error;
    if (!error) {
      return [];
    }

    return [
      `nh-assistant.composer.attachment-errors.${error.code}`,
      error.messageKey,
      `nh-assistant.errors.${error.code}`,
      'nh-assistant.errors.generic'
    ];
  }

  percent(attachment: NhAssistantAttachment): number {
    return Math.round(attachment.progress * 100);
  }

  /** A size in bytes in the current language, such as `1.4 MB`. */
  size(bytes: number): string {
    return nhAssistantFormatFileSize(bytes, this.translate?.getCurrentLang());
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
