import { ChangeDetectionStrategy, Component, afterNextRender, computed, effect, inject, untracked } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { NhAssistantIconComponent } from '../../internal/nh-assistant-icon.component';
import { NhAssistantPanelService } from '../../services/nh-assistant-panel.service';
import { NhAssistantStore } from '../../services/nh-assistant.store';

/**
 * Header button that opens the assistant panel. It renders nothing while the assistant is
 * disabled on the server or the access policy denies the user. A badge marks a conversation
 * that waits for approval. Projected content replaces the default icon.
 */
@Component({
  selector: 'nh-assistant-launcher',
  standalone: true,
  imports: [TranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (store.enabled()) {
      <button
        class="launcher"
        type="button"
        [class.open]="panel.isOpen()"
        [attr.aria-expanded]="panel.isOpen()"
        [attr.aria-label]="label() | translate"
        [attr.title]="label() | translate"
        (click)="panel.toggle()">
        <ng-content><nh-assistant-icon name="assistant" /></ng-content>
        @if (waitingForApproval()) {
          <span class="badge" aria-hidden="true"></span>
          <span class="visually-hidden">{{ 'nh-assistant.launcher.pending-approval' | translate }}</span>
        }
      </button>
    }
  `,
  styleUrl: './nh-assistant-launcher.component.scss'
})
export class NhAssistantLauncherComponent {
  readonly store = inject(NhAssistantStore);
  readonly panel = inject(NhAssistantPanelService);

  readonly waitingForApproval = computed(() =>
    this.store.pendingApproval() !== null ||
    this.store.conversations().some(conversation => conversation.status === 'waiting-for-approval')
  );
  readonly label = computed(() => this.panel.isOpen() ? 'nh-assistant.launcher.close' : 'nh-assistant.launcher.open');

  constructor() {
    // Browser only: the status request and access policy need the signed-in user.
    afterNextRender(() => {
      void this.store.initialize();
    });

    effect(() => {
      if (this.store.enabled()) {
        untracked(() => void this.store.refreshConversations());
      }
    });
  }
}
