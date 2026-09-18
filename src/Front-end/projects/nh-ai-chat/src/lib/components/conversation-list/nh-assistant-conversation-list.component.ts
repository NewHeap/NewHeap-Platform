import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { ConversationSummary } from '../../models/assistant-api.models';
import { NhAssistantIconComponent } from '../../internal/nh-assistant-icon.component';

/** The user's conversations with loading and empty states; selecting one opens it. */
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
  readonly conversationDelete = output<string>();
  readonly conversationCreate = output<void>();
}
