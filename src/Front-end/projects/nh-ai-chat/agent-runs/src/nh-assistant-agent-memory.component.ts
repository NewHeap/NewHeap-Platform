import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import { AgentThreadMemory, NhAssistantAgentRunApiService } from '@newheap/platform-ai-chat';

/**
 * The working memory of an agent about the subject of a thread, read-only. The agent keeps it
 * up to date itself; it is shown so that people can see what the agent remembers.
 */
@Component({
  selector: 'nh-assistant-agent-memory',
  standalone: true,
  imports: [DatePipe, TranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-agent-memory.component.html',
  styleUrl: './nh-assistant-agent-runs.scss'
})
export class NhAssistantAgentMemoryComponent {
  private readonly api = inject(NhAssistantAgentRunApiService);
  private subscription?: Subscription;

  readonly conversationId = input.required<string>();

  readonly loading = signal(true);
  readonly loadFailed = signal(false);
  readonly memory = signal<AgentThreadMemory | null>(null);
  readonly formatted = computed(() => {
    const value = this.memory()?.memory;
    return value === null || value === undefined ? null : JSON.stringify(value, null, 2);
  });

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
    this.subscription = this.api.getMemory(this.conversationId()).subscribe({
      next: memory => {
        this.memory.set(memory);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.loadFailed.set(true);
      }
    });
  }
}
