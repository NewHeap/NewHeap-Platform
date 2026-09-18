import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import {
  McpServer,
  McpTool,
  McpToolEffect,
  NhAssistantAdminApiService,
  NhAssistantError,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';

/** Unsaved changes of one tool row. */
interface ToolDraft {
  isEnabled: boolean;
  effect: McpToolEffect;
  descriptionOverride: string;
}

let nextId = 0;

/**
 * The tools of one MCP server. Every tool starts disabled as a change that needs approval;
 * the administrator enables it and chooses the effect. Remote annotations appear as hints
 * only, and a changed remote input schema is shown as a warning.
 */
@Component({
  selector: 'nh-assistant-admin-mcp-tools',
  standalone: true,
  imports: [TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-admin-mcp-tools.component.html',
  styleUrl: '../nh-assistant-admin-shared.scss'
})
export class NhAssistantAdminMcpToolsComponent {
  private readonly admin = inject(NhAssistantAdminApiService);
  private subscription?: Subscription;
  private rowSubscription?: Subscription;

  readonly server = input.required<McpServer>();
  readonly closed = output<void>();
  /** Emitted after a sync so the host can refresh the server's sync status. */
  readonly synced = output<void>();

  readonly id = `nh-assistant-mcp-tools-${nextId++}`;
  readonly effects: readonly McpToolEffect[] = ['mutation', 'read-only'];
  readonly loading = signal(true);
  readonly loadFailed = signal(false);
  readonly syncing = signal(false);
  readonly savingTool = signal<string | null>(null);
  readonly savedTool = signal<string | null>(null);
  readonly tools = signal<McpTool[]>([]);
  readonly drafts = signal<Record<string, ToolDraft>>({});
  readonly error = signal<NhAssistantError | null>(null);
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.subscription?.unsubscribe();
      this.rowSubscription?.unsubscribe();
    });

    effect(() => {
      const server = this.server();
      untracked(() => this.load(server.id));
    });
  }

  load(serverId = this.server().id): void {
    this.loading.set(true);
    this.loadFailed.set(false);
    this.subscription?.unsubscribe();
    this.subscription = this.admin.getMcpTools(serverId).subscribe({
      next: tools => {
        this.setTools(tools);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.loadFailed.set(true);
      }
    });
  }

  sync(): void {
    this.syncing.set(true);
    this.error.set(null);
    this.subscription?.unsubscribe();
    this.subscription = this.admin.syncMcpServer(this.server().id).subscribe({
      next: tools => {
        this.setTools(tools);
        this.syncing.set(false);
        this.synced.emit();
      },
      error: (error: unknown) => {
        this.syncing.set(false);
        this.error.set(toNhAssistantError(error));
        this.synced.emit();
      }
    });
  }

  draft(tool: McpTool): ToolDraft {
    return this.drafts()[tool.remoteName] ?? toDraft(tool);
  }

  isDirty(tool: McpTool): boolean {
    const draft = this.draft(tool);
    return draft.isEnabled !== tool.isEnabled || draft.effect !== tool.effect ||
      draft.descriptionOverride !== (tool.descriptionOverride ?? '');
  }

  change(tool: McpTool, patch: Partial<ToolDraft>): void {
    this.savedTool.set(null);
    this.drafts.update(drafts => ({ ...drafts, [tool.remoteName]: { ...this.draft(tool), ...patch } }));
  }

  value(event: Event): string {
    return (event.target as HTMLInputElement | HTMLSelectElement).value;
  }

  checked(event: Event): boolean {
    return (event.target as HTMLInputElement).checked;
  }

  save(tool: McpTool): void {
    const draft = this.draft(tool);
    const override = draft.descriptionOverride.trim();

    this.savingTool.set(tool.remoteName);
    this.savedTool.set(null);
    this.error.set(null);
    this.rowSubscription?.unsubscribe();
    this.rowSubscription = this.admin.updateMcpTool(this.server().id, tool.remoteName, {
      isEnabled: draft.isEnabled,
      effect: draft.effect,
      descriptionOverride: override.length > 0 ? override : null
    }).subscribe({
      next: updated => {
        this.tools.update(tools => tools.map(item => item.remoteName === updated.remoteName ? updated : item));
        this.drafts.update(drafts => {
          const { [updated.remoteName]: removed, ...rest } = drafts;
          return rest;
        });
        this.savingTool.set(null);
        this.savedTool.set(updated.remoteName);
      },
      error: (error: unknown) => {
        this.savingTool.set(null);
        this.error.set(toNhAssistantError(error));
      }
    });
  }

  private setTools(tools: McpTool[]): void {
    this.tools.set(tools);
    this.drafts.set({});
    this.savedTool.set(null);
  }
}

function toDraft(tool: McpTool): ToolDraft {
  return { isEnabled: tool.isEnabled, effect: tool.effect, descriptionOverride: tool.descriptionOverride ?? '' };
}
