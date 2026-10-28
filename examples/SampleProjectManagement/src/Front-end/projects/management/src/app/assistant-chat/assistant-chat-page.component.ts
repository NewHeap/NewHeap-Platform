import { Component } from '@angular/core';
import { NhAssistantPanelComponent } from '@newheap/platform-ai-chat';
import { TranslateModule } from '@ngx-translate/core';

/**
 * SPM-253: the assistant's chat on a page of its own, for long sessions. The same panel as the
 * layout's drawer renders in place with `mode="page"`: on wide screens the conversations stay listed
 * beside the thread, and it shares them with the drawer of the portal's assistant scope.
 */
@Component({
  selector: 'app-assistant-chat-page',
  standalone: true,
  imports: [TranslateModule, NhAssistantPanelComponent],
  template: `
    <section class="assistant-chat-page" [attr.aria-label]="'project.assistant-chat-nav' | translate">
      <nh-assistant-panel mode="page" />
    </section>
  `,
  styles: [`
    :host { display: block; }
    .assistant-chat-page {
      height: max(440px, calc(100dvh - 72px - 88px));
      margin-top: 24px;
    }
    @media (max-width: 760px) {
      .assistant-chat-page { height: max(420px, calc(100dvh - 64px - 56px)); margin-top: 16px; }
    }
  `]
})
export class AssistantChatPageComponent {}
