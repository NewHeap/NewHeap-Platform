import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { ToolResultPresentation } from '../../models/assistant-api.models';
import { NhAssistantCardPlaceholderComponent, nhAssistantFieldsHeight } from '../../internal/nh-assistant-card-placeholder.component';
import { NhAssistantToolResultCardViewComponent } from './nh-assistant-tool-result-card-view.component';

/**
 * The default card of what a tool call returned, from the application's trusted presentation: the
 * title, the optional summary, the fields (with what changed, when the presenter says so) and an
 * optional link to a page of the application, which opens through the router. It never shows the raw
 * result; the call's technical details stay in the tool-call card.
 *
 * Result cards are rarely on screen, so the card loads on first use, in a chunk of its own together with
 * its fields. Until it has arrived, and on the server, an empty box of about its height holds its place.
 */
@Component({
  selector: 'nh-assistant-tool-result-card',
  standalone: true,
  imports: [NhAssistantCardPlaceholderComponent, NhAssistantToolResultCardViewComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @defer (on immediate) {
      <nh-assistant-tool-result-card-view [presentation]="presentation()" [displayName]="displayName()" />
    } @placeholder {
      <nh-assistant-card-placeholder [height]="placeholderHeight()" />
    }
  `,
  styles: `
    :host {
      display: block;
    }
  `
})
export class NhAssistantToolResultCardComponent {
  readonly presentation = input.required<ToolResultPresentation>();
  /** The tool's name as people read it, shown above the title; none without it. */
  readonly displayName = input<string | null>(null);

  /** The card's height: its frame and title, then the tool's name, summary, fields and link it has. */
  readonly placeholderHeight = computed(() => {
    const presentation = this.presentation();
    let height = 46;
    if (this.displayName()) {
      height += 22;
    }
    if (presentation.summary) {
      height += 22;
    }
    if (presentation.fields.length > 0) {
      height += 10 + nhAssistantFieldsHeight(presentation.fields);
    }
    if (presentation.link) {
      height += 30;
    }

    return height;
  });
}
