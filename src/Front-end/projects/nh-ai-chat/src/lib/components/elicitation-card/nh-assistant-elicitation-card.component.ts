import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { ElicitationAction, ElicitationPart } from '../../models/assistant-api.models';
import { NhAssistantCardPlaceholderComponent } from '../../internal/nh-assistant-card-placeholder.component';
import { NhAssistantElicitationCardViewComponent } from './nh-assistant-elicitation-card-view.component';

/** A value the person entered in one field, before it is sent. */
type NhAssistantElicitationValue = string | number | boolean;

/** The person's answer to a request for input of an MCP server. */
export interface NhAssistantElicitationAnswer {
  action: ElicitationAction;
  /** The values of an accepted form, by field name. */
  content?: Record<string, NhAssistantElicitationValue> | null;
}

/**
 * A request for input of an MCP server whose tool the turn runs: the server's own message attributed
 * to the server, never to the assistant, and who receives the answer. A form asks for its fields and
 * sends them only when they fit; a link shows its host and address and opens only when the person
 * clicks it, after which they say they are done. Only the person whose turn it is answers (`canAnswer`);
 * everyone else sees it read-only. It counts down to the end of the tool call's wait.
 *
 * Requests for input are rarely on screen, so the card loads on first use, in a chunk of its own. Until
 * it has arrived, and on the server, an empty box of about its height holds its place; the host's
 * `data-status` is the server's status, the card's own also says when the wait ended.
 */
@Component({
  selector: 'nh-assistant-elicitation-card',
  standalone: true,
  imports: [NhAssistantCardPlaceholderComponent, NhAssistantElicitationCardViewComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @defer (on immediate) {
      <nh-assistant-elicitation-card-view
        [elicitation]="elicitation()"
        [canAnswer]="canAnswer()"
        [answering]="answering()"
        (answer)="answer.emit($event)" />
    } @placeholder {
      <nh-assistant-card-placeholder [height]="placeholderHeight()" />
    }
  `,
  styles: `
    :host {
      display: block;
    }
  `,
  host: {
    '[attr.data-status]': 'elicitation().status',
    '[attr.data-mode]': 'elicitation().mode'
  }
})
export class NhAssistantElicitationCardComponent {
  readonly elicitation = input.required<ElicitationPart>();
  /** True when the current user is the person whose turn it is and may answer. */
  readonly canAnswer = input(false);
  /** True while the host sends an answer; disables the actions. */
  readonly answering = input(false);
  /** The person's answer; emitted at most once until the host reports it is no longer answering. */
  readonly answer = output<NhAssistantElicitationAnswer>();

  /**
   * The card's height: its frame, header, sender and a line of the message, then the link, the form's
   * fields or the list of what was asked, and the actions with the countdown or the outcome.
   */
  readonly placeholderHeight = computed(() => {
    const elicitation = this.elicitation();
    const actionable = elicitation.status === 'pending' && this.canAnswer();
    let height = 110;
    if (elicitation.mode === 'url') {
      height += actionable ? 80 : 52;
    } else if (actionable) {
      height += elicitation.fields.reduce((sum, field) => sum + (field.type === 'boolean' ? 30 : 68), 2);
    } else if (elicitation.fields.length > 0) {
      height += 10 + 20 * elicitation.fields.length;
    }

    return height + (actionable ? 104 : 28);
  });
}
