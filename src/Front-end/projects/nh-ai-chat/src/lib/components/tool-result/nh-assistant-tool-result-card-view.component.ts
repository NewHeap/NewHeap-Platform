import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { ToolResultPresentation } from '../../models/assistant-api.models';
import { NhAssistantIconComponent } from '../../internal/nh-assistant-icon.component';
import { NhAssistantMessageLinkComponent } from '../../internal/nh-assistant-message-link.component';
import { NhAssistantPresentationFieldsViewComponent } from '../presentation-fields/nh-assistant-presentation-fields-view.component';
import { NhAssistantTokensDirective } from '../../internal/nh-assistant-tokens';

/**
 * The card of `nh-assistant-tool-result-card` (internal), which loads it on first use: the title, the
 * optional summary, the fields with what changed and the optional link. It shows the fields' list
 * directly, so the card and its fields arrive together.
 */
@Component({
  selector: 'nh-assistant-tool-result-card-view',
  standalone: true,
  hostDirectives: [NhAssistantTokensDirective],
  imports: [TranslatePipe, NhAssistantIconComponent, NhAssistantMessageLinkComponent, NhAssistantPresentationFieldsViewComponent],
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
        <nh-assistant-presentation-fields-view class="fields" mode="result" [fields]="presentation().fields" />
      }
      @if (presentation().link; as link) {
        <nh-assistant-message-link
          class="link"
          [url]="link.url"
          [label]="link.label || ('nh-assistant.tool-result.open-link' | translate)" />
      }
    </article>
  `,
  styleUrl: './nh-assistant-tool-result-card-view.component.scss'
})
export class NhAssistantToolResultCardViewComponent {
  readonly presentation = input.required<ToolResultPresentation>();
  readonly displayName = input<string | null>(null);
}
