import { Component, HostListener, OnDestroy, inject, signal } from '@angular/core';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import {
  NhAssistantAlertBadgeService,
  NhAssistantLauncherComponent,
  NhAssistantPanelComponent,
  NhAssistantPushService
} from '@newheap/platform-ai-chat';
import { NhAuthService, NhBackgroundOperationsBadgeComponent } from '@newheap/platform-common';
import { TranslateModule } from '@ngx-translate/core';
import { Subscription, filter } from 'rxjs';
import {
  SampleApiConnectionStateService,
  SampleUserMenuComponent
} from 'sample-project-management-common';

@Component({
  selector: 'app-management-layout',
  standalone: true,
  imports: [
    RouterLink,
    RouterLinkActive,
    RouterOutlet,
    TranslateModule,
    SampleUserMenuComponent,
    NhAssistantLauncherComponent,
    NhAssistantPanelComponent,
    NhBackgroundOperationsBadgeComponent
  ],
  templateUrl: './management-layout.component.html',
  styleUrl: './management-layout.component.scss'
})
export class ManagementLayoutComponent implements OnDestroy {
  private readonly router = inject(Router);
  private readonly auth = inject(NhAuthService);
  private readonly navigationSubscription: Subscription;
  private readonly notifiedRunSubscription: Subscription;
  private readonly notifiedAlertsSubscription: Subscription;
  private readonly releaseAlertBadge?: () => void;

  readonly connectionState = inject(SampleApiConnectionStateService);
  /** SPM-392: the person's unread alerts, which the bell and the navigation count. */
  readonly alertBadge = inject(NhAssistantAlertBadgeService);
  readonly navigationOpen = signal(false);
  readonly pageTitle = signal('Samplecatalogus');

  constructor() {
    this.syncPageTitle();
    this.navigationSubscription = this.router.events
      .pipe(filter(event => event instanceof NavigationEnd))
      .subscribe(() => {
        this.syncPageTitle();
        this.navigationOpen.set(false);
      });

    // SPM-310: a clicked notification about the proposals of a run opens the approval inbox of that
    // run, which lists every proposal of the run the person may decide, instead of one of its
    // conversations. The shell subscribes, so a notification that opens a new window finds it.
    const push = inject(NhAssistantPushService);
    this.notifiedRunSubscription = push.openRun$
      .subscribe(runId => void this.router.navigate(['/management/intake-reviews'], { queryParams: { run: runId } }));

    // SPM-392: a clicked notification of an alert, or of the morning overview, opens the Alerts page in
    // the window that is open; without one the push worker opens the page itself (`alertsUrl`).
    this.notifiedAlertsSubscription = push.openAlerts$
      .subscribe(() => void this.router.navigate(['/management/alerts']));

    // SPM-392: the shell keeps the badge of the person's alerts current while it shows: on load, on
    // focus, every few minutes and after the person's own actions on the alerts page.
    if (this.canUseAssistant()) {
      this.releaseAlertBadge = this.alertBadge.watch();
    }
  }

  ngOnDestroy(): void {
    this.navigationSubscription.unsubscribe();
    this.notifiedRunSubscription.unsubscribe();
    this.notifiedAlertsSubscription.unsubscribe();
    this.releaseAlertBadge?.();
  }

  canAdministerBackgroundOperations(): boolean {
    return this.auth.isOnePermissionGranted(['app.background-operation.administer']);
  }

  /** SPM-253: the assistant's access permission, which the Chat page needs. */
  canUseAssistant(): boolean {
    return this.auth.isOnePermissionGranted(['app.assistant.access']);
  }

  /** The assistant's access permission and the permission behind its admin policy. */
  canAdministerAssistant(): boolean {
    return this.auth.isOnePermissionGranted(['app.assistant.access']) && this.auth.isOnePermissionGranted(['app.project.manage']);
  }

  /** SPM-304: the assistant's access permission and the permission to read the projects. */
  canSeeProjectManagementBoard(): boolean {
    return this.auth.isOnePermissionGranted(['app.assistant.access']) && this.auth.isOnePermissionGranted(['app.project.view']);
  }

  /** SPM-083: the header badge opens the person's own background operations. */
  openBackgroundOperations(): void {
    void this.router.navigate(['/management/background-operations']);
  }

  toggleNavigation(): void {
    this.navigationOpen.update(open => !open);
  }

  closeNavigation(): void {
    this.navigationOpen.set(false);
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    this.closeNavigation();
  }

  private syncPageTitle(): void {
    let route = this.router.routerState.snapshot.root;
    while (route.firstChild) {
      route = route.firstChild;
    }
    this.pageTitle.set(route.title ?? 'Samplecatalogus');
  }
}
