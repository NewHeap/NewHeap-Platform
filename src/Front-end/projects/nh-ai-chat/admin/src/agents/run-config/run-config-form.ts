import { DestroyRef, ElementRef, Injector, afterNextRender, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { TranslateService } from '@ngx-translate/core';
import { Observable, Subscription, map, startWith } from 'rxjs';
import {
  NhAssistantError,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';
import { RunConfigErrors, RunConfigFieldError, fieldErrorKey, focusFirstInvalid, serverFieldErrors } from './run-config-shared';

/**
 * What the run configuration forms share: errors checked before sending and those the server
 * returns, each shown at its field, a form-level error next to the actions, and the focus on the
 * first invalid field after a refused save.
 */
export abstract class RunConfigForm {
  protected readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  protected readonly injector = inject(Injector);
  protected readonly translate = inject(TranslateService);
  private subscription?: Subscription;

  readonly busy = signal(false);
  readonly clientErrors = signal<RunConfigErrors>({});
  readonly serverError = signal<NhAssistantError | null>(null);
  /** Fields whose server error the administrator already corrected. */
  private readonly cleared = signal<ReadonlySet<string>>(new Set());
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.serverError()));

  /** The language, so computed texts follow a switch. */
  protected readonly language = toSignal(
    this.translate.onLangChange.pipe(map(event => event.lang), startWith(this.translate.currentLang)),
    { initialValue: this.translate.currentLang });

  /** The fields this form shows; server errors of other fields are listed with the form's error. */
  protected abstract readonly fields: readonly string[];

  readonly fieldErrors = computed<RunConfigErrors>(() => {
    const cleared = this.cleared();
    const server = Object.fromEntries(Object.entries(this.mapServerErrors(serverFieldErrors(this.serverError())))
      .filter(([field]) => !cleared.has(field)));
    return { ...server, ...this.clientErrors() };
  });

  /** Server errors of fields this form does not show, by name. */
  readonly otherFieldErrors = computed(() => Object.keys(serverFieldErrors(this.serverError()))
    .filter(field => !this.fields.includes(field) && field !== 'dailyBudget'));

  readonly hasErrors = computed(() => this.serverError() !== null || Object.keys(this.clientErrors()).length > 0);

  constructor() {
    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());
  }

  fieldError(field: string): RunConfigFieldError | null {
    return this.fieldErrors()[field] ?? null;
  }

  errorKey(field: string): string | null {
    return fieldErrorKey(this.fieldError(field));
  }

  /** The ids a control describes itself with: its hint and, when shown, its error. */
  describedBy(hintId: string | null, field: string, errorId: string): string | null {
    const ids = [hintId, this.fieldError(field) ? errorId : null].filter((id): id is string => id !== null);
    return ids.length > 0 ? ids.join(' ') : null;
  }

  /** Removes a field's error once the administrator changes it. */
  touch(field: string): void {
    if (this.clientErrors()[field]) {
      const { [field]: _, ...rest } = this.clientErrors();
      this.clientErrors.set(rest);
    }
    if (!this.cleared().has(field)) {
      this.cleared.set(new Set([...this.cleared(), field]));
    }
  }

  /** The server's codes for fields of other forms, such as a budget the server reports as one. */
  protected mapServerErrors(errors: RunConfigErrors): RunConfigErrors {
    return errors;
  }

  /**
   * Sends the request when `errors` is empty; otherwise shows them and focuses the first. A refused
   * request shows its errors the same way and calls `refused`; `done` receives the last value once
   * the request completed.
   */
  protected submit<T>(
    errors: RunConfigErrors,
    request: () => Observable<T>,
    done: (result: T) => void,
    refused?: () => void
  ): void {
    this.serverError.set(null);
    this.cleared.set(new Set());
    this.clientErrors.set(errors);
    if (Object.keys(errors).length > 0) {
      this.focusFirstInvalid();
      return;
    }
    this.busy.set(true);
    this.subscription?.unsubscribe();
    let last: T | undefined;
    this.subscription = request().subscribe({
      next: result => {
        last = result;
      },
      complete: () => {
        this.busy.set(false);
        done(last as T);
      },
      error: (error: unknown) => {
        this.busy.set(false);
        this.serverError.set(toNhAssistantError(error));
        refused?.();
        this.focusFirstInvalid();
      }
    });
  }

  protected focusFirstInvalid(): void {
    afterNextRender(() => {
      const form = this.host.nativeElement.querySelector<HTMLElement>('form');
      const invalid = form?.querySelector('[aria-invalid="true"]');
      if (invalid) {
        focusFirstInvalid(form);
      } else {
        form?.querySelector<HTMLElement>('.form-error')?.scrollIntoView?.({ block: 'nearest' });
      }
    }, { injector: this.injector });
  }

  /** The translated text of a key, for computed descriptions. */
  protected t(key: string, params?: Record<string, unknown>): string {
    return this.translate.instant(key, params) as string;
  }

  value(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  checked(event: Event): boolean {
    return (event.target as HTMLInputElement).checked;
  }
}
