import { CommonModule } from '@angular/common';
import { Component, ElementRef, HostListener, Input, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { Router, RouterLink } from '@angular/router';
import { NhUserNotification, NhUserNotificationsAbstractComponent } from '@newheap/platform-common';
import { TranslateModule, TranslateService } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import { SampleAuthService } from './sample-auth.service';

@Component({
  selector: 'app-sample-user-menu',
  standalone: true,
  imports: [CommonModule, RouterLink, TranslateModule],
  templateUrl: './sample-user-menu.component.html',
  styleUrl: './sample-user-menu.component.scss'
})
export class SampleUserMenuComponent extends NhUserNotificationsAbstractComponent {
  private readonly elementRef = inject(ElementRef<HTMLElement>);
  private readonly appRouter = inject(Router);
  private readonly sampleAuthService = inject(SampleAuthService);
  private readonly translate = inject(TranslateService);
  private readonly authSubscription: Subscription;
  private readonly languageChanged = toSignal(this.translate.onLangChange, { initialValue: null });

  @Input() profileRoute = '/profile';

  readonly authorization = signal(this.sampleAuthService.getAuthorization());
  readonly userMenuOpen = signal(false);
  readonly notificationsOpen = signal(false);
  readonly notificationsBusy = signal(false);
  readonly notificationError = signal('');
  readonly loggingOut = signal(false);
  readonly userEmail = computed(() => {
    this.languageChanged();
    return this.authorization()?.user?.email
      ?? String(this.translate.instant('project.account-menu.unknown-user'));
  });
  readonly userInitials = computed(() => this.userEmail()
    .split('@')[0]
    .split(/[._-]/)
    .filter(Boolean)
    .map(part => part[0])
    .join('')
    .slice(0, 2)
    .toUpperCase() || '?');

  constructor() {
    super();
    this.authSubscription = this.sampleAuthService.authSubject.subscribe(authorization =>
      this.authorization.set(authorization));
  }

  override async appOnDestroy(): Promise<void> {
    this.authSubscription.unsubscribe();
    await super.appOnDestroy();
  }

  toggleUserMenu(): void {
    this.userMenuOpen.update(open => !open);
    this.notificationsOpen.set(false);
  }

  toggleNotifications(): void {
    this.notificationsOpen.update(open => !open);
    this.userMenuOpen.set(false);
    this.notificationError.set('');
  }

  async clearNotifications(): Promise<void> {
    if (this.notificationsBusy()) {
      return;
    }

    this.notificationsBusy.set(true);
    this.notificationError.set('');
    try {
      const markResult = await this.markAllAsRead();
      if (!markResult.isSuccess) {
        this.notificationError.set(this.translate.instant('project.account-menu.notifications-mark-all-failed'));
        return;
      }

      const archiveResult = await this.archiveAll();
      if (!archiveResult.isSuccess) {
        this.notificationError.set(this.translate.instant('project.account-menu.notifications-archive-failed'));
        return;
      }
      this.notificationsOpen.set(false);
    } finally {
      this.notificationsBusy.set(false);
    }
  }

  async openNotification(notification: NhUserNotification): Promise<void> {
    if (!notification.isLastRead) {
      const result = await this.markAsRead(notification);
      if (!result.isSuccess) {
        this.notificationError.set(this.translate.instant('project.account-menu.notification-mark-read-failed'));
        return;
      }
    }

    this.notificationsOpen.set(false);
    const url = this.getNotificationUrl(notification);
    if (!url) {
      return;
    }

    const parsedUrl = new URL(url, window.location.origin);
    if (notification.data?.urlInNewTab || parsedUrl.origin !== window.location.origin) {
      window.open(url, '_blank', 'noopener');
      return;
    }

    await this.appRouter.navigateByUrl(`${parsedUrl.pathname}${parsedUrl.search}${parsedUrl.hash}`);
  }

  async logout(): Promise<void> {
    if (this.loggingOut()) {
      return;
    }

    this.loggingOut.set(true);
    this.userMenuOpen.set(false);
    try {
      await this.sampleAuthService.logout();
      await this.appRouter.navigateByUrl('/auth/login');
    } finally {
      this.loggingOut.set(false);
    }
  }

  closeMenus(): void {
    this.userMenuOpen.set(false);
    this.notificationsOpen.set(false);
  }

  @HostListener('document:click', ['$event'])
  onDocumentClick(event: MouseEvent): void {
    const target = event.target as Node | null;
    if (target && !this.elementRef.nativeElement.contains(target)) {
      this.closeMenus();
    }
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    this.closeMenus();
  }

  private getNotificationUrl(notification: NhUserNotification): string | undefined {
    if ((notification.data?.url?.length ?? 0) > 0) {
      return notification.data.url;
    }

    return notification.lastMessage
      ?.match(/https?:\/\/\S+/)?.[0]
      ?.replace(/[.,;)]$/, '');
  }
}
