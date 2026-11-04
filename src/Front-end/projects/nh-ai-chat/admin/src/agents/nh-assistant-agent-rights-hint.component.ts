import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { ɵNhAssistantIconComponent as NhAssistantIconComponent } from '@newheap/platform-ai-chat';

/**
 * The one hint next to what a person without the agent rights policy (`UseAgentRightsPolicy`) cannot change
 * (internal): the controls show their values, disabled, and this says who changes them. A disabled control takes
 * no focus, so the text is always visible rather than a tooltip; give it `hintId` and point the group at it with
 * `aria-describedby`.
 */
@Component({
  selector: 'nh-assistant-agent-rights-hint',
  standalone: true,
  imports: [TranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <p class="rights-hint" data-agent-rights-hint [id]="hintId()">
      <nh-assistant-icon name="lock" />
      <span>{{ 'nh-assistant.admin.agent-rights.hint' | translate }}</span>
    </p>
  `,
  styles: [`
    :host { display: block; min-width: 0; }
    .rights-hint {
      display: flex;
      align-items: flex-start;
      gap: 6px;
      margin: 4px 2px 0;
      color: var(--nha-text-muted);
      font-size: 12px;
      overflow-wrap: anywhere;
    }
    nh-assistant-icon { flex: none; margin-top: 1px; }
  `]
})
export class NhAssistantAgentRightsHintComponent {
  /** The id the group that holds the disabled controls describes itself with. */
  readonly hintId = input.required<string>();
}
