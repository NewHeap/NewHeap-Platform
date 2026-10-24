import { EnvironmentProviders, InjectionToken, Signal, Type, makeEnvironmentProviders } from '@angular/core';
import { AgentMetadata, NhAssistantError } from '@newheap/platform-ai-chat';

/**
 * The application's editor of one entry of agent metadata (`AddAgentMetadata`): a standalone
 * component that the agent editor renders in the entry's slot. It injects
 * `NhAssistantAgentMetadataSlot` for the entry's value and saves the value with it; the library has no
 * generic form for metadata.
 */
export interface NhAssistantAgentMetadataEditor {
  /** The entry's name, as the application registers it with `AddAgentMetadata`. */
  name: string;
  /** The application's standalone component that shows and edits the value. */
  component: Type<unknown>;
  /** A translation key of the slot's title; the entry's name without one. */
  label?: string;
}

/** The editors the application registers for its entries of agent metadata, by entry name. */
export const NH_ASSISTANT_AGENT_METADATA_EDITORS = new InjectionToken<NhAssistantAgentMetadataEditor[]>(
  'NH_ASSISTANT_AGENT_METADATA_EDITORS');

/**
 * Registers the application's editors of agent metadata, where the administration is provided (in
 * the application's configuration or the providers of its administration route). The agent editor
 * renders one slot per registered entry; an entry without an editor shows that the application shows
 * none. Several calls add their editors.
 */
export function provideNhAssistantAgentMetadataEditors(...editors: NhAssistantAgentMetadataEditor[]): EnvironmentProviders {
  return makeEnvironmentProviders(editors.map(editor => ({
    provide: NH_ASSISTANT_AGENT_METADATA_EDITORS,
    useValue: editor,
    multi: true
  })));
}

/** What the last change of an entry did, for the slot's status line. */
export type NhAssistantAgentMetadataStatus = 'saved' | 'removed';

/**
 * The slot of one entry of agent metadata, which an application's editor injects: the entry's value
 * as the server answered it, whether it may change, and `save` and `remove`, which go through
 * `NhAssistantAdminApiService` with the entry's version and keep the slot up to date. A refusal shows
 * in `error`, with the validator's codes in `fieldErrors`.
 */
export abstract class NhAssistantAgentMetadataSlot {
  /** The agent's id. */
  abstract readonly agentId: string;
  /** The entry with its value in effect, where it comes from and its version. */
  abstract readonly entry: Signal<AgentMetadata>;
  /**
   * Whether the value is read-only here: an entry of an agent in code that is not editable, or one
   * whose value the agent's package locks until an administrator unlinks it.
   */
  abstract readonly readOnly: Signal<boolean>;
  /** Whether a change is being saved. */
  abstract readonly saving: Signal<boolean>;
  /** The refusal of the last change, or `null`. */
  abstract readonly error: Signal<NhAssistantError | null>;
  /** What the last change did, until the next one starts. */
  abstract readonly status: Signal<NhAssistantAgentMetadataStatus | null>;

  /**
   * Saves `value`, in the shape of the entry's type, with the entry's version: an agent created here
   * gets its next version, an agent in code an override. Ignored while read-only or saving.
   */
  abstract save(value: unknown): void;

  /** Removes the stored value: an agent in code uses its value in code again. */
  abstract remove(): void;

  /**
   * The codes the server reported for the whole value, or for one of its fields by camelCase path
   * such as `code`; empty without a refusal.
   */
  abstract fieldErrors(field?: string): readonly string[];
}
