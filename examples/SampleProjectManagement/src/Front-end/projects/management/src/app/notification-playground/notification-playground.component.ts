import { CommonModule } from '@angular/common';
import { Component } from '@angular/core';
import {
  NhUserNotification,
  NhUserNotificationsAbstractComponent
} from '@newheap/platform-common';
import { TranslateModule } from '@ngx-translate/core';

@Component({
  selector: 'app-notification-playground',
  standalone: true,
  imports: [CommonModule, TranslateModule],
  templateUrl: './notification-playground.component.html',
  styleUrl: './notification-playground.component.scss'
})
export class NotificationPlaygroundComponent extends NhUserNotificationsAbstractComponent {
  async read(notification: NhUserNotification): Promise<void> {
    await this.markAsRead(notification);
  }

  async archiveItem(notification: NhUserNotification): Promise<void> {
    await this.archive(notification);
  }
}
