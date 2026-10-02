import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, output, signal } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { ConversationMember, DirectoryEntry } from '../../models/assistant-api.models';
import { NhAssistantIconComponent } from '../../internal/nh-assistant-icon.component';
import { NH_ASSISTANT_CONFIG } from '../../nh-assistant.config';
import { NhAssistantStore } from '../../services/nh-assistant.store';
import { nhAssistantShareLink } from '../../services/nh-assistant-share-link';

let nextId = 0;
const searchDelayMs = 250;

/**
 * Sharing of the open conversation. The owner creates, copies, renews or stops an invitation
 * link, invites colleagues through the application's directory when it offers one, and removes
 * participants. A participant sees who takes part and can leave.
 */
@Component({
  selector: 'nh-assistant-share',
  standalone: true,
  imports: [TranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-share.component.html',
  styleUrl: './nh-assistant-share.component.scss'
})
export class NhAssistantShareComponent {
  readonly store = inject(NhAssistantStore);
  private readonly config = inject(NH_ASSISTANT_CONFIG);
  private searchTimer?: ReturnType<typeof setTimeout>;
  private searchRevision = 0;

  /** Emitted when the user leaves the view. */
  readonly closed = output<void>();

  readonly id = `nh-assistant-share-${nextId++}`;
  readonly conversation = this.store.activeConversation;
  readonly isOwner = this.store.isOwner;
  readonly busy = signal(false);
  readonly copied = signal(false);
  readonly query = signal('');
  readonly searching = signal(false);
  readonly candidates = signal<DirectoryEntry[]>([]);
  readonly invited = signal<string | null>(null);

  readonly directory = computed(() => this.store.collaboration()?.directory === true && this.isOwner());
  readonly maxParticipants = computed(() => this.store.collaboration()?.maxParticipants ?? null);
  readonly participants = computed(() => this.store.members().filter(member => member.role === 'participant'));
  readonly link = computed(() => {
    const conversation = this.conversation();
    if (!conversation?.shareToken) {
      return null;
    }
    return (this.config.buildShareLink ?? nhAssistantShareLink)(conversation.id, conversation.shareToken);
  });
  readonly full = computed(() => {
    const max = this.maxParticipants();
    return max !== null && this.participants().length >= max;
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => clearTimeout(this.searchTimer));
  }

  async createLink(): Promise<void> {
    await this.run(() => this.store.createShareLink());
    this.copied.set(false);
  }

  async revokeLink(): Promise<void> {
    await this.run(() => this.store.revokeShareLink());
    this.copied.set(false);
  }

  async copy(input: HTMLInputElement): Promise<void> {
    const link = this.link();
    if (!link) {
      return;
    }
    try {
      await navigator.clipboard.writeText(link);
    } catch {
      // Without clipboard access the selected text can be copied by hand.
      input.select();
    }
    this.copied.set(true);
  }

  onSearch(event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.query.set(value);
    this.invited.set(null);
    clearTimeout(this.searchTimer);
    const revision = ++this.searchRevision;
    if (value.trim().length < 2) {
      this.candidates.set([]);
      this.searching.set(false);
      return;
    }
    this.searching.set(true);
    this.searchTimer = setTimeout(async () => {
      const results = await this.store.searchParticipantCandidates(value);
      if (revision === this.searchRevision) {
        this.candidates.set(results);
        this.searching.set(false);
      }
    }, searchDelayMs);
  }

  async invite(entry: DirectoryEntry): Promise<void> {
    const invited = await this.run(() => this.store.inviteParticipant(entry.actorId));
    if (invited) {
      this.invited.set(entry.displayName);
      this.candidates.update(items => items.filter(item => item.actorId !== entry.actorId));
    }
  }

  async remove(member: ConversationMember): Promise<void> {
    await this.run(() => this.store.removeParticipant(member.actorId));
  }

  async leave(): Promise<void> {
    const actorId = this.conversation()?.currentActorId;
    if (!actorId) {
      return;
    }
    if (await this.run(() => this.store.removeParticipant(actorId))) {
      this.closed.emit();
    }
  }

  isCurrentUser(member: ConversationMember): boolean {
    return member.actorId === this.conversation()?.currentActorId;
  }

  private async run<T>(action: () => Promise<T>): Promise<T | undefined> {
    if (this.busy()) {
      return undefined;
    }
    this.busy.set(true);
    try {
      return await action();
    } finally {
      this.busy.set(false);
    }
  }
}
