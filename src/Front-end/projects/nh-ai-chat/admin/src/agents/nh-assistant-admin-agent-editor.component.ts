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
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
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

/** A text of an agent in code: its translation key and the translation the editor shows instead. */
interface KeyedText {
  key: string;
  shown: string;
}

/** How an agent created in the administration is used: chosen in the chat, working on its own, or both. */
export type AgentUse = 'chat' | 'background' | 'both';

let nextId = 0;

/**
 * Creates or edits one agent: how it is used (offered in the chat, working on its own, or both),
 * texts, autonomy, policy, tool selectors chosen from the tool catalog or typed as patterns, and
 * assigned MCP servers. A new agent meant to work on its own continues with `workRequested`. An
 * agent in code names and describes itself with translation keys: the editor shows their translation
 * and sends the key back unless the administrator changed the text, so the agent stays translated.
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
  private readonly translate = inject(TranslateService);
  private subscription?: Subscription;
  private keyedName: KeyedText | null = null;
  private keyedDescription: KeyedText | null = null;

  /** The agent to edit, or null for a new agent. */
  readonly agent = input<AdminAgent | null>(null);
  readonly tools = input<readonly ToolCatalogEntry[]>([]);
  readonly servers = input<readonly McpServer[]>([]);
  readonly saved = output<AdminAgent>();
  /**
   * A new agent meant to work on its own was created; the host continues with its run
   * configuration's guided setup. `saved` is not emitted for it.
   */
  readonly workRequested = output<AdminAgent>();
  readonly cancelled = output<void>();
  readonly uses: readonly AgentUse[] = ['chat', 'background', 'both'];

  readonly id = `nh-assistant-agent-editor-${nextId++}`;
  readonly max = NH_ASSISTANT_MAX_INSTRUCTIONS;
  readonly autonomyLevels: readonly AssistantAutonomy[] = ['observe', 'explain', 'propose', 'simulate', 'execute'];

  readonly agentId = signal('');
  readonly displayName = signal('');
  readonly description = signal('');
  readonly instructions = signal('');
  readonly autonomy = signal<AssistantAutonomy>('explain');
  /** How the agent is used: in the chat, on its own, or both. */
  readonly use = signal<AgentUse>('chat');
  readonly requiredPolicy = signal('');
  readonly toolSelectors = signal<string[]>([]);
  readonly mcpServerIds = signal<string[]>([]);
  readonly newSelector = signal('');
  readonly catalogFilter = signal('');
  readonly submitted = signal(false);
  readonly saving = signal(false);
  readonly error = signal<NhAssistantError | null>(null);

  readonly isNew = computed(() => this.agent() === null);
  /** Only agents created here choose whether they are offered in the chat; code decides for the others. */
  readonly choosesUse = computed(() => this.agent()?.source !== 'code');
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

  /** The first error the server reported for a field, as a translation key. */
  fieldError(field: string): string | null {
    const code = this.error()?.fieldErrors?.[field]?.[0];
    return code ? `nh-assistant.admin.run-config.field-errors.${code}` : null;
  }

  save(): void {
    this.submitted.set(true);
    if (!this.valid() || this.saving()) {
      return;
    }

    const policy = this.requiredPolicy().trim();
    const input: AdminAgentInput = {
      id: this.isNew() ? this.agentId() : this.agent()!.id,
      displayName: keyOrText(this.displayName(), this.keyedName),
      description: keyOrText(this.description(), this.keyedDescription),
      instructions: this.instructions(),
      toolSelectors: this.toolSelectors(),
      mcpServerIds: this.mcpServerIds(),
      requiredPolicy: policy.length > 0 ? policy : null,
      autonomy: this.autonomy(),
      ...(this.choosesUse() ? { offeredInChat: this.use() !== 'background' } : {})
    };
    const startsWork = this.isNew() && this.use() !== 'chat';

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
        if (startsWork) {
          this.workRequested.emit(agent);
        } else {
          this.saved.emit(agent);
        }
      },
      error: (error: unknown) => {
        this.saving.set(false);
        this.error.set(toNhAssistantError(error));
      }
    });
  }

  private reset(agent: AdminAgent | null): void {
    this.agentId.set(agent?.id ?? '');
    this.keyedName = agent?.source === 'code' ? this.keyed(agent.displayName) : null;
    this.keyedDescription = agent?.source === 'code' ? this.keyed(agent.description) : null;
    this.displayName.set(this.keyedName?.shown ?? agent?.displayName ?? '');
    this.description.set(this.keyedDescription?.shown ?? agent?.description ?? '');
    this.instructions.set(agent?.instructions ?? '');
    this.autonomy.set(agent?.autonomy ?? 'explain');
    this.use.set(agent?.offeredInChat === false ? 'background' : 'chat');
    this.requiredPolicy.set(agent?.requiredPolicy ?? '');
    this.toolSelectors.set([...(agent?.toolSelectors ?? [])]);
    this.mcpServerIds.set([...(agent?.mcpServerIds ?? [])]);
    this.submitted.set(false);
    this.error.set(null);
  }

  /** The translation of a key of an agent in code, or `null` when the text is not a key with a translation. */
  private keyed(text: string | null | undefined): KeyedText | null {
    if (!text) {
      return null;
    }
    const shown = this.translate.instant(text) as unknown;
    return typeof shown === 'string' && shown.length > 0 && shown !== text ? { key: text, shown } : null;
  }
}

/** The key of a text of an agent in code while the administrator kept its translation, otherwise the typed text. */
function keyOrText(value: string, keyed: KeyedText | null): string {
  const text = value.trim();
  return keyed !== null && text === keyed.shown.trim() ? keyed.key : text;
}
