import { NgComponentOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, Injector, Signal, Type, computed, inject, input, untracked } from '@angular/core';
import { ToolCallPart, ToolResultPresentation } from '../../models/assistant-api.models';
import { NhAssistantToolResultCardComponent } from './nh-assistant-tool-result-card.component';
import { NH_ASSISTANT_TOOL_RESULT_RENDERERS, NhAssistantToolResult } from './nh-assistant-tool-result-renderers';

/** The result a renderer injects, read from the call the host shows. */
class NhAssistantToolResultOfCall extends NhAssistantToolResult {
  constructor(
    readonly toolId: string,
    readonly toolVersion: number,
    readonly invocationId: string,
    readonly displayName: Signal<string>,
    readonly presentation: Signal<ToolResultPresentation>
  ) {
    super();
  }
}

/**
 * Shows the trusted result card of one tool call (`resultPresentation`): the application's renderer
 * for the tool (`provideNhAssistantToolResultRenderers`), which injects `NhAssistantToolResult`, or
 * otherwise the default `nh-assistant-tool-result-card`. It shows nothing for a call without a card,
 * such as a failed call, a confidential tool or a result shielded from the reader. The thread shows it
 * under the call's tool group.
 */
@Component({
  selector: 'nh-assistant-tool-result',
  standalone: true,
  imports: [NgComponentOutlet, NhAssistantToolResultCardComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (presentation(); as card) {
      @if (renderer(); as view) {
        <ng-container *ngComponentOutlet="view.component; injector: view.injector" />
      } @else {
        <nh-assistant-tool-result-card [presentation]="card" [displayName]="part().displayName" />
      }
    }
  `,
  host: {
    '[attr.data-tool-id]': 'part().toolId'
  }
})
export class NhAssistantToolResultComponent {
  private readonly injector = inject(Injector);
  private readonly renderers = inject(NH_ASSISTANT_TOOL_RESULT_RENDERERS, { optional: true }) ?? [];

  readonly part = input.required<ToolCallPart>();

  readonly presentation = computed(() => this.part().resultPresentation ?? null);
  private readonly toolId = computed(() => this.part().toolId);

  /**
   * The application's renderer of the tool with the injector that gives it the result, or `null` for
   * the default card. It is made once per tool, so the renderer keeps its state while the
   * presentation updates.
   */
  readonly renderer = computed<{ component: Type<unknown>; injector: Injector } | null>(() => {
    const toolId = this.toolId();
    const renderer = [...this.renderers].reverse().find(item => item.toolId === toolId);
    if (!renderer) {
      return null;
    }

    const part = untracked(this.part);
    const result = new NhAssistantToolResultOfCall(
      part.toolId,
      part.toolVersion,
      part.invocationId,
      computed(() => this.part().displayName),
      // The renderer is only shown while there is a presentation.
      computed(() => this.part().resultPresentation as ToolResultPresentation));
    return {
      component: renderer.component,
      injector: Injector.create({
        providers: [{ provide: NhAssistantToolResult, useValue: result }],
        parent: this.injector
      })
    };
  });
}
