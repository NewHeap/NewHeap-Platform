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
import type {
  NhAssistantAttachment,
  NhAssistantComposerDraft,
  NhAssistantUnsentMessage
} from '../../services/nh-assistant.store';

let nextId = 0;

/**
 * Message input. Enter sends when sending is available; Shift+Enter inserts a new line.
 * Drafting remains available while the assistant runs or waits for approval. With `uploads`, an
 * attach button chooses files for the next message; the host uploads them and passes them back as
 * `attachments`, which show their progress, or why they cannot be attached, at the control. Sending
 * waits while a file uploads. A prepared draft passed as `restore` shows in the input without being
 * sent, and never replaces what the person typed unless it says so. With `unsent` the host keeps the
 * text that is not sent yet, so it outlives the composer, as the panel does with
 * `NhAssistantStore.unsentMessage()`.
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
  /**
   * Announces why sending is temporarily unavailable: a running turn, a turn that waits for an
   * approval, or one that waits for the answer to a question the agent asked.
   */
  readonly status = input<'running' | 'waiting-for-approval' | 'waiting-for-answer' | null>(null);
  /** Name of the participant whose turn runs or waits, or who answers the question, when it is someone else. */
  readonly statusActor = input<string | null>(null);
  /** False when the user may not stop the running turn, such as another participant's turn. */
  readonly canStop = input(true);
  readonly maxLength = input<number | null>(null);
  /**
   * Text to put in the input. A string is the person's own text, such as a message the server did not
   * accept, and replaces what the input holds. An `NhAssistantComposerDraft`, such as
   * `NhAssistantStore.pendingDraft()`, is a prepared draft: it keeps what the person typed unless its
   * `replace` is set, and replaces a prepared draft the person did not change.
   */
  readonly restore = input<string | NhAssistantComposerDraft | null>(null);
  /**
   * The unsent message the host keeps for the open conversation, for example
   * `NhAssistantStore.unsentMessage()`, with `unsentChange` passed back to
   * `NhAssistantStore.keepUnsentMessage`. The input shows it whenever it changes: when the composer
   * shows again after the drawer closed, and when another conversation opens; a prepared draft in it
   * still gives way to the next one. `null` means nothing is kept and empties the input. Left unbound,
   * the composer keeps its text itself, which ends with the composer.
   */
  readonly unsent = input<NhAssistantUnsentMessage | null | undefined>(undefined);
  /**
   * What the person may attach, for example `NhAssistantStore.uploads()`; `null` hides the attach
   * button.
   */
  readonly uploads = input<AgentUploads | null>(null);
  /** The attachments of the next message, for example `NhAssistantStore.attachments()`. */
  readonly attachments = input<readonly NhAssistantAttachment[]>([]);

  readonly send = output<string>();
  readonly cancel = output<void>();
  /**
   * The composer handled `restore`: it shows the text, or kept what the person typed instead of a
   * prepared draft. The host then clears it, for example with `NhAssistantStore.consumeRestoredDraft`.
   */
  readonly restored = output<void>();
  /**
   * The text in the input changed: the person typed, sent the message, or the composer took a restored
   * message or a draft. `null` once the input is empty, as after sending. Pass it to
   * `NhAssistantStore.keepUnsentMessage` with `unsent` bound.
   */
  readonly unsentChange = output<NhAssistantUnsentMessage | null>();
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
  /** True while the input holds the person's own text: typed, changed or put back after a refusal. */
  private ownText = false;
  /** The `unsent` and `restore` values the composer handled last, so each is handled once. */
  private shownUnsent: NhAssistantUnsentMessage | null | undefined = undefined;
  private takenRestore: string | NhAssistantComposerDraft | null = null;

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
    // One effect for both, the kept message first: a draft that arrives together with another
    // conversation's message, such as a draft for a new chat, is decided against the text it lands on.
    effect(() => {
      const unsent = this.unsent();
      const restore = this.restore();
      untracked(() => {
        if (unsent !== this.shownUnsent) {
          this.shownUnsent = unsent;
          if (unsent !== undefined) {
            this.showUnsent(unsent);
          }
        }
        if (restore !== this.takenRestore) {
          this.takenRestore = restore;
          if (restore !== null) {
            this.takeRestore(restore);
          }
        }
      });
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
    const value = (event.target as HTMLTextAreaElement).value;
    this.text.set(value);
    this.ownText = value.trim().length > 0;
    this.reportUnsent();
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
    this.show('');
    this.ownText = false;
    // The host forgets the unsent message before it learns of the message, which may be refused and come back.
    this.reportUnsent();
    this.send.emit(text);
  }

  /**
   * Shows the person's own text as it is, and a prepared draft unless the person typed something it
   * does not replace; either way the host learns that the composer handled it.
   */
  private takeRestore(draft: string | NhAssistantComposerDraft): void {
    if (typeof draft === 'string') {
      this.show(draft);
      this.ownText = draft.trim().length > 0;
    } else if (draft.replace || !this.ownText) {
      this.show(draft.text);
      this.ownText = false;
    }
    this.reportUnsent();
    this.restored.emit();
  }

  /** Shows the message the host kept, such as the one of the conversation that opened. */
  private showUnsent(message: NhAssistantUnsentMessage | null): void {
    const text = message?.text ?? '';
    this.show(text);
    this.ownText = message !== null && !message.prepared && text.trim().length > 0;
  }

  /**
   * Puts `text` in the input. The `[value]` binding writes the input only when `text` differs from what
   * it rendered last, so text typed and sent between two renders, as fast automated typing does, would
   * stay in the input after the empty text it rendered before.
   */
  private show(text: string): void {
    this.text.set(text);
    const input = this.textarea().nativeElement;
    if (input.value !== text) {
      input.value = text;
    }
  }

  /** Tells the host what the input holds now, so it keeps the text beyond this composer. */
  private reportUnsent(): void {
    const text = this.text();
    if (text.length === 0) {
      this.unsentChange.emit(null);
      return;
    }

    // A draft the person did not change stays prepared; blank space is no draft.
    const prepared = !this.ownText && text.trim().length > 0;
    this.unsentChange.emit({ text, prepared });
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
