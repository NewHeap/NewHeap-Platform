import { Component, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { TranslateModule } from '@ngx-translate/core';
import {
  AlertSubjectTarget,
  NhAssistantAlertDensity,
  NhAssistantAlertsComponent,
  NhAssistantNotificationPreferencesComponent
} from '@newheap/platform-ai-chat';

/** The page that shows the portal's projects, where a project's agent work and its subject links open. */
const projectPage = '/management/intake-reviews';

/**
 * SPM-392: a person's alerts in the management portal. The deadline watch warns a project's owner and
 * the people working on it when the project is due today; the list shows those warnings per division,
 * the parent every project names, in the person's language, with nothing to approve. The person marks
 * an alert read or dismisses it, and the shell's bell, which `NhAssistantAlertBadgeService` keeps
 * current, follows. Opening an alert emits its subject: the page routes it to the project with the link
 * the server gives the project type (`OpenAt`), and the list itself never navigates. Dismissing an alert
 * also offers to hear nothing more about its project for 7 or 30 days. Next to the list the person
 * chooses how the alerts reach them: desktop notifications, the times of the morning and afternoon
 * overviews in their own time zone, their quiet hours, the channels, and whether they receive the kind
 * at all; they also lift a project they muted there. Both parts read the person's settings once
 * (FB87-13).
 */
@Component({
  selector: 'app-alerts-page',
  standalone: true,
  imports: [TranslateModule, NhAssistantAlertsComponent, NhAssistantNotificationPreferencesComponent],
  templateUrl: './alerts-page.component.html',
  styleUrl: './alerts-page.component.scss'
})
export class AlertsPageComponent {
  private readonly router = inject(Router);

  /** One row per alert for a long list, as the inbox's compact list. */
  readonly density = signal<NhAssistantAlertDensity>('comfortable');

  toggleDensity(): void {
    this.density.update(density => (density === 'compact' ? 'comfortable' : 'compact'));
  }

  /**
   * Routes an opened alert to its object: the server's link of the subject type, else the project page
   * with the project chosen.
   */
  openSubject(target: AlertSubjectTarget): void {
    if (target.link && target.link.startsWith('/')) {
      void this.router.navigateByUrl(target.link);
      return;
    }
    if (target.subjectType === 'project') {
      void this.router.navigate([projectPage], { queryParams: { project: target.subjectId } });
    }
  }
}
