import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import {
  AgentProposal,
  NhAssistantAgentRunApiService,
  ɵNhAssistantIconComponent as NhAssistantIconComponent
} from '@newheap/platform-ai-chat';
import { NhAssistantAgentProposalsComponent } from './nh-assistant-agent-proposals.component';

/**
 * The approval inbox: pending agent proposals across subjects that the signed-in person may
 * decide, earliest expiry first. Proposals are decided in place, one by one or together; the
 * server checks each decision against the proposal's approval policy again. After a rejection the
 * inbox asks, optionally, what was wrong, also when the rejected proposals have left the list.
 */
@Component({
  selector: 'nh-assistant-agent-inbox',
  standalone: true,
  imports: [TranslatePipe, NhAssistantIconComponent, NhAssistantAgentProposalsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-agent-inbox.component.html',
  styleUrl: './nh-assistant-agent-runs.scss'
})
export class NhAssistantAgentInboxComponent {
  private readonly api = inject(NhAssistantAgentRunApiService);
  private subscription?: Subscription;

  /** Limits the inbox to one agent. */
  readonly agentId = input<string | undefined>(undefined);
  /** Limits the inbox to one kind of subject, for example `project`. */
  readonly subjectType = input<string | undefined>(undefined);
  readonly itemsPerPage = input(20);

  readonly page = signal(1);
  readonly loading = signal(true);
  /** True after the first successful load; later loads keep the list, and an open feedback question, in place. */
  readonly loaded = signal(false);
  readonly loadFailed = signal(false);
  readonly items = signal<AgentProposal[]>([]);
  readonly total = signal(0);
  readonly pages = computed(() => Math.max(1, Math.ceil(this.total() / this.itemsPerPage())));

  constructor() {
    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());
    effect(() => {
      this.agentId();
      this.subjectType();
      untracked(() => {
        this.page.set(1);
        this.load();
      });
    });
  }

  load(): void {
    this.loading.set(true);
    this.loadFailed.set(false);
    this.subscription?.unsubscribe();
    this.subscription = this.api
      .getInbox({
        agentId: this.agentId(),
        subjectType: this.subjectType(),
        page: this.page(),
        itemsPerPage: this.itemsPerPage()
      })
      .subscribe({
        next: inbox => {
          this.items.set(inbox.items);
          this.total.set(inbox.total);
          this.loading.set(false);
          this.loaded.set(true);
        },
        error: () => {
          this.loading.set(false);
          this.loadFailed.set(true);
        }
      });
  }

  goTo(page: number): void {
    this.page.set(Math.min(Math.max(1, page), this.pages()));
    this.load();
  }
}
