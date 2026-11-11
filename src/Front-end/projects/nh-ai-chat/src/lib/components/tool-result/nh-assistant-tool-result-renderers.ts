import { EnvironmentProviders, InjectionToken, Signal, Type, makeEnvironmentProviders } from '@angular/core';
import { ToolResultPresentation } from '../../models/assistant-api.models';

/**
 * The application's own card for the results of one tool: a standalone component that the chat
 * renders instead of the default `nh-assistant-tool-result-card` for every call of the tool that the
 * server presented (`resultPresentation`). The component injects `NhAssistantToolResult` for the
 * presentation; it never receives the call's raw result or arguments.
 */
export interface NhAssistantToolResultRenderer {
  /** The tool id, such as `projects.get`, for every version of the tool. */
  toolId: string;
  /** The application's standalone component that shows the presentation. */
  component: Type<unknown>;
}

/** The renderers the application registers for the results of its tools. */
export const NH_ASSISTANT_TOOL_RESULT_RENDERERS = new InjectionToken<NhAssistantToolResultRenderer[]>(
  'NH_ASSISTANT_TOOL_RESULT_RENDERERS');

/**
 * Registers the application's renderers of tool results, where the assistant is provided (next to
 * `provideNhAssistant`, in the application's configuration or a route's providers). A call of a tool
 * without a renderer shows the default card. Several calls add their renderers; when two name the same
 * tool, the one registered last renders it.
 */
export function provideNhAssistantToolResultRenderers(...renderers: NhAssistantToolResultRenderer[]): EnvironmentProviders {
  return makeEnvironmentProviders(renderers.map(renderer => ({
    provide: NH_ASSISTANT_TOOL_RESULT_RENDERERS,
    useValue: renderer,
    multi: true
  })));
}

/**
 * The result of one tool call, which an application's renderer injects: which call it is and the
 * trusted presentation the application's presenter wrote on the server, in the reader's language.
 * The presentation follows the conversation, such as a live update of another participant.
 */
export abstract class NhAssistantToolResult {
  /** The tool id. */
  abstract readonly toolId: string;
  /** The version of the tool that ran. */
  abstract readonly toolVersion: number;
  /** The call. */
  abstract readonly invocationId: string;
  /** The tool's name as people read it. */
  abstract readonly displayName: Signal<string>;
  /** The presentation of what the call returned. */
  abstract readonly presentation: Signal<ToolResultPresentation>;
}
