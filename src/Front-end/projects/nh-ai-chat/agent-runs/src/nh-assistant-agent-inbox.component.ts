import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
  viewChild
} from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import {
  AgentProposal,
  NhAssistantAgentRunApiService,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵnhAssistantReloadOnAgentWork as nhAssistantReloadOnAgentWork,
  ɵnhAssistantWatchAgentWork as nhAssistantWatchAgentWork
} from '@newheap/platform-ai-chat';
import { NhAssistantAgentProposalsComponent } from './nh-assistant-agent-proposals.component';

/**
 * The approval inbox: pending agent proposals across subjects that the signed-in person may
 * decide, earliest expiry first. Proposals are decided in place, one by one or together; the
 * server checks each decision against the proposal's approval policy again. After a rejection the
 * inbox asks, optionally, what was wrong, also when the rejected proposals have left the list.
 * With live updates the inbox reloads by itself when agent work changes, for example when a
 * colleague decided a proposal or a run proposed a new one; it watches the work of agents for the
 * whole application, whose approvers are no members of it, while it is shown. A proposal that closes because it no
 * longer fits, for example because someone did the work by hand, stays as a closed card with the
 * reason until it is dismissed. A proposal a run placed at its object from another subject, such as a
 * run for the whole application, names that run; `openConversation` emits its conversation.
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
  private readonly proposalList = viewChild(NhAssistantAgentProposalsComponent);
  private subscription?: Subscription;

  /** Limits the inbox to one agent. */
  readonly agentId = input<string | undefined>(undefined);
  /** Limits the inbox to one kind of subject, for example `project`. */
  readonly subjectType = input<string | undefined>(undefined);
  readonly itemsPerPage = input(20);
  /**
   * Emits the conversation of the run that placed a proposal at its object from another subject, so
   * the host opens it, for example in the assistant panel.
   */
  readonly openConversation = output<string>();

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
    // Any agent's work may add, decide or close a proposal of this inbox, also work for the whole
    // application, which reaches the approvers only while they watch it.
    nhAssistantWatchAgentWork();
    nhAssistantReloadOnAgentWork(() => true, () => this.refresh());
  }

  load(): void {
    this.fetch(false);
  }

  goTo(page: number): void {
    this.page.set(Math.min(Math.max(1, page), this.pages()));
    this.load();
  }

  /**
   * Reloads after a live update. Once the inbox is shown it reloads quietly: no loading state, and
   * a failure keeps the list and an open feedback question in place. While a decision of the list
   * is in flight it waits, because the decision reloads the inbox when it ends.
   */
  private refresh(): void {
    if (this.proposalList()?.busy()) {
      return;
    }

    this.fetch(this.loaded() && !this.loading() && !this.loadFailed());
  }

  private fetch(quiet: boolean): void {
    if (!quiet) {
      this.loading.set(true);
      this.loadFailed.set(false);
    }

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
          const lastPage = Math.max(1, Math.ceil(inbox.total / this.itemsPerPage()));
          if (inbox.items.length === 0 && this.page() > lastPage) {
            // Proposals were decided elsewhere and this page no longer exists.
            this.page.set(lastPage);
            this.fetch(quiet);
            return;
          }

          const previous = this.items();
          this.items.set(inbox.items);
          if (quiet) {
            this.proposalList()?.holdClosed(previous, inbox.items);
          }
          this.total.set(inbox.total);
          this.loading.set(false);
          this.loadFailed.set(false);
          this.loaded.set(true);
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
