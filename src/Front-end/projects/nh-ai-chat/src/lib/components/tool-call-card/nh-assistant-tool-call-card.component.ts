import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { ToolCallPart } from '../../models/assistant-api.models';
import { NhAssistantIconComponent, NhAssistantIconName } from '../../internal/nh-assistant-icon.component';
import { NhAssistantTranslatePipe } from '../../internal/nh-assistant-translate.pipe';

let nextId = 0;

const statusIcons: Record<ToolCallPart['status'], NhAssistantIconName> = {
  running: 'tool',
  succeeded: 'check',
  failed: 'warning',
  'awaiting-approval': 'clock',
  rejected: 'x'
};

/** Shows one tool invocation with its status and expandable input and result previews. */
@Component({
  selector: 'nh-assistant-tool-call-card',
  standalone: true,
  imports: [TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-tool-call-card.component.html',
  styleUrl: './nh-assistant-tool-call-card.component.scss',
  host: {
    '[attr.data-status]': 'part().status'
  }
})
export class NhAssistantToolCallCardComponent {
  readonly part = input.required<ToolCallPart>();

  readonly detailsId = `nh-assistant-tool-call-${nextId++}`;
  readonly expanded = signal(false);
  readonly statusIcon = computed(() => statusIcons[this.part().status]);
  readonly hasDetails = computed(() => !!this.part().toolId);

  toggle(): void {
    this.expanded.update(expanded => !expanded);
  }
}
