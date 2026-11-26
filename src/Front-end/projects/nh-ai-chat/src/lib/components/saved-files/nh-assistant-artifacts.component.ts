import { DatePipe, DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import { AgentArtifact } from '../../models/assistant-agent-run.models';
import type { NhAssistantError } from '../../services/nh-assistant.store';
import { NhAssistantApiService } from '../../services/nh-assistant-api.service';
import { nhAssistantErrorKeys, toNhAssistantError } from '../../internal/nh-assistant-errors';
import { NhAssistantIconComponent } from '../../internal/nh-assistant-icon.component';
import { nhAssistantReloadOnAgentWork } from '../../internal/nh-assistant-agent-work';
import { NhAssistantTokensDirective } from '../../internal/nh-assistant-tokens';
import { NhAssistantTranslatePipe } from '../../internal/nh-assistant-translate.pipe';

/**
 * The files tools saved in a chat or agent thread, every version newest first. Files of the
 * agent's own grant that the viewer may not read are listed as shielded and cannot be downloaded.
 * With live updates the list reloads by itself when a run or proposal of the thread changes.
 */
@Component({
  selector: 'nh-assistant-artifacts, nh-assistant-agent-artifacts',
  standalone: true,
  hostDirectives: [NhAssistantTokensDirective],
  imports: [DatePipe, TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-artifacts.component.html',
  styleUrl: './nh-assistant-artifacts.component.scss'
})
export class NhAssistantArtifactsComponent {
  private readonly api = inject(NhAssistantApiService);
  private readonly document = inject(DOCUMENT);
  private subscription?: Subscription;

  readonly conversationId = input.required<string>();
  /** Inline chats hide the empty state; standalone file lists show it. */
  readonly compact = input(false);
  /** Reloads when the turn finishes; avoids requests during streaming. */
  readonly busy = input(false);
  private loadedConversationId: string | null = null;
  private readonly destroyRef = inject(DestroyRef);

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
      const id = this.conversationId();
      const busy = this.busy();
      untracked(() => {
        if (id !== this.loadedConversationId) {
          this.subscription?.unsubscribe();
          this.artifacts.set([]);
          this.downloadError.set(null);
          this.loadFailed.set(false);
          this.loadedConversationId = id;
        }
        if (!busy) {
          this.load();
        }
      });
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
    if (artifact.restricted || this.downloadingId() !== null) {
      return;
    }
    const conversationId = this.conversationId();
    this.downloadingId.set(artifact.id);
    this.downloadError.set(null);
    try {
      const blob = await this.api.downloadArtifact(conversationId, artifact.id);
      if (this.destroyRef.destroyed || conversationId !== this.conversationId()) {
        return;
      }
      const url = URL.createObjectURL(blob);
      const link = this.document.createElement('a');
      link.href = url;
      link.download = artifact.name;
      try {
        link.click();
      } finally {
        URL.revokeObjectURL(url);
      }
    } catch (error) {
      if (!this.destroyRef.destroyed && conversationId === this.conversationId()) {
        this.downloadError.set(toNhAssistantError(error));
      }
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
