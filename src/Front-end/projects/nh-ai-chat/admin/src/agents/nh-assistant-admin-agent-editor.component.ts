import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked
} from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import {
  AdminAgent,
  AdminAgentInput,
  AssistantAutonomy,
  McpServer,
  ɵNH_ASSISTANT_DASH_CASE as NH_ASSISTANT_DASH_CASE,
  NhAssistantAdminApiService,
  NhAssistantError,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError,
  ToolCatalogEntry
} from '@newheap/platform-ai-chat';
import { NH_ASSISTANT_MAX_INSTRUCTIONS } from '../context/nh-assistant-admin-context.component';

/** True when a tool id matches a selector glob (`*` matches any characters). */
export function nhAssistantSelectorMatches(toolId: string, selector: string): boolean {
  const pattern = selector.split('*').map(part => part.replace(/[.+?^${}()|[\]\\]/g, '\\$&')).join('.*');
  return new RegExp(`^${pattern}$`).test(toolId);
}

const selectorPattern = /^[a-z0-9*][a-z0-9.*_-]*$/i;

let nextId = 0;

/**
 * Creates or edits one agent: texts, autonomy, policy, tool selectors chosen from the tool
 * catalog or typed as patterns, and assigned MCP servers.
 */
@Component({
  selector: 'nh-assistant-admin-agent-editor',
  standalone: true,
  imports: [TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-admin-agent-editor.component.html',
  styleUrl: '../nh-assistant-admin-shared.scss'
})
export class NhAssistantAdminAgentEditorComponent {
  private readonly admin = inject(NhAssistantAdminApiService);
  private subscription?: Subscription;

  /** The agent to edit, or null for a new agent. */
  readonly agent = input<AdminAgent | null>(null);
  readonly tools = input<readonly ToolCatalogEntry[]>([]);
  readonly servers = input<readonly McpServer[]>([]);
  readonly saved = output<AdminAgent>();
  readonly cancelled = output<void>();

  readonly id = `nh-assistant-agent-editor-${nextId++}`;
  readonly max = NH_ASSISTANT_MAX_INSTRUCTIONS;
  readonly autonomyLevels: readonly AssistantAutonomy[] = ['observe', 'explain', 'propose', 'simulate', 'execute'];

  readonly agentId = signal('');
  readonly displayName = signal('');
  readonly description = signal('');
  readonly instructions = signal('');
  readonly autonomy = signal<AssistantAutonomy>('explain');
  readonly requiredPolicy = signal('');
  readonly isEnabled = signal(true);
  readonly toolSelectors = signal<string[]>([]);
  readonly mcpServerIds = signal<string[]>([]);
  readonly newSelector = signal('');
  readonly catalogFilter = signal('');
  readonly submitted = signal(false);
  readonly saving = signal(false);
  readonly error = signal<NhAssistantError | null>(null);

  readonly isNew = computed(() => this.agent() === null);
  readonly idInvalid = computed(() => this.isNew() && !NH_ASSISTANT_DASH_CASE.test(this.agentId()));
  readonly nameMissing = computed(() => this.displayName().trim().length === 0);
  readonly tooLong = computed(() => this.instructions().length > this.max);
  readonly selectorInvalid = computed(() => this.newSelector().trim().length > 0 && !selectorPattern.test(this.newSelector().trim()));
  readonly valid = computed(() => !this.idInvalid() && !this.nameMissing() && !this.tooLong());
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));
  readonly filteredTools = computed(() => {
    const filter = this.catalogFilter().trim().toLowerCase();
    return this.tools().filter(tool => !filter ||
      tool.id.toLowerCase().includes(filter) || tool.description.toLowerCase().includes(filter));
  });
  readonly matchCount = computed(() => this.tools()
    .filter(tool => this.toolSelectors().some(selector => nhAssistantSelectorMatches(tool.id, selector))).length);

  constructor() {
    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());

    effect(() => {
      const agent = this.agent();
      untracked(() => this.reset(agent));
    });
  }

  isSelected(toolId: string): boolean {
    return this.toolSelectors().includes(toolId);
  }

  isCovered(toolId: string): boolean {
    return this.toolSelectors().some(selector => selector !== toolId && nhAssistantSelectorMatches(toolId, selector));
  }

  toggleTool(toolId: string, checked: boolean): void {
    this.toolSelectors.update(selectors => checked
      ? [...selectors.filter(selector => selector !== toolId), toolId]
      : selectors.filter(selector => selector !== toolId));
  }

  addSelector(): void {
    const selector = this.newSelector().trim();
    if (selector.length === 0 || this.selectorInvalid()) {
      return;
    }

    this.toolSelectors.update(selectors => selectors.includes(selector) ? selectors : [...selectors, selector]);
    this.newSelector.set('');
  }

  onSelectorKeydown(event: KeyboardEvent): void {
    if (event.key === 'Enter') {
      event.preventDefault();
      this.addSelector();
    }
  }

  removeSelector(selector: string): void {
    this.toolSelectors.update(selectors => selectors.filter(item => item !== selector));
  }

  toggleServer(serverId: string, checked: boolean): void {
    this.mcpServerIds.update(ids => checked ? [...ids.filter(id => id !== serverId), serverId] : ids.filter(id => id !== serverId));
  }

  value(event: Event): string {
    return (event.target as HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement).value;
  }

  checked(event: Event): boolean {
    return (event.target as HTMLInputElement).checked;
  }

  save(): void {
    this.submitted.set(true);
    if (!this.valid() || this.saving()) {
      return;
    }

    const policy = this.requiredPolicy().trim();
    const input: AdminAgentInput = {
      id: this.isNew() ? this.agentId() : this.agent()!.id,
      displayName: this.displayName().trim(),
      description: this.description().trim(),
      instructions: this.instructions(),
      toolSelectors: this.toolSelectors(),
      mcpServerIds: this.mcpServerIds(),
      requiredPolicy: policy.length > 0 ? policy : null,
      autonomy: this.autonomy(),
      isEnabled: this.isEnabled()
    };

    const existing = this.agent();
    const request = existing
      ? this.admin.updateAgent(existing.id, { ...input, expectedVersion: existing.version })
      : this.admin.createAgent(input);

    this.saving.set(true);
    this.error.set(null);
    this.subscription?.unsubscribe();
    this.subscription = request.subscribe({
      next: agent => {
        this.saving.set(false);
        this.saved.emit(agent);
      },
      error: (error: unknown) => {
        this.saving.set(false);
        this.error.set(toNhAssistantError(error));
      }
    });
  }

  private reset(agent: AdminAgent | null): void {
    this.agentId.set(agent?.id ?? '');
    this.displayName.set(agent?.displayName ?? '');
    this.description.set(agent?.description ?? '');
    this.instructions.set(agent?.instructions ?? '');
    this.autonomy.set(agent?.autonomy ?? 'explain');
    this.requiredPolicy.set(agent?.requiredPolicy ?? '');
    this.isEnabled.set(agent?.isEnabled ?? true);
    this.toolSelectors.set([...(agent?.toolSelectors ?? [])]);
    this.mcpServerIds.set([...(agent?.mcpServerIds ?? [])]);
    this.submitted.set(false);
    this.error.set(null);
  }
}
