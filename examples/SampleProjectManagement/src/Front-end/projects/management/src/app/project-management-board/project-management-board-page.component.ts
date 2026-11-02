import { Component, OnInit, inject } from '@angular/core';
import { Router } from '@angular/router';
import { TranslateModule } from '@ngx-translate/core';
import { NhAssistantPanelService, NhAssistantStore } from '@newheap/platform-ai-chat';
import { NhAssistantAgentBoardComponent, NhAssistantBoardInboxRequest } from '@newheap/platform-ai-chat/agent-runs';

/**
 * SPM-304: the project management board against the real back-end. The server registers the
 * "Project management" group of agents in code with English and Dutch names; the project closer and
 * the project planner join it in code, the project follow-up, created as data, in the
 * administration. `nh-assistant-agent-board` shows the agents the signed-in person may see: the
 * follow-up's latest result for the whole application to everyone who passes its view policy
 * (`app.project.view`), "New" for what arrived since the person's last visit of this board, "Run now"
 * for project managers, its proposals and its history. Showing the page records the visit.
 *
 * The board is compact: what an agent is about folds behind "About this agent", the latest result shows
 * two lines with "More", and the count of an agent's open proposals opens the portal's inbox on the
 * intake reviews page, filtered on that agent and the board's division (`openInbox`), because the
 * library reads no route parameters.
 */
@Component({
  selector: 'app-project-management-board-page',
  standalone: true,
  imports: [TranslateModule, NhAssistantAgentBoardComponent],
  template: `
    <section class="project-management-board-page">
      <p class="intro">{{ 'project-management-board.description' | translate }}</p>
      <nh-assistant-agent-board
        [group]="group"
        [compact]="true"
        (openConversation)="openConversation($event)"
        (openInbox)="openInbox($event)" />
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
  private readonly router = inject(Router);

  /** The group the server registers with `AddAgentGroup`. */
  readonly group = 'project-management';

  ngOnInit(): void {
    // The portal's launcher normally starts the assistant scope already; starting it twice is safe.
    void this.assistant.initialize();
  }

  openConversation(conversationId: string): void {
    this.panel.open(conversationId);
  }

  /** The portal's inbox of the agent's open proposals, in the board's scope. */
  openInbox(request: NhAssistantBoardInboxRequest): void {
    void this.router.navigate(['/management/intake-reviews'], {
      queryParams: { agent: request.agentId, division: request.divisionId ?? undefined }
    });
  }
}
