import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  input,
  output,
  signal
} from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import {
  AdminAgent,
  NhAssistantAdminApiService,
  NhAssistantError,
  nhAssistantErrorMessageKey,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';
import { NhAssistantAccountNames } from '../../nh-assistant-account-names';
import { NhAssistantAccountPickerComponent } from '../run-config/nh-assistant-account-picker.component';
import { maxStateReason, withState } from '../agent-operations';
import { startNhAssistantAdminTranslations } from '../../i18n/nh-assistant-admin-translations';

let nextId = 0;

/** Refusals of a technical account that concern the account field itself. */
const accountRefusals = new Set([
  'assistant-service-account-not-found',
  'assistant-service-account-not-permitted',
  'assistant-service-account-not-used'
]);

/**
 * The technical account an agent in code answers to: the account in effect, the one its code
 * configures when an administrator replaced it, and the reason. An administrator replaces it with
 * another account, found by name when the application offers an account search, or returns to the
 * account in code, each with an optional reason. A refused account shows at its field.
 */
@Component({
  selector: 'nh-assistant-agent-account-control',
  standalone: true,
  imports: [TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent, NhAssistantAccountPickerComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-agent-account-control.component.html',
  styleUrls: ['../../nh-assistant-admin-shared.scss', './agent-page.scss']
})
export class NhAssistantAgentAccountControlComponent {
  private readonly admin = inject(NhAssistantAdminApiService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);
  private subscription?: Subscription;

  readonly agent = input.required<AdminAgent>();
  /** Whether the application offers an account search for the technical account. */
  readonly accountSearch = input(false);
  /** The names of the accounts, once known. */
  readonly names = input.required<NhAssistantAccountNames>();
  readonly disabled = input(false);
  /** The agent with its new technical account. */
  readonly changed = output<AdminAgent>();

  readonly id = `nh-assistant-account-control-${nextId++}`;
  readonly maxReason = maxStateReason;
  readonly mode = signal<'set' | 'code' | null>(null);
  readonly accountId = signal('');
  readonly reason = signal('');
  readonly busy = signal(false);
  readonly error = signal<NhAssistantError | null>(null);
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));
  readonly accountError = signal<NhAssistantError | null>(null);
  /** The text of the account field's error: the refused field value, then the code. */
  readonly accountErrorKeys = computed(() => {
    const error = this.accountError();
    const value = error?.fieldErrors?.['actorId']?.[0];
    return value ? [`nh-assistant.admin.run-config.field-errors.${value}`, ...nhAssistantErrorKeys(error)] : nhAssistantErrorKeys(error);
  });

  constructor() {
    startNhAssistantAdminTranslations();
    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());
  }

  accountName(actorId: string | null | undefined): string {
    return this.names().nameOf(actorId);
  }

  accountTitle(actorId: string | null | undefined): string | null {
    return this.names().idTitleOf(actorId);
  }

  ask(mode: 'set' | 'code'): void {
    this.mode.set(mode);
    this.accountId.set('');
    this.reason.set('');
    this.error.set(null);
    this.accountError.set(null);
    afterNextRender(() => this.host.nativeElement.querySelector<HTMLElement>('[data-account-form] input')?.focus(), { injector: this.injector });
  }

  cancel(): void {
    this.mode.set(null);
  }

  /** A changed account clears the error of the one before. */
  onAccountChange(actorId: string): void {
    this.accountId.set(actorId);
    this.accountError.set(null);
  }

  /**
   * Changes the technical account. The form stays open while the server decides, so a missing or
   * refused account shows at the account field, which takes the focus.
   */
  apply(): void {
    const mode = this.mode();
    if (mode === null || this.busy()) {
      return;
    }

    const actorId = mode === 'set' ? this.accountId().trim() : null;
    if (actorId === '') {
      this.accountError.set({
        code: 'assistant-validation',
        messageKey: nhAssistantErrorMessageKey('assistant-validation'),
        fieldErrors: { actorId: ['required'] }
      });
      this.focusAccount();
      return;
    }

    const agent = this.agent();
    const reason = this.reason().trim();
    this.busy.set(true);
    this.error.set(null);
    this.accountError.set(null);
    this.subscription?.unsubscribe();
    this.subscription = this.admin.setServiceAccount(agent.id, { actorId, reason: reason.length > 0 ? reason : null }).subscribe({
      next: state => {
        this.busy.set(false);
        this.mode.set(null);
        this.changed.emit(withState(agent, state));
      },
      error: (error: unknown) => {
        this.busy.set(false);
        const failure = toNhAssistantError(error);
        if (failure.fieldErrors?.['actorId'] || accountRefusals.has(failure.code)) {
          this.accountError.set(failure);
          this.focusAccount();
        } else {
          this.error.set(failure);
        }
      }
    });
  }

  value(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  private focusAccount(): void {
    afterNextRender(() => this.host.nativeElement.querySelector<HTMLElement>('[data-account-form] input')?.focus(), { injector: this.injector });
  }
}
