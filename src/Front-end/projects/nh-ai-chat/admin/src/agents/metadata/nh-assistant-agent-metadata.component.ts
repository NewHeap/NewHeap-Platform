import { NgComponentOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  Injector,
  Type,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked
} from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Observable, Subscription } from 'rxjs';
import {
  AdminAgent,
  AgentMetadata,
  AgentMetadataEntry,
  NhAssistantAdminApiService,
  NhAssistantError,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';
import {
  NH_ASSISTANT_AGENT_METADATA_EDITORS,
  NhAssistantAgentMetadataSlot,
  NhAssistantAgentMetadataStatus
} from './nh-assistant-agent-metadata-slot';

let nextId = 0;

/**
 * The slot of one entry: its state as the server answered it, saved through the administration API
 * with the entry's version.
 */
class NhAssistantAgentMetadataSlotRef extends NhAssistantAgentMetadataSlot {
  private subscription?: Subscription;
  readonly entry;
  readonly saving = signal(false);
  readonly error = signal<NhAssistantError | null>(null);
  readonly status = signal<NhAssistantAgentMetadataStatus | null>(null);
  readonly readOnly = computed(() => !this.entry().editable);

  constructor(
    readonly agentId: string,
    entry: AgentMetadata,
    private readonly admin: NhAssistantAdminApiService,
    private readonly changed: (entry: AgentMetadata) => void) {
    super();
    this.entry = signal(entry);
  }

  save(value: unknown): void {
    if (this.readOnly() || this.saving()) {
      return;
    }

    const entry = this.entry();
    this.run(this.admin.saveAgentMetadata(this.agentId, entry.name, { value, expectedVersion: entry.version }), 'saved');
  }

  remove(): void {
    if (this.readOnly() || this.saving()) {
      return;
    }

    const entry = this.entry();
    this.run(this.admin.removeAgentMetadata(this.agentId, entry.name, entry.version), 'removed');
  }

  fieldErrors(field?: string): readonly string[] {
    return this.error()?.fieldErrors?.[field ? `value.${field}` : 'value'] ?? [];
  }

  /** The agent's next version after another entry of an agent created here changed. */
  followAgentVersion(agentVersion: number): void {
    this.entry.update(entry => ({ ...entry, version: agentVersion, agentVersion }));
  }

  dispose(): void {
    this.subscription?.unsubscribe();
  }

  private run(request: Observable<AgentMetadata>, status: NhAssistantAgentMetadataStatus): void {
    this.saving.set(true);
    this.error.set(null);
    this.status.set(null);
    this.subscription?.unsubscribe();
    this.subscription = request.subscribe({
      next: entry => {
        this.entry.set(entry);
        this.saving.set(false);
        this.status.set(status);
        this.changed(entry);
      },
      error: (error: unknown) => {
        this.saving.set(false);
        this.error.set(toNhAssistantError(error));
      }
    });
  }
}

/** One slot as the frame shows it. */
interface NhAssistantAgentMetadataSlotView {
  name: string;
  label: string | null;
  component: Type<unknown> | null;
  slot: NhAssistantAgentMetadataSlotRef;
  injector: Injector;
}

/**
 * The application fields of an agent in the agent editor (MD3): one slot per entry of agent
 * metadata the application registers with `AddAgentMetadata`, where the application's own editor,
 * registered with `provideNhAssistantAgentMetadataEditors`, shows the value and saves it through
 * `NhAssistantAgentMetadataSlot`. The frame names the entry, says where its value comes from, marks
 * an entry of an agent in code that only code changes, and shows what a save did. A change of an
 * agent created here is its next version: `agentVersionChanged` tells the agent editor. A new agent
 * shows its entries once it is saved.
 */
@Component({
  selector: 'nh-assistant-agent-metadata',
  standalone: true,
  imports: [NgComponentOutlet, TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-agent-metadata.component.html',
  styleUrl: '../../nh-assistant-admin-shared.scss',
  styles: [':host { display: block; } section { margin-top: 16px; }']
})
export class NhAssistantAgentMetadataComponent {
  private readonly admin = inject(NhAssistantAdminApiService);
  private readonly injector = inject(Injector);
  private readonly editors = inject(NH_ASSISTANT_AGENT_METADATA_EDITORS, { optional: true }) ?? [];
  private subscription?: Subscription;

  /** The agent whose fields are shown; `null` for a new agent, which is saved first. */
  readonly agent = input<AdminAgent | null>(null);
  /** The run catalog's `agentMetadata`: the entries a new agent will have. */
  readonly entries = input<readonly AgentMetadataEntry[]>([]);
  /** The agent's version after a field of an agent created here changed. */
  readonly agentVersionChanged = output<number>();

  readonly id = `nh-assistant-agent-metadata-${nextId++}`;
  readonly loading = signal(false);
  readonly loadFailed = signal(false);
  readonly slots = signal<readonly NhAssistantAgentMetadataSlotView[]>([]);
  readonly sourceKey = (entry: AgentMetadata): string =>
    `nh-assistant.admin.agent-editor.metadata.source.${entry.source ?? 'none'}`;
  readonly errorKeys = nhAssistantErrorKeys;

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.subscription?.unsubscribe();
      this.disposeSlots();
    });
    effect(() => {
      this.agent();
      untracked(() => this.load());
    });
  }

  load(): void {
    const agent = this.agent();
    this.subscription?.unsubscribe();
    this.disposeSlots();
    this.slots.set([]);
    this.loadFailed.set(false);
    if (!agent) {
      this.loading.set(false);
      return;
    }

    this.loading.set(true);
    this.subscription = this.admin.getAgentMetadata(agent.id).subscribe({
      next: entries => {
        this.slots.set(entries.map(entry => this.slotOf(agent, entry)));
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.loadFailed.set(true);
      }
    });
  }

  private slotOf(agent: AdminAgent, entry: AgentMetadata): NhAssistantAgentMetadataSlotView {
    const editor = this.editors.find(item => item.name === entry.name) ?? null;
    const slot = new NhAssistantAgentMetadataSlotRef(agent.id, entry, this.admin, changed => this.onChanged(agent, changed));
    return {
      name: entry.name,
      label: editor?.label ?? null,
      component: editor?.component ?? null,
      slot,
      injector: Injector.create({
        providers: [{ provide: NhAssistantAgentMetadataSlot, useValue: slot }],
        parent: this.injector
      })
    };
  }

  /**
   * A field of an agent created here made the agent's next version: the other fields and the agent
   * editor continue on it. An override of an agent in code has a version of its own.
   */
  private onChanged(agent: AdminAgent, changed: AgentMetadata): void {
    if (agent.source === 'code') {
      return;
    }

    for (const view of this.slots()) {
      if (view.name !== changed.name) {
        view.slot.followAgentVersion(changed.agentVersion);
      }
    }
    this.agentVersionChanged.emit(changed.agentVersion);
  }

  private disposeSlots(): void {
    for (const view of this.slots()) {
      view.slot.dispose();
    }
  }
}
