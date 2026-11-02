import { ChangeDetectionStrategy, Component, inject, output } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { NhAssistantPushService } from '../services/nh-assistant-push.service';

let nextId = 0;

/**
 * The switch of desktop notifications for every browser of the person, with this browser's state. It
 * shows nothing while Web Push is unavailable. `NhAssistantPushService` owns the choice; the user's
 * preferences and their notification choices both show this one switch.
 */
@Component({
  selector: 'nh-assistant-push-switch',
  standalone: true,
  imports: [TranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (push.state() !== 'unavailable') {
      <fieldset class="notifications">
        <legend>{{ 'nh-assistant.preferences.notifications-label' | translate }}</legend>
        <label class="switch">
          <input
            type="checkbox"
            [checked]="push.settings()?.pushEnabled === true"
            [disabled]="push.busy()"
            [attr.aria-describedby]="id + '-hint'"
            (change)="toggle($event)" />
          <span>{{ 'nh-assistant.preferences.notifications' | translate }}</span>
        </label>
        <p class="hint" [id]="id + '-hint'">
          <span>{{ 'nh-assistant.preferences.notifications-state.' + push.state() | translate }}</span>
        </p>
      </fieldset>
    }
  `,
  styleUrl: './nh-assistant-push-switch.component.scss'
})
export class NhAssistantPushSwitchComponent {
  readonly push = inject(NhAssistantPushService);
  readonly id = `nh-assistant-push-switch-${nextId++}`;

  /** Emits the failure when the choice could not be stored, for the hosting form to show. */
  readonly failed = output<unknown>();

  /** Turns desktop notifications on or off; turning them on asks the browser for permission. */
  async toggle(event: Event): Promise<void> {
    const enabled = (event.target as HTMLInputElement).checked;
    try {
      await this.push.setEnabled(enabled);
    } catch (error) {
      this.failed.emit(error);
    }
  }
}
