import { ChangeDetectionStrategy, Component, DestroyRef, effect, inject, input, output, signal, viewChild } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Observable, Subscription, map } from 'rxjs';
import {
  NhAssistantAdminApiService,
  ɵNhAssistantPickerComponent as NhAssistantPickerComponent,
  ɵNhAssistantPickerOption as NhAssistantPickerOption
} from '@newheap/platform-ai-chat';

const lookupDelayMs = 300;

/**
 * Chooses the account an agent answers to. When the application offers an account search the
 * administrator finds the account by name or e-mail address; otherwise they enter its id and the
 * screen shows the name the application knows for it.
 */
@Component({
  selector: 'nh-assistant-account-picker',
  standalone: true,
  imports: [TranslatePipe, NhAssistantPickerComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (searchable()) {
      <nh-assistant-picker
        [inputId]="inputId()"
        [value]="value()"
        [selectedLabel]="label()"
        [search]="search"
        [invalid]="invalid()"
        [describedBy]="describedBy()"
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
        [attr.aria-describedby]="describedBy()"
        [attr.placeholder]="'nh-assistant.admin.run-config.account.id-placeholder' | translate"
        (input)="valueChange.emit(text($event))" />
    }
    @if (!searchable() && value()) {
      <p class="account-name" aria-live="polite">
        {{ (label() ? 'nh-assistant.admin.run-config.account.known' : 'nh-assistant.admin.run-config.account.unknown') | translate: { name: label() } }}
      </p>
    }
  `,
  styles: [`
    :host { display: block; min-width: 0; }
    input {
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

  /** The name of the chosen account, once known. */
  readonly label = signal<string | null>(null);

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
      clearTimeout(this.lookupTimer);
      this.lookupSubscription?.unsubscribe();
      if (value.length === 0) {
        this.label.set(null);
        return;
      }
      this.lookupTimer = setTimeout(() => {
        this.lookupSubscription = this.admin.getAccounts([value]).subscribe({
          next: accounts => this.label.set(accounts.find(account => account.actorId === value)?.displayName ?? null),
          error: () => this.label.set(null)
        });
      }, this.searchable() ? 0 : lookupDelayMs);
    });
  }

  focus(): void {
    this.picker()?.focus();
    this.manual()?.nativeElement.focus();
  }

  text(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }
}
