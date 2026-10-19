import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import { McpConnection } from '../../models/assistant-admin.models';
import { NhAssistantIconComponent } from '../../internal/nh-assistant-icon.component';
import { NhAssistantTranslatePipe } from '../../internal/nh-assistant-translate.pipe';
import { NhAssistantApiService } from '../../services/nh-assistant-api.service';
import { NhAssistantError, NhAssistantStore } from '../../services/nh-assistant.store';
import { NhAssistantApiError, nhAssistantErrorMessageKey } from '../../services/nh-assistant-transport';

let nextId = 0;

/**
 * The caller's consent to background use of MCP servers with the caller's own account: agents may
 * then call such a server in runs without the caller, with a short-lived token of the caller. Each
 * switch applies at once and can be withdrawn at any time. Renders nothing while no server asks
 * for it, so most applications never show it.
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
  private readonly store = inject(NhAssistantStore, { optional: true });
  private loadSubscription?: Subscription;
  private updateSubscription?: Subscription;

  readonly id = `nh-assistant-mcp-connections-${nextId++}`;
  readonly connections = signal<McpConnection[]>([]);
  readonly loadFailed = signal(false);
  /** The server whose consent is being saved. */
  readonly busy = signal<string | null>(null);
  readonly error = signal<NhAssistantError | null>(null);
  readonly errorKeys = computed(() => {
    const error = this.error();
    return error ? [error.messageKey, nhAssistantErrorMessageKey(error.code), 'nh-assistant.errors.generic'] : [];
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.loadSubscription?.unsubscribe();
      this.updateSubscription?.unsubscribe();
    });
    this.load();
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
        this.error.set(error instanceof NhAssistantApiError
          ? { code: error.code, messageKey: error.messageKey }
          : { code: 'assistant-server', messageKey: nhAssistantErrorMessageKey('assistant-server') });
      }
    });
  }
}
