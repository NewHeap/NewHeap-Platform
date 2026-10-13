import { DatePipe, DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, effect, inject, input, signal, untracked } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import {
  AgentArtifact,
  NhAssistantAgentRunApiService,
  ɵNhAssistantIconComponent as NhAssistantIconComponent
} from '@newheap/platform-ai-chat';

/**
 * The files the agent's tools saved in a thread, every version newest first. Files of the
 * agent's own grant that the viewer may not read are listed as shielded and cannot be downloaded.
 */
@Component({
  selector: 'nh-assistant-agent-artifacts',
  standalone: true,
  imports: [DatePipe, TranslatePipe, NhAssistantIconComponent],
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
  readonly downloadFailed = signal(false);
  readonly artifacts = signal<AgentArtifact[]>([]);
  readonly downloadingId = signal<string | null>(null);

  constructor() {
    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());
    effect(() => {
      this.conversationId();
      untracked(() => this.load());
    });
  }

  load(): void {
    this.loading.set(true);
    this.loadFailed.set(false);
    this.subscription?.unsubscribe();
    this.subscription = this.api.getArtifacts(this.conversationId()).subscribe({
      next: artifacts => {
        this.artifacts.set(artifacts);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.loadFailed.set(true);
      }
    });
  }

  async download(artifact: AgentArtifact): Promise<void> {
    this.downloadingId.set(artifact.id);
    this.downloadFailed.set(false);
    try {
      const blob = await this.api.downloadArtifact(this.conversationId(), artifact.id);
      const url = URL.createObjectURL(blob);
      const link = this.document.createElement('a');
      link.href = url;
      link.download = `${artifact.name}-v${artifact.version}`;
      link.click();
      URL.revokeObjectURL(url);
    } catch {
      this.downloadFailed.set(true);
    } finally {
      this.downloadingId.set(null);
    }
  }
}
