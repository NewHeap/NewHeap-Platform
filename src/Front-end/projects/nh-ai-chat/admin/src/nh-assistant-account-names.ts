import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, signal } from '@angular/core';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import {
  NhAssistantAdminApiService,
  NhAssistantError,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';

/**
 * The names the application knows for account ids, for the screens of the administration
 * (internal). A screen shows the name only; it shows the id, labeled as an account ID, only while
 * no name is known. When the lookup fails, `error` says why with the failure's code and `retry` asks
 * for the names that are still missing, so a failure never hides behind the ids. Create it in an
 * injection context, such as a component's field.
 */
export class NhAssistantAccountNames {
  private readonly admin = inject(NhAssistantAdminApiService);
  private readonly translate = inject(TranslateService);
  private readonly subscriptions = new Subscription();
  /** The ids whose names were asked for, also the ones the application does not know. */
  private readonly asked = new Set<string>();
  /** The ids of failed lookups, which `retry` asks for again. */
  private failed = new Set<string>();

  /** The names found so far, by account id. */
  readonly names = signal<ReadonlyMap<string, string>>(new Map());
  /** Why the latest lookup failed; `null` while none did. */
  readonly error = signal<NhAssistantError | null>(null);

  constructor() {
    inject(DestroyRef).onDestroy(() => this.subscriptions.unsubscribe());
  }

  /**
   * The name of an account for a sentence, or its id labeled as an account ID ("account ID …")
   * while the application knows no name for it.
   */
  nameOf(actorId: string | null | undefined): string {
    if (!actorId) {
      return '';
    }

    return this.names().get(actorId) ?? this.translate.instant('nh-assistant.account-id', { id: actorId }) as string;
  }

  /**
   * The account ID as a tooltip for a name shown in its place; `null` while no name is known,
   * because the text shows the id then.
   */
  idTitleOf(actorId: string | null | undefined): string | null {
    if (!actorId || !this.names().has(actorId)) {
      return null;
    }

    return this.translate.instant('nh-assistant.account-id-title', { id: actorId }) as string;
  }

  /** Looks up the names of the ids that were not asked for yet. */
  load(actorIds: readonly (string | null | undefined)[]): void {
    const missing = [...new Set(actorIds.filter((id): id is string => !!id && !this.asked.has(id)))];
    if (missing.length === 0) {
      return;
    }

    for (const id of missing) {
      this.asked.add(id);
    }
    this.subscriptions.add(this.admin.getAccounts(missing).subscribe({
      next: accounts => {
        this.names.update(names => new Map([...names, ...accounts.map(account => [account.actorId, account.displayName] as const)]));
      },
      error: (error: unknown) => {
        for (const id of missing) {
          this.asked.delete(id);
          this.failed.add(id);
        }
        this.error.set(toNhAssistantError(error));
      }
    }));
  }

  /** Asks again for the names whose lookup failed. */
  retry(): void {
    const ids = [...this.failed];
    this.failed = new Set();
    this.error.set(null);
    this.load(ids);
  }
}

/**
 * The notice of a failed lookup of account names (internal): the screen shows ids, says why there
 * are no names and offers to look them up again.
 */
@Component({
  selector: 'nh-assistant-account-names-error',
  standalone: true,
  imports: [TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (names().error(); as failure) {
      <p class="names-error" role="status" data-account-names-error>
        <nh-assistant-icon name="warning" />
        <span>{{ 'nh-assistant.admin.account-names-failed' | translate }} {{ keys() | nhAssistantTranslate: failure.code }}</span>
        <button type="button" class="link" data-account-names-retry (click)="names().retry()">{{ 'nh-assistant.admin.retry' | translate }}</button>
      </p>
    }
  `,
  styles: [`
    :host { display: block; min-width: 0; }
    .names-error {
      display: flex;
      flex-wrap: wrap;
      align-items: baseline;
      gap: 6px;
      margin: 0;
      border: 1px solid var(--nha-warning, #8a5a00);
      border-radius: var(--nha-radius-control, 12px);
      padding: 8px 12px;
      color: var(--nha-text, inherit);
      background: var(--nha-warning-soft, transparent);
      font-size: 13px;
      overflow-wrap: anywhere;
    }
    .names-error span { flex: 1 1 auto; min-width: 0; }
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
export class NhAssistantAccountNamesErrorComponent {
  readonly names = input.required<NhAssistantAccountNames>();
  readonly keys = computed(() => nhAssistantErrorKeys(this.names().error()));
}

/**
 * One account by its name (internal): the name only, with the account ID as its tooltip and a
 * button that copies the ID; without a known name the ID itself, labeled as an account ID. Where
 * the browser refuses the clipboard, the button shows the ID next to the name to copy by hand.
 */
@Component({
  selector: 'nh-assistant-account-label',
  standalone: true,
  imports: [TranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (name(); as known) {
      <span class="account-name" data-account-label-name [attr.title]="'nh-assistant.account-id-title' | translate: { id: actorId() }">{{ known }}</span>
      <button
        type="button"
        class="copy-id"
        data-account-copy-id
        [attr.aria-label]="'nh-assistant.admin.account.copy-id' | translate: { name: known }"
        [attr.title]="'nh-assistant.admin.account.copy-id' | translate: { name: known }"
        (click)="copyId()">
        <nh-assistant-icon [name]="copied() ? 'check' : 'copy'" />
      </button>
      @if (revealed()) {
        <span class="account-id" data-account-label-revealed>{{ 'nh-assistant.admin.account.id' | translate: { id: actorId() } }}</span>
      }
      <span class="visually-hidden" role="status">{{ copied() ? ('nh-assistant.admin.account.id-copied' | translate) : '' }}</span>
    } @else {
      <span class="account-id" data-account-label-id>{{ 'nh-assistant.admin.account.id' | translate: { id: actorId() } }}</span>
    }
  `,
  styles: [`
    :host { display: inline-flex; flex-wrap: wrap; align-items: center; gap: 4px; min-width: 0; max-width: 100%; }
    .account-name { font-weight: 600; overflow-wrap: anywhere; }
    .account-id { color: var(--nha-muted, inherit); overflow-wrap: anywhere; }
    .copy-id {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      box-sizing: border-box;
      min-width: 28px;
      min-height: 28px;
      border: 1px solid transparent;
      border-radius: var(--nha-radius-control, 12px);
      padding: 4px;
      color: var(--nha-muted, currentColor);
      background: none;
      cursor: pointer;
    }
    .copy-id:hover { color: var(--nha-text, currentColor); border-color: var(--nha-border, currentColor); }
    .copy-id:focus-visible { outline: 2px solid var(--nha-accent, currentColor); outline-offset: 2px; }
    .visually-hidden {
      position: absolute;
      width: 1px;
      height: 1px;
      overflow: hidden;
      clip: rect(0 0 0 0);
      clip-path: inset(50%);
      white-space: nowrap;
    }
  `]
})
export class NhAssistantAccountLabelComponent {
  /** The account's id. */
  readonly actorId = input.required<string>();
  /** The account's name; `null` while the application knows none. */
  readonly name = input<string | null>(null);

  readonly copied = signal(false);
  readonly revealed = signal(false);
  private resetTimer: ReturnType<typeof setTimeout> | undefined;

  constructor() {
    inject(DestroyRef).onDestroy(() => clearTimeout(this.resetTimer));
    effect(() => {
      // Another account starts without the state of the previous one.
      this.actorId();
      this.copied.set(false);
      this.revealed.set(false);
    });
  }

  async copyId(): Promise<void> {
    try {
      await navigator.clipboard.writeText(this.actorId());
    } catch {
      // Without clipboard access the ID is shown to copy by hand.
      this.revealed.set(true);
      return;
    }

    this.copied.set(true);
    clearTimeout(this.resetTimer);
    this.resetTimer = setTimeout(() => this.copied.set(false), 3_000);
  }
}
