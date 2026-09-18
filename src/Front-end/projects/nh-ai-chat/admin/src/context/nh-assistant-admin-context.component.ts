import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription, forkJoin } from 'rxjs';
import {
  ApplicationContext,
  ApplicationContextVersion,
  NhAssistantAdminApiService,
  NhAssistantClientErrorCodes,
  NhAssistantError,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';

/** Maximum length of the application context and of agent instructions. */
export const NH_ASSISTANT_MAX_INSTRUCTIONS = 20_000;

let nextId = 0;

/**
 * Editor of the application context: the current version with its hash, optimistic
 * concurrency through `expectedVersion`, and the version history.
 */
@Component({
  selector: 'nh-assistant-admin-context',
  standalone: true,
  imports: [DatePipe, TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-admin-context.component.html',
  styleUrl: '../nh-assistant-admin-shared.scss'
})
export class NhAssistantAdminContextComponent {
  private readonly admin = inject(NhAssistantAdminApiService);
  private subscription?: Subscription;

  readonly id = `nh-assistant-admin-context-${nextId++}`;
  readonly max = NH_ASSISTANT_MAX_INSTRUCTIONS;
  readonly loading = signal(true);
  readonly loadFailed = signal(false);
  readonly saving = signal(false);
  readonly saved = signal(false);
  readonly conflict = signal(false);
  readonly error = signal<NhAssistantError | null>(null);
  readonly context = signal<ApplicationContext | null>(null);
  readonly versions = signal<ApplicationContextVersion[]>([]);
  readonly text = signal('');

  readonly tooLong = computed(() => this.text().length > this.max);
  readonly dirty = computed(() => this.text() !== (this.context()?.text ?? ''));
  readonly canSave = computed(() => !this.saving() && !this.tooLong() && this.dirty() && this.context() !== null);
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));
  readonly history = computed(() => this.versions().filter(version => version.version !== this.context()?.version));

  constructor() {
    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());
    this.load(false);
  }

  /** Loads the latest version; with `keepDraft` the user's unsaved text stays in the editor. */
  load(keepDraft: boolean): void {
    this.loading.set(!keepDraft);
    this.loadFailed.set(false);
    this.subscription?.unsubscribe();
    this.subscription = forkJoin([this.admin.getContext(), this.admin.getContextVersions()]).subscribe({
      next: ([context, versions]) => {
        const draft = this.text();
        this.context.set(context);
        this.versions.set(versions);
        this.text.set(keepDraft ? draft : context.text);
        this.conflict.set(false);
        this.error.set(null);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.loadFailed.set(true);
      }
    });
  }

  onInput(event: Event): void {
    this.text.set((event.target as HTMLTextAreaElement).value);
    this.saved.set(false);
  }

  save(): void {
    const context = this.context();
    if (!context || !this.canSave()) {
      return;
    }

    this.saving.set(true);
    this.saved.set(false);
    this.error.set(null);
    this.subscription?.unsubscribe();
    this.subscription = this.admin.updateContext({ text: this.text(), expectedVersion: context.version }).subscribe({
      next: updated => {
        this.context.set(updated);
        this.versions.update(versions => [
          { version: updated.version, hash: updated.hash, updatedAt: updated.updatedAt, updatedBy: updated.updatedBy },
          ...versions
        ]);
        this.text.set(updated.text);
        this.saving.set(false);
        this.saved.set(true);
      },
      error: (error: unknown) => {
        const failure = toNhAssistantError(error);
        this.saving.set(false);
        this.conflict.set(failure.code === NhAssistantClientErrorCodes.versionConflict);
        this.error.set(this.conflict() ? null : failure);
      }
    });
  }
}
