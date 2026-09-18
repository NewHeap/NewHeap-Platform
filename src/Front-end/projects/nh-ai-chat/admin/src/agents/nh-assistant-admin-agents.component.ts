import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Observable, Subscription, forkJoin } from 'rxjs';
import {
  AdminAgent,
  McpServer,
  NhAssistantAdminApiService,
  NhAssistantError,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  NhAssistantStore,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError,
  ToolCatalogEntry
} from '@newheap/platform-ai-chat';
import { NhAssistantAdminAgentEditorComponent } from './nh-assistant-admin-agent-editor.component';

let nextId = 0;

/**
 * Agent administration: code agents (override, disable, reset) and agents created here
 * (edit, disable, delete). Editing opens `nh-assistant-admin-agent-editor`.
 */
@Component({
  selector: 'nh-assistant-admin-agents',
  standalone: true,
  imports: [TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent, NhAssistantAdminAgentEditorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-admin-agents.component.html',
  styleUrl: '../nh-assistant-admin-shared.scss'
})
export class NhAssistantAdminAgentsComponent {
  private readonly admin = inject(NhAssistantAdminApiService);
  private readonly store = inject(NhAssistantStore);
  private subscription?: Subscription;
  private actionSubscription?: Subscription;

  readonly id = `nh-assistant-admin-agents-${nextId++}`;
  readonly loading = signal(true);
  readonly loadFailed = signal(false);
  readonly agents = signal<AdminAgent[]>([]);
  readonly tools = signal<ToolCatalogEntry[]>([]);
  readonly servers = signal<McpServer[]>([]);
  /** `undefined`: list; `null`: new agent; an agent: editing that agent. */
  readonly editing = signal<AdminAgent | null | undefined>(undefined);
  readonly confirming = signal<{ agentId: string; action: 'delete' | 'reset' } | null>(null);
  readonly busyAgentId = signal<string | null>(null);
  readonly error = signal<NhAssistantError | null>(null);
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.subscription?.unsubscribe();
      this.actionSubscription?.unsubscribe();
    });
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.loadFailed.set(false);
    this.subscription?.unsubscribe();
    this.subscription = forkJoin([this.admin.getAgents(), this.admin.getTools(), this.admin.getMcpServers()]).subscribe({
      next: ([agents, tools, servers]) => {
        this.agents.set(agents);
        this.tools.set(tools);
        this.servers.set(servers);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.loadFailed.set(true);
      }
    });
  }

  create(): void {
    this.error.set(null);
    this.editing.set(null);
  }

  edit(agent: AdminAgent): void {
    this.error.set(null);
    this.editing.set(agent);
  }

  onSaved(agent: AdminAgent): void {
    this.agents.update(agents => agents.some(item => item.id === agent.id)
      ? agents.map(item => item.id === agent.id ? agent : item)
      : [...agents, agent]);
    this.editing.set(undefined);
    this.refreshChat();
  }

  setEnabled(agent: AdminAgent, isEnabled: boolean): void {
    const { version, source, isOverridden, instructionsHash, updatedAt, ...input } = agent;
    this.run(agent.id, this.admin.updateAgent(agent.id, { ...input, isEnabled, expectedVersion: version }), updated => this.replace(updated));
  }

  confirm(agentId: string, action: 'delete' | 'reset'): void {
    this.confirming.set({ agentId, action });
  }

  isConfirming(agentId: string, action: 'delete' | 'reset'): boolean {
    const confirming = this.confirming();
    return confirming?.agentId === agentId && confirming.action === action;
  }

  reset(agent: AdminAgent): void {
    this.run(agent.id, this.admin.resetAgent(agent.id), updated => this.replace(updated));
  }

  remove(agent: AdminAgent): void {
    this.run<void>(agent.id, this.admin.deleteAgent(agent.id), () => {
      this.agents.update(agents => agents.filter(item => item.id !== agent.id));
    });
  }

  private run<T>(agentId: string, request: Observable<T>, done: (result: T) => void): void {
    this.busyAgentId.set(agentId);
    this.confirming.set(null);
    this.error.set(null);
    this.actionSubscription?.unsubscribe();
    this.actionSubscription = request.subscribe({
      next: result => done(result),
      complete: () => {
        this.busyAgentId.set(null);
        this.refreshChat();
      },
      error: (error: unknown) => {
        this.busyAgentId.set(null);
        this.error.set(toNhAssistantError(error));
      }
    });
  }

  private replace(agent: AdminAgent): void {
    this.agents.update(agents => agents.map(item => item.id === agent.id ? agent : item));
  }

  /** Agent changes affect the agent picker of the panel. */
  private refreshChat(): void {
    void this.store.reloadStatus();
  }
}
