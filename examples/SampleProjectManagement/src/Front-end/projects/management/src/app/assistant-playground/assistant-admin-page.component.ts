import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { NhAssistantAdminComponent } from '@newheap/platform-ai-chat/admin';
import { TranslateModule } from '@ngx-translate/core';

/**
 * Hosts the library's administration page in the playground scope. A real host routes to
 * `NhAssistantAdminComponent` behind its admin permission guard.
 */
@Component({
  selector: 'app-assistant-admin-page',
  standalone: true,
  imports: [RouterLink, TranslateModule, NhAssistantAdminComponent],
  template: `
    <section class="assistant-admin-page">
      <a class="back" routerLink="/management/assistant">
        <i class="ph ph-arrow-left" aria-hidden="true"></i>{{ 'project.assistant-admin-back' | translate }}
      </a>
      <nh-assistant-admin />
    </section>
  `,
  styles: [`
    :host { display: block; }
    .assistant-admin-page {
      margin-top: 24px;
      padding: clamp(18px, 3vw, 30px);
      border: 1px solid var(--line);
      border-radius: var(--radius-panel);
      background: var(--surface);
      box-shadow: var(--shadow-sm);
    }
    .back {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      margin-bottom: 16px;
      color: var(--brand);
      font-size: 13px;
      font-weight: 700;
      text-decoration: none;
    }
    .back:hover { text-decoration: underline; }
    @media (max-width: 760px) {
      .assistant-admin-page { margin-top: 16px; border-radius: var(--radius-card); }
    }
  `]
})
export class AssistantAdminPageComponent {}
