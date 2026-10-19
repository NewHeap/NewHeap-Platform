import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import { McpConnection } from '../../models/assistant-admin.models';
import { NhAssistantIconComponent } from '../../internal/nh-assistant-icon.component';
import { NhAssistantTranslatePipe } from '../../internal/nh-assistant-translate.pipe';
import { NhAssistantApiService } from '../../services/nh-assistant-api.service';
import { NhAssistantMcpConnectService } from '../../services/nh-assistant-mcp-connect.service';
import { NhAssistantError, NhAssistantStore } from '../../services/nh-assistant.store';
import { NhAssistantApiError, nhAssistantErrorMessageKey } from '../../services/nh-assistant-transport';

let nextId = 0;

/**
 * The caller's own MCP accounts: servers each person connects to with OAuth, with Connect,
 * Connect again and Disconnect, and the consent to background use of servers that run with the
 * caller's own account. Each change applies at once. Renders nothing while no server asks for it,
 * so most applications never show it.
 */
@Component({
  selector: 'nh-assistant-mcp-connections',
  standalone: true,
  imports: [DatePipe, TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-mcp-connections.component.html',
  styleUrl: './nh-assistant-mcp-connections.component.scss'
})
export class NhAssistantMcpConnectionsComponent {
  private readonly api = inject(NhAssistantApiService);
  private readonly connector = inject(NhAssistantMcpConnectService);
  private readonly store = inject(NhAssistantStore, { optional: true });
  private loadSubscription?: Subscription;
  private updateSubscription?: Subscription;

  readonly id = `nh-assistant-mcp-connections-${nextId++}`;
  readonly connections = signal<McpConnection[]>([]);
  readonly loadFailed = signal(false);
  /** The server whose consent or connection is being changed. */
  readonly busy = signal<string | null>(null);
  readonly confirmingDisconnect = signal<string | null>(null);
  /** The latest change to announce, per server. */
  readonly notice = signal<{ serverId: string; key: string } | null>(null);
  readonly error = signal<NhAssistantError | null>(null);
  readonly errorKeys = computed(() => {
    const error = this.error();
    return error ? [error.messageKey, nhAssistantErrorMessageKey(error.code), 'nh-assistant.errors.generic'] : [];
  });
  /** Whether any listed server is one the caller connects with OAuth. */
  readonly connectsAccounts = computed(() => this.connections().some(connection => connection.connectionStatus !== null));

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.loadSubscription?.unsubscribe();
      this.updateSubscription?.unsubscribe();
    });
    this.load();
    // A redirect-mode authorization that returned to this page.
    effect(() => {
      const returned = this.connector.returned();
      if (returned) {
        untracked(() => {
          this.connector.returned.set(null);
          if (returned.completion) {
            this.notice.set({ serverId: returned.completion.serverId, key: 'nh-assistant.preferences.mcp.connect-done' });
            this.load();
          } else {
            this.error.set({ code: returned.code, messageKey: nhAssistantErrorMessageKey(returned.code) });
          }
        });
      }
    });
  }

  load(): void {
    this.loadFailed.set(false);
    this.loadSubscription?.unsubscribe();
    this.loadSubscription = this.api.getMcpConnections().subscribe({
      next: connections => this.connections.set(connections),
      error: () => this.loadFailed.set(true)
    });
  }

  /** The agents' names as the chat shows them; an agent the chat does not list shows its id. */
  agentKeys(connection: McpConnection): string[] {
    const agents = this.store?.agents() ?? [];
    return connection.agentIds.map(agentId => agents.find(agent => agent.id === agentId)?.displayNameKey ?? agentId);
  }

  toggle(connection: McpConnection, event: Event): void {
    const allow = (event.target as HTMLInputElement).checked;
    this.busy.set(connection.serverId);
    this.error.set(null);
    this.notice.set(null);
    this.updateSubscription?.unsubscribe();
    this.updateSubscription = this.api.updateMcpConnection(connection.serverId, { allowBackgroundUse: allow }).subscribe({
      next: updated => {
        this.connections.update(items => items.map(item => item.serverId === updated.serverId ? updated : item));
        this.busy.set(null);
      },
      error: (error: unknown) => {
        // Show the stored decision again.
        this.connections.update(items => [...items]);
        (event.target as HTMLInputElement).checked = connection.allowBackgroundUse;
        this.busy.set(null);
        this.setError(error);
      }
    });
  }

  /** Connects the caller's own account; call from the click, so the popup is allowed. */
  connect(connection: McpConnection): void {
    this.busy.set(connection.serverId);
    this.error.set(null);
    this.notice.set(null);
    this.updateSubscription?.unsubscribe();
    this.updateSubscription = this.connector.connect(connection.serverId).subscribe({
      next: () => {
        this.busy.set(null);
        this.notice.set({ serverId: connection.serverId, key: 'nh-assistant.preferences.mcp.connect-done' });
        this.load();
      },
      error: (error: unknown) => {
        this.busy.set(null);
        this.setError(error);
      }
    });
  }

  disconnect(connection: McpConnection): void {
    this.confirmingDisconnect.set(null);
    this.busy.set(connection.serverId);
    this.error.set(null);
    this.notice.set(null);
    this.updateSubscription?.unsubscribe();
    this.updateSubscription = this.api.disconnectMcpConnection(connection.serverId).subscribe({
      complete: () => {
        this.busy.set(null);
        this.notice.set({ serverId: connection.serverId, key: 'nh-assistant.preferences.mcp.disconnect-done' });
        this.load();
      },
      error: (error: unknown) => {
        this.busy.set(null);
        this.setError(error);
      }
    });
  }

  private setError(error: unknown): void {
    this.error.set(error instanceof NhAssistantApiError
      ? { code: error.code, messageKey: error.messageKey }
      : { code: 'assistant-server', messageKey: nhAssistantErrorMessageKey('assistant-server') });
  }
}
