import { DatePipe, DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import {
  AgentArtifact,
  NhAssistantAgentRunApiService,
  NhAssistantError,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵnhAssistantReloadOnAgentWork as nhAssistantReloadOnAgentWork,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';

/**
 * The files the agent's tools saved in a thread, every version newest first. Files of the
 * agent's own grant that the viewer may not read are listed as shielded and cannot be downloaded.
 * With live updates the list reloads by itself when a run or proposal of the thread changes.
 */
@Component({
  selector: 'nh-assistant-agent-artifacts',
  standalone: true,
  imports: [DatePipe, TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-agent-artifacts.component.html',
  styleUrl: './nh-assistant-agent-runs.scss'
})
export class NhAssistantAgentArtifactsComponent {
  private readonly api = inject(NhAssistantAgentRunApiService);
  private readonly document = inject(DOCUMENT);
  private subscription?: Subscription;

  readonly conversationId = input.required<string>();

  readonly loading = signal(true);
  readonly loadFailed = signal(false);
  /** Why the latest download failed, as a code and translation key; `null` after one that worked. */
  readonly downloadError = signal<NhAssistantError | null>(null);
  /** Whether the latest download failed. */
  readonly downloadFailed = computed(() => this.downloadError() !== null);
  /** The text of the download's failure, then the text that a file could not be downloaded. */
  readonly downloadErrorKeys = computed(() => {
    const keys = nhAssistantErrorKeys(this.downloadError());
    return keys.length > 0 ? [...keys.slice(0, -1), 'nh-assistant.agent-runs.artifacts.download-failed'] : [];
  });
  readonly artifacts = signal<AgentArtifact[]>([]);
  readonly downloadingId = signal<string | null>(null);

  constructor() {
    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());
    effect(() => {
      this.conversationId();
      untracked(() => this.load());
    });
    nhAssistantReloadOnAgentWork(
      change => change.conversationId === this.conversationId() && change.reason !== 'feedback',
      () => this.fetch(!this.loading() && !this.loadFailed())
    );
  }

  load(): void {
    this.fetch(false);
  }

  async download(artifact: AgentArtifact): Promise<void> {
    this.downloadingId.set(artifact.id);
    this.downloadError.set(null);
    try {
      const blob = await this.api.downloadArtifact(this.conversationId(), artifact.id);
      const url = URL.createObjectURL(blob);
      const link = this.document.createElement('a');
      link.href = url;
      link.download = `${artifact.name}-v${artifact.version}`;
      link.click();
      URL.revokeObjectURL(url);
    } catch (error) {
      this.downloadError.set(toNhAssistantError(error));
    } finally {
      this.downloadingId.set(null);
    }
  }

  /** A quiet load, after a live update, shows no loading state and keeps the files when it fails. */
  private fetch(quiet: boolean): void {
    if (!quiet) {
      this.loading.set(true);
      this.loadFailed.set(false);
    }

    this.subscription?.unsubscribe();
    this.subscription = this.api.getArtifacts(this.conversationId()).subscribe({
      next: artifacts => {
        this.artifacts.set(artifacts);
        this.loading.set(false);
        this.loadFailed.set(false);
      },
      error: () => {
        if (quiet) {
          return;
        }

        this.loading.set(false);
        this.loadFailed.set(true);
      }
    });
  }
}
