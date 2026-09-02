import { CommonModule } from '@angular/common';
import { Component, inject } from '@angular/core';
import {
  NhUserNotification,
  NhUserNotificationsAbstractComponent
} from '@newheap/platform-common';
import { NhToastrService } from '@newheap/nh-toastr';
import { TranslateModule, TranslateService } from '@ngx-translate/core';

@Component({
  selector: 'app-notification-playground',
  standalone: true,
  imports: [CommonModule, TranslateModule],
  templateUrl: './notification-playground.component.html',
  styleUrl: './notification-playground.component.scss'
})
export class NotificationPlaygroundComponent extends NhUserNotificationsAbstractComponent {
  private readonly toastr = inject(NhToastrService);
  private readonly translate = inject(TranslateService);

  showToast(): void {
    this.toastr.success(
      this.translate.instant('project.toast-sample-message'),
      this.translate.instant('project.toast-sample-title')
    );
  }

  async read(notification: NhUserNotification): Promise<void> {
    await this.markAsRead(notification);
  }

  async archiveItem(notification: NhUserNotification): Promise<void> {
    await this.archive(notification);
  }
}
