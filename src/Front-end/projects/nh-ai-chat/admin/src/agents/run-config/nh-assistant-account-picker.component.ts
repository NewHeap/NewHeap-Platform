import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, output, signal, untracked, viewChild } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Observable, Subscription, map } from 'rxjs';
import {
  NhAssistantAdminApiService,
  NhAssistantError,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantPickerComponent as NhAssistantPickerComponent,
  ɵNhAssistantPickerOption as NhAssistantPickerOption,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';

const lookupDelayMs = 300;

let nextId = 0;

/**
 * Chooses the account an agent answers to. When the application offers an account search the
 * administrator finds the account by name or e-mail address; otherwise they enter its id and the
 * screen shows the name the application knows for it. When the name cannot be looked up, the screen
 * says why with the translated code and offers to look it up again, instead of calling the account
 * unknown.
 */
@Component({
  selector: 'nh-assistant-account-picker',
  standalone: true,
  imports: [TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent, NhAssistantPickerComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (searchable()) {
      <nh-assistant-picker
        [inputId]="inputId()"
        [value]="value()"
        [selectedLabel]="label()"
        [search]="search"
        [invalid]="invalid()"
        [describedBy]="fieldDescribedBy()"
        [placeholder]="'nh-assistant.admin.run-config.account.search-placeholder' | translate"
        (valueChange)="valueChange.emit($event)"
        (optionChange)="label.set($event?.label ?? null)" />
    } @else {
      <input
        #manual
        type="text"
        class="mono"
        autocomplete="off"
        maxlength="256"
        [id]="inputId()"
        [value]="value() ?? ''"
        [attr.aria-invalid]="invalid() ? true : null"
        [attr.aria-describedby]="fieldDescribedBy()"
        [attr.placeholder]="'nh-assistant.admin.run-config.account.id-placeholder' | translate"
        (input)="valueChange.emit(text($event))" />
    }
    @if (lookupError(); as failure) {
      <p class="lookup-error" role="alert" data-account-lookup-error [id]="lookupErrorId">
        <nh-assistant-icon name="warning" />
        <span>{{ lookupErrorKeys() | nhAssistantTranslate: failure.code }}</span>
        <button type="button" class="link" data-account-lookup-retry (click)="lookUpAgain()">
          {{ 'nh-assistant.admin.run-config.account.lookup-again' | translate }}
        </button>
      </p>
    } @else if (!searchable() && value()) {
      <p class="account-name" aria-live="polite">
        {{ (label() ? 'nh-assistant.admin.run-config.account.known' : 'nh-assistant.admin.run-config.account.unknown') | translate: { name: label() } }}
      </p>
    }
  `,
  styles: [`
    :host { display: block; min-width: 0; }
    input {
      box-sizing: border-box;
      width: 100%;
      min-width: 0;
      min-height: 38px;
      border: 1px solid var(--nha-border, #d0d5dd);
      border-radius: var(--nha-radius-control, 8px);
      padding: 8px 10px;
      color: var(--nha-text, inherit);
      background: var(--nha-background, transparent);
      font-family: var(--nha-mono, monospace);
      font-size: 12px;
    }
    input[aria-invalid='true'] { border-color: var(--nha-danger, #b42318); }
    .account-name { margin: 4px 2px 0; color: var(--nha-text-muted, #667085); font-size: 12px; overflow-wrap: anywhere; }
    .lookup-error {
      display: flex;
      flex-wrap: wrap;
      align-items: baseline;
      gap: 6px;
      margin: 4px 2px 0;
      color: var(--nha-danger, #b42318);
      font-size: 12px;
      font-weight: 600;
      overflow-wrap: anywhere;
    }
    .lookup-error span { flex: 1 1 auto; min-width: 0; }
    button.link {
      border: 0;
      padding: 0;
      color: var(--nha-accent, currentColor);
      background: none;
      font: inherit;
      text-decoration: underline;
      cursor: pointer;
    }
  `]
})
export class NhAssistantAccountPickerComponent {
  private readonly admin = inject(NhAssistantAdminApiService);
  private lookupTimer?: ReturnType<typeof setTimeout>;
  private lookupSubscription?: Subscription;
  private readonly picker = viewChild(NhAssistantPickerComponent);
  private readonly manual = viewChild<{ nativeElement: HTMLInputElement }>('manual');

  readonly inputId = input.required<string>();
  readonly value = input<string | null>(null);
  /** Whether the application offers an account search (`RunCatalog.accountSearch`). */
  readonly searchable = input(false);
  readonly invalid = input(false);
  readonly describedBy = input<string | null>(null);
  readonly valueChange = output<string>();

  readonly lookupErrorId = `nh-assistant-account-picker-lookup-${nextId++}`;
  /** The name of the chosen account, once known. */
  readonly label = signal<string | null>(null);
  /** Why the name of the account could not be looked up; `null` after a lookup that answered. */
  readonly lookupError = signal<NhAssistantError | null>(null);
  readonly lookupErrorKeys = computed(() => nhAssistantErrorKeys(this.lookupError()));
  readonly fieldDescribedBy = computed(() =>
    [this.describedBy(), this.lookupError() ? this.lookupErrorId : null].filter(Boolean).join(' ') || null);

  readonly search = (query: string): Observable<NhAssistantPickerOption[]> =>
    this.admin.searchAccounts(query).pipe(map(accounts => accounts.map(account => ({
      value: account.actorId,
      label: account.displayName,
      // An account named by its e-mail address needs no second line.
      description: account.detail === account.displayName ? null : account.detail
    }))));

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      clearTimeout(this.lookupTimer);
      this.lookupSubscription?.unsubscribe();
    });
    // The name of a stored account comes from the application's directory.
    effect(() => {
      const value = this.value()?.trim() ?? '';
      const delay = this.searchable() ? 0 : lookupDelayMs;
      untracked(() => this.lookUp(value, delay));
    });
  }

  focus(): void {
    this.picker()?.focus();
    this.manual()?.nativeElement.focus();
  }

  text(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  /** Looks the name of the account up again after a failed lookup. */
  lookUpAgain(): void {
    this.lookUp(this.value()?.trim() ?? '', 0);
  }

  private lookUp(value: string, delay: number): void {
    clearTimeout(this.lookupTimer);
    this.lookupSubscription?.unsubscribe();
    this.lookupError.set(null);
    if (value.length === 0) {
      this.label.set(null);
      return;
    }

    this.lookupTimer = setTimeout(() => {
      this.lookupSubscription = this.admin.getAccounts([value]).subscribe({
        next: accounts => this.label.set(accounts.find(account => account.actorId === value)?.displayName ?? null),
        error: (error: unknown) => {
          this.label.set(null);
          this.lookupError.set(toNhAssistantError(error));
        }
      });
    }, delay);
  }
}
