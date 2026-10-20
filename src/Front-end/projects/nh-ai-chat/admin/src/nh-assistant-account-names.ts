import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, input, signal } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
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
 * (internal). A screen shows the id while no name is known. When the lookup fails, `error` says why
 * with the failure's code and `retry` asks for the names that are still missing, so a failure never
 * hides behind the ids. Create it in an injection context, such as a component's field.
 */
export class NhAssistantAccountNames {
  private readonly admin = inject(NhAssistantAdminApiService);
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

  /** The name of an account, or its id when the application does not know it. */
  nameOf(actorId: string | null | undefined): string {
    return actorId ? this.names().get(actorId) ?? actorId : '';
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
