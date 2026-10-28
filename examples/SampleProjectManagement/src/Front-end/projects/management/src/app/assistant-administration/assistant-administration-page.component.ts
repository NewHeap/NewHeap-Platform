import { Component } from '@angular/core';
import { NhAssistantAdminComponent } from '@newheap/platform-ai-chat/admin';
import { TranslateModule } from '@ngx-translate/core';
import { sampleAssistantAdminLocation } from './sample-assistant-admin-location';

/**
 * The assistant's administration against the real back-end, in the portal's own assistant scope:
 * the agents the application registers in code and those created here, such as the project
 * starter that the development seed creates as an agent that works on its own. SPM-253: the URL
 * keeps the open agent and its tab, so a link opens that agent's page.
 */
@Component({
  selector: 'app-assistant-administration-page',
  standalone: true,
  imports: [TranslateModule, NhAssistantAdminComponent],
  template: `
    <section class="assistant-administration-page">
      <p class="intro">{{ 'project.assistant-administration-intro' | translate }}</p>
      <nh-assistant-admin [location]="admin.location()" (locationChange)="admin.follow($event)" />
    </section>
  `,
  styles: [`
    :host { display: block; }
    .assistant-administration-page {
      margin-top: 24px;
      padding: clamp(18px, 3vw, 30px);
      border: 1px solid var(--line);
      border-radius: var(--radius-panel);
      background: var(--surface);
      box-shadow: var(--shadow-sm);
    }
    .intro {
      max-width: 72ch;
      margin: 0 0 16px;
      color: var(--muted);
      font-size: 14px;
    }
    @media (max-width: 760px) {
      .assistant-administration-page { margin-top: 16px; border-radius: var(--radius-card); }
    }
  `]
})
export class AssistantAdministrationPageComponent {
  readonly admin = sampleAssistantAdminLocation();
}
