import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { ToolCallPart } from '../../models/assistant-api.models';
import { NhAssistantIconComponent, NhAssistantIconName } from '../../internal/nh-assistant-icon.component';
import { NhAssistantToolCallCardComponent } from '../tool-call-card/nh-assistant-tool-call-card.component';

let nextId = 0;

/** Overall state of a run of tool calls, from most to least urgent. */
export type NhAssistantToolCallGroupState = 'awaiting-approval' | 'running' | 'failed' | 'succeeded';

const stateIcons: Record<NhAssistantToolCallGroupState, NhAssistantIconName> = {
  'awaiting-approval': 'clock',
  running: 'tool',
  failed: 'warning',
  succeeded: 'check'
};

/**
 * Summarizes consecutive tool calls in one collapsed line that counts along live: the
 * tool that runs now while the assistant works, and how many tools were used and failed
 * afterwards. Expanding it shows every call as a card.
 */
@Component({
  selector: 'nh-assistant-tool-call-group',
  standalone: true,
  imports: [TranslatePipe, NhAssistantIconComponent, NhAssistantToolCallCardComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-tool-call-group.component.html',
  styleUrl: './nh-assistant-tool-call-group.component.scss',
  host: {
    '[attr.data-status]': 'state()'
  }
})
export class NhAssistantToolCallGroupComponent {
  readonly calls = input.required<readonly ToolCallPart[]>();

  readonly callsId = `nh-assistant-tool-call-group-${nextId++}`;
  readonly expanded = signal(false);

  /** The call that currently needs attention or runs; the latest one when several do. */
  readonly current = computed(() => {
    const calls = this.calls();
    return findLast(calls, call => call.status === 'awaiting-approval')
      ?? findLast(calls, call => call.status === 'running')
      ?? null;
  });
  readonly total = computed(() => this.calls().length);
  readonly finished = computed(() => this.calls().filter(call => !isActive(call)).length);
  readonly failed = computed(() => this.calls().filter(call => call.status === 'failed').length);
  readonly state = computed<NhAssistantToolCallGroupState>(() => {
    const current = this.current();
    if (current) {
      return current.status === 'awaiting-approval' ? 'awaiting-approval' : 'running';
    }

    return this.failed() > 0 ? 'failed' : 'succeeded';
  });
  readonly stateIcon = computed(() => stateIcons[this.state()]);

  toggle(): void {
    this.expanded.update(expanded => !expanded);
  }
}

function isActive(call: ToolCallPart): boolean {
  return call.status === 'running' || call.status === 'awaiting-approval';
}

function findLast(calls: readonly ToolCallPart[], predicate: (call: ToolCallPart) => boolean): ToolCallPart | undefined {
  for (let index = calls.length - 1; index >= 0; index--) {
    if (predicate(calls[index])) {
      return calls[index];
    }
  }

  return undefined;
}
