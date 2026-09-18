import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, output, signal } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import {
  AssistantAddressForm,
  AssistantPreferences,
  AssistantResponseLength,
  AssistantStyle
} from '../../models/assistant-admin.models';
import { NhAssistantIconComponent } from '../../internal/nh-assistant-icon.component';
import { NhAssistantTranslatePipe } from '../../internal/nh-assistant-translate.pipe';
import { NhAssistantApiService } from '../../services/nh-assistant-api.service';
import { NhAssistantError } from '../../services/nh-assistant.store';
import { NhAssistantApiError, nhAssistantErrorMessageKey } from '../../services/nh-assistant-transport';

/** Maximum length of the user's own instructions. */
export const NH_ASSISTANT_MAX_CUSTOM_INSTRUCTIONS = 1_000;

let nextId = 0;

/**
 * The user's own assistant preferences: style, form of address, answer length and
 * optional instructions. Preferences steer style only; the server keeps them below the
 * application context and agent instructions.
 */
@Component({
  selector: 'nh-assistant-preferences',
  standalone: true,
  imports: [TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-preferences.component.html',
  styleUrl: './nh-assistant-preferences.component.scss'
})
export class NhAssistantPreferencesComponent {
  private readonly api = inject(NhAssistantApiService);
  private subscription?: Subscription;

  /** Emitted when the user leaves the preferences. */
  readonly closed = output<void>();
  /** Emitted with the stored preferences after a successful save. */
  readonly saved = output<AssistantPreferences>();

  readonly id = `nh-assistant-preferences-${nextId++}`;
  readonly styles: readonly AssistantStyle[] = ['default', 'direct', 'personal', 'detailed'];
  readonly addressForms: readonly AssistantAddressForm[] = ['informal', 'formal'];
  readonly responseLengths: readonly AssistantResponseLength[] = ['short', 'normal', 'long'];
  readonly maxInstructions = NH_ASSISTANT_MAX_CUSTOM_INSTRUCTIONS;

  readonly loading = signal(true);
  readonly loadFailed = signal(false);
  readonly saving = signal(false);
  readonly savedMessage = signal(false);
  readonly error = signal<NhAssistantError | null>(null);

  readonly style = signal<AssistantStyle>('default');
  readonly addressForm = signal<AssistantAddressForm>('informal');
  readonly responseLength = signal<AssistantResponseLength>('normal');
  readonly customInstructions = signal('');
  private readonly stored = signal<AssistantPreferences | null>(null);

  readonly instructionsLength = computed(() => this.customInstructions().length);
  readonly tooLong = computed(() => this.instructionsLength() > this.maxInstructions);
  readonly dirty = computed(() => {
    const stored = this.stored();
    return !stored || stored.style !== this.style() || stored.addressForm !== this.addressForm() ||
      stored.responseLength !== this.responseLength() || (stored.customInstructions ?? '') !== this.customInstructions();
  });
  readonly canSave = computed(() => !this.loading() && !this.saving() && !this.tooLong() && this.dirty());
  readonly errorKeys = computed(() => {
    const error = this.error();
    return error ? [error.messageKey, nhAssistantErrorMessageKey(error.code), 'nh-assistant.errors.generic'] : [];
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.loadFailed.set(false);
    this.subscription?.unsubscribe();
    this.subscription = this.api.getPreferences().subscribe({
      next: preferences => {
        this.apply(preferences);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.loadFailed.set(true);
      }
    });
  }

  onInstructionsInput(event: Event): void {
    this.customInstructions.set((event.target as HTMLTextAreaElement).value);
    this.savedMessage.set(false);
  }

  choose<T>(target: { set(value: T): void }, value: T): void {
    target.set(value);
    this.savedMessage.set(false);
  }

  save(): void {
    if (!this.canSave()) {
      return;
    }

    const instructions = this.customInstructions().trim();
    const preferences: AssistantPreferences = {
      style: this.style(),
      addressForm: this.addressForm(),
      responseLength: this.responseLength(),
      customInstructions: instructions.length > 0 ? instructions : null
    };

    this.saving.set(true);
    this.error.set(null);
    this.savedMessage.set(false);
    this.subscription?.unsubscribe();
    this.subscription = this.api.updatePreferences(preferences).subscribe({
      next: stored => {
        this.apply(stored);
        this.saving.set(false);
        this.savedMessage.set(true);
        this.saved.emit(stored);
      },
      error: (error: unknown) => {
        this.saving.set(false);
        this.error.set(error instanceof NhAssistantApiError
          ? { code: error.code, messageKey: error.messageKey }
          : { code: 'assistant-server', messageKey: nhAssistantErrorMessageKey('assistant-server') });
      }
    });
  }

  private apply(preferences: AssistantPreferences): void {
    this.stored.set(preferences);
    this.style.set(preferences.style);
    this.addressForm.set(preferences.addressForm);
    this.responseLength.set(preferences.responseLength);
    this.customInstructions.set(preferences.customInstructions ?? '');
  }
}
