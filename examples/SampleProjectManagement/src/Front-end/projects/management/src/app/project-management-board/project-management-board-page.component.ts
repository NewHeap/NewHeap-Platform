import { Component, OnInit, inject } from '@angular/core';
import { TranslateModule } from '@ngx-translate/core';
import { NhAssistantPanelService, NhAssistantStore } from '@newheap/platform-ai-chat';
import { NhAssistantAgentBoardComponent } from '@newheap/platform-ai-chat/agent-runs';

/**
 * SPM-304: the project management board against the real back-end. The server registers the
 * "Project management" group of agents in code with English and Dutch names; the project closer and
 * the project planner join it in code, the project follow-up, created as data, in the
 * administration. `nh-assistant-agent-board` shows the agents the signed-in person may see: the
 * follow-up's latest result for the whole application to everyone who passes its view policy
 * (`app.project.view`), "New" for what arrived since the person's last visit of this board, "Run now"
 * for project managers, its proposals and its history. Showing the page records the visit.
 */
@Component({
  selector: 'app-project-management-board-page',
  standalone: true,
  imports: [TranslateModule, NhAssistantAgentBoardComponent],
  template: `
    <section class="project-management-board-page">
      <p class="intro">{{ 'project-management-board.description' | translate }}</p>
      <nh-assistant-agent-board [group]="group" (openConversation)="openConversation($event)" />
    </section>
  `,
  styles: [`
    :host { display: block; }
    .project-management-board-page {
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
      .project-management-board-page { margin-top: 16px; border-radius: var(--radius-card); }
    }
  `]
})
export class ProjectManagementBoardPageComponent implements OnInit {
  private readonly panel = inject(NhAssistantPanelService);
  private readonly assistant = inject(NhAssistantStore);

  /** The group the server registers with `AddAgentGroup`. */
  readonly group = 'project-management';

  ngOnInit(): void {
    // The portal's launcher normally starts the assistant scope already; starting it twice is safe.
    void this.assistant.initialize();
  }

  openConversation(conversationId: string): void {
    this.panel.open(conversationId);
  }
}
