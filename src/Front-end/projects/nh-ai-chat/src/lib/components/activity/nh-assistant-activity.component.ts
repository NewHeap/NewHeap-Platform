import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { NhAssistantIconComponent } from '../../internal/nh-assistant-icon.component';
import { NhAssistantActivity } from '../../services/nh-assistant.store';

/**
 * One slim row of the other conversations that need attention: a running turn, an approval that
 * waits, or an unread answer. It renders nothing when there are none, so it costs no space in the
 * usual case. Selecting a chip opens that conversation; its turn keeps running meanwhile.
 */
@Component({
  selector: 'nh-assistant-activity',
  standalone: true,
  imports: [TranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (others().length > 0) {
      <nav class="strip" [attr.aria-label]="'nh-assistant.activity.label' | translate">
        @for (item of others(); track item.id) {
          <button
            class="chip"
            type="button"
            [attr.data-state]="state(item)"
            [attr.title]="(item.title || ('nh-assistant.conversation-list.untitled' | translate)) + ' — ' + ('nh-assistant.activity.' + state(item) | translate)"
            (click)="conversationSelect.emit(item.id)">
            <span class="indicator" aria-hidden="true"></span>
            <span class="title">{{ item.title || ('nh-assistant.conversation-list.untitled' | translate) }}</span>
            @if (item.shared) {
              <nh-assistant-icon class="shared" name="users" />
            }
            <span class="visually-hidden">{{ 'nh-assistant.activity.' + state(item) | translate }}</span>
          </button>
        }
      </nav>
    }
  `,
  styleUrl: './nh-assistant-activity.component.scss'
})
export class NhAssistantActivityComponent {
  readonly items = input.required<readonly NhAssistantActivity[]>();
  readonly conversationSelect = output<string>();

  /** The open conversation already shows its own state. */
  readonly others = computed(() => this.items().filter(item => !item.active));

  state(item: NhAssistantActivity): 'running' | 'waiting' | 'unread' {
    if (item.status === 'running') {
      return 'running';
    }
    return item.status === 'waiting-for-approval' ? 'waiting' : 'unread';
  }
}
