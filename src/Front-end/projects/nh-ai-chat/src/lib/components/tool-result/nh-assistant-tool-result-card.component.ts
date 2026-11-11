import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { ToolResultPresentation } from '../../models/assistant-api.models';
import { NhAssistantIconComponent } from '../../internal/nh-assistant-icon.component';
import { NhAssistantMessageLinkComponent } from '../../internal/nh-assistant-message-link.component';
import { NhAssistantPresentationFieldsComponent } from '../presentation-fields/nh-assistant-presentation-fields.component';

/**
 * The default card of what a tool call returned, from the application's trusted presentation: the
 * title, the optional summary, the fields (with what changed, when the presenter says so) and an
 * optional link to a page of the application, which opens through the router. It never shows the raw
 * result; the call's technical details stay in the tool-call card.
 */
@Component({
  selector: 'nh-assistant-tool-result-card',
  standalone: true,
  imports: [TranslatePipe, NhAssistantIconComponent, NhAssistantMessageLinkComponent, NhAssistantPresentationFieldsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <article
      class="card"
      [attr.aria-label]="'nh-assistant.tool-result.label' | translate: { name: displayName() ?? presentation().title }">
      @if (displayName(); as name) {
        <p class="tool">
          <nh-assistant-icon name="check" />
          <span>{{ name }}</span>
        </p>
      }
      <h4 class="title">{{ presentation().title }}</h4>
      @if (presentation().summary; as summary) {
        <p class="summary">{{ summary }}</p>
      }
      @if (presentation().fields.length > 0) {
        <nh-assistant-presentation-fields class="fields" mode="result" [fields]="presentation().fields" />
      }
      @if (presentation().link; as link) {
        <nh-assistant-message-link
          class="link"
          [url]="link.url"
          [label]="link.label || ('nh-assistant.tool-result.open-link' | translate)" />
      }
    </article>
  `,
  styleUrl: './nh-assistant-tool-result-card.component.scss'
})
export class NhAssistantToolResultCardComponent {
  readonly presentation = input.required<ToolResultPresentation>();
  /** The tool's name as people read it, shown above the title; none without it. */
  readonly displayName = input<string | null>(null);
}
