import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { ConversationSummary } from '../../models/assistant-api.models';
import { NhAssistantIconComponent } from '../../internal/nh-assistant-icon.component';
import { nhAssistantIsUnread } from '../../services/nh-assistant.store';

/**
 * The user's own and shared conversations with loading and empty states; selecting one opens it.
 * Each row marks a running turn, a waiting approval and unread messages. A participant leaves a
 * shared conversation instead of deleting it.
 */
@Component({
  selector: 'nh-assistant-conversation-list',
  standalone: true,
  imports: [DatePipe, TranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-conversation-list.component.html',
  styleUrl: './nh-assistant-conversation-list.component.scss'
})
export class NhAssistantConversationListComponent {
  readonly conversations = input.required<readonly ConversationSummary[]>();
  readonly total = input(0);
  readonly activeConversationId = input<string | null>(null);
  readonly loading = input(false);
  readonly disabled = input(false);

  readonly conversationSelect = output<string>();
  /** The owner deletes the conversation; a participant leaves it. */
  readonly conversationDelete = output<string>();
  readonly conversationCreate = output<void>();

  unread(conversation: ConversationSummary): boolean {
    return nhAssistantIsUnread(conversation);
  }

  indicator(conversation: ConversationSummary): 'running' | 'waiting' | 'unread' | null {
    if (conversation.status === 'running') {
      return 'running';
    }
    if (conversation.status === 'waiting-for-approval') {
      return 'waiting';
    }
    return this.unread(conversation) ? 'unread' : null;
  }
}
