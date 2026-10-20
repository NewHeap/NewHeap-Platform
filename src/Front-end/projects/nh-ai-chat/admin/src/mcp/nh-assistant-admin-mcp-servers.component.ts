import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import {
  McpConnectionsSummary,
  McpServer,
  McpServerTestResult,
  NhAssistantAdminApiService,
  NhAssistantError,
  NhAssistantMcpConnectService,
  ɵnhAssistantCodeKeys as nhAssistantCodeKeys,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵnhAssistantErrorOf as nhAssistantErrorOf,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';
import { NhAssistantAdminMcpServerEditorComponent } from './nh-assistant-admin-mcp-server-editor.component';
import { NhAssistantAdminMcpToolsComponent } from './nh-assistant-admin-mcp-tools.component';

/** Result of the latest connection test per server. */
interface TestState {
  running: boolean;
  result: McpServerTestResult | null;
}

let nextId = 0;

/**
 * MCP server administration: list, connection test, tool sync, the organization's OAuth connection,
 * the server editor and the tool table of one server. The personal connections are counted and
 * revoked only for servers each person connects to with OAuth; a count that could not be read shows
 * its translated code at the server.
 */
@Component({
  selector: 'nh-assistant-admin-mcp-servers',
  standalone: true,
  imports: [
    DatePipe,
    TranslatePipe,
    NhAssistantTranslatePipe,
    NhAssistantIconComponent,
    NhAssistantAdminMcpServerEditorComponent,
    NhAssistantAdminMcpToolsComponent
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-admin-mcp-servers.component.html',
  styleUrl: '../nh-assistant-admin-shared.scss'
})
export class NhAssistantAdminMcpServersComponent {
  private readonly admin = inject(NhAssistantAdminApiService);
  private readonly connector = inject(NhAssistantMcpConnectService);
  private subscription?: Subscription;
  private readonly actions = new Subscription();

  readonly id = `nh-assistant-admin-mcp-${nextId++}`;
  readonly loading = signal(true);
  readonly loadFailed = signal(false);
  readonly servers = signal<McpServer[]>([]);
  /** `undefined`: list; `null`: new server; a server: editing that server. */
  readonly editing = signal<McpServer | null | undefined>(undefined);
  readonly toolsOf = signal<McpServer | null>(null);
  readonly tests = signal<Record<string, TestState>>({});
  readonly confirmingDelete = signal<string | null>(null);
  /** The server whose organization connection is being authorized. */
  readonly connecting = signal<string | null>(null);
  readonly confirmingDisconnect = signal<string | null>(null);
  readonly confirmingRevokeAll = signal<string | null>(null);
  /** The personal connections per status of each server people connect to themselves. */
  readonly summaries = signal<Record<string, McpConnectionsSummary>>({});
  /** Why the personal connections of a server could not be counted, per server. */
  readonly summaryErrors = signal<Record<string, NhAssistantError>>({});
  /** The latest connection result to announce, per server. */
  readonly notice = signal<{ serverId: string; key: string } | null>(null);
  readonly error = signal<NhAssistantError | null>(null);
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.subscription?.unsubscribe();
      this.actions.unsubscribe();
    });
    this.load();
    // An authorization in redirect mode that returned to this page.
    effect(() => {
      const returned = this.connector.returned();
      if (returned) {
        untracked(() => {
          this.connector.returned.set(null);
          if (returned.completion) {
            this.notice.set({ serverId: returned.completion.serverId, key: 'nh-assistant.admin.mcp.connect-done' });
            this.refresh();
          } else {
            this.error.set(nhAssistantErrorOf(returned.code));
          }
        });
      }
    });
  }

  load(): void {
    this.loading.set(true);
    this.loadFailed.set(false);
    this.subscription?.unsubscribe();
    this.subscription = this.admin.getMcpServers().subscribe({
      next: servers => {
        this.servers.set(servers);
        this.loading.set(false);
        this.loadSummaries(servers);
      },
      error: () => {
        this.loading.set(false);
        this.loadFailed.set(true);
      }
    });
  }

  /** Reloads the list silently, for example after a sync changed the sync status. */
  refresh(): void {
    this.actions.add(this.admin.getMcpServers().subscribe({
      next: servers => {
        this.servers.set(servers);
        this.loadSummaries(servers);
      },
      error: () => undefined
    }));
  }

  create(): void {
    this.error.set(null);
    this.editing.set(null);
  }

  edit(server: McpServer): void {
    this.error.set(null);
    this.editing.set(server);
  }

  onSaved(server: McpServer): void {
    this.servers.update(servers => servers.some(item => item.id === server.id)
      ? servers.map(item => item.id === server.id ? server : item)
      : [...servers, server]);
    this.editing.set(undefined);
  }

  test(server: McpServer): void {
    this.setTest(server.id, { running: true, result: null });
    this.actions.add(this.admin.testMcpServer(server.id).subscribe({
      next: result => {
        this.setTest(server.id, { running: false, result });
        // The test records the connection state of the server.
        this.refresh();
      },
      error: (error: unknown) => {
        const failure = toNhAssistantError(error);
        this.setTest(server.id, { running: false, result: { ok: false, code: failure.code, toolCount: null } });
      }
    }));
  }

  testState(serverId: string): TestState | null {
    return this.tests()[serverId] ?? null;
  }

  testErrorKeys(code: string | null): string[] {
    return code ? nhAssistantCodeKeys(code) : ['nh-assistant.errors.generic'];
  }

  /** The text of a server's failed count of personal connections. */
  summaryErrorKeys(serverId: string): string[] {
    return nhAssistantErrorKeys(this.summaryErrors()[serverId] ?? null);
  }

  /** Whether an administrator connects the organization's account to the server. */
  authorizesOrganization(server: McpServer): boolean {
    return server.authMode === 'oauth' && server.connectionScope === 'organization';
  }

  /** Whether each person connects their own account to the server. */
  connectsPeople(server: McpServer): boolean {
    return server.authMode === 'oauth' && server.connectionScope === 'user';
  }

  summary(server: McpServer): McpConnectionsSummary | null {
    return this.summaries()[server.id] ?? null;
  }

  /** Revokes every person's connection of the server; each person connects again. */
  revokeAll(server: McpServer): void {
    this.confirmingRevokeAll.set(null);
    this.error.set(null);
    this.notice.set(null);
    this.actions.add(this.admin.revokeMcpConnections(server.id).subscribe({
      complete: () => {
        this.notice.set({ serverId: server.id, key: 'nh-assistant.admin.mcp.revoke-all-done' });
        this.loadSummaries([server]);
      },
      error: (error: unknown) => this.error.set(toNhAssistantError(error))
    }));
  }

  /** Counts the personal connections of the servers each person connects to with OAuth; only those have them. */
  private loadSummaries(servers: readonly McpServer[]): void {
    for (const server of servers.filter(item => this.connectsPeople(item))) {
      this.actions.add(this.admin.getMcpConnectionsSummary(server.id).subscribe({
        next: summary => {
          this.summaries.update(summaries => ({ ...summaries, [server.id]: summary }));
          this.summaryErrors.update(({ [server.id]: _, ...others }) => others);
        },
        error: (error: unknown) => {
          this.summaryErrors.update(errors => ({ ...errors, [server.id]: toNhAssistantError(error) }));
        }
      }));
    }
  }

  /** Counts the personal connections of one server again. */
  retrySummary(server: McpServer): void {
    this.loadSummaries([server]);
  }

  /**
   * Authorizes the organization's account, or for a server people connect to themselves the
   * administrator's own account, in a window opened right away, so popup blockers allow it, and
   * completes the connection with this administrator's session.
   */
  connect(server: McpServer): void {
    this.error.set(null);
    this.notice.set(null);
    this.connecting.set(server.id);
    this.actions.add(this.connector.run(request => this.admin.authorizeMcpOrganizationConnection(server.id, request), server.id).subscribe({
      next: () => {
        this.connecting.set(null);
        this.notice.set({
          serverId: server.id,
          key: this.connectsPeople(server) ? 'nh-assistant.admin.mcp.connect-own-done' : 'nh-assistant.admin.mcp.connect-done'
        });
        this.refresh();
      },
      error: (error: unknown) => {
        this.connecting.set(null);
        this.error.set(toNhAssistantError(error));
      }
    }));
  }

  /** Removes the organization connection; the server revokes its tokens. */
  disconnect(server: McpServer): void {
    this.confirmingDisconnect.set(null);
    this.error.set(null);
    this.notice.set(null);
    this.actions.add(this.admin.disconnectMcpOrganizationConnection(server.id).subscribe({
      complete: () => {
        this.notice.set({ serverId: server.id, key: 'nh-assistant.admin.mcp.disconnect-done' });
        this.refresh();
      },
      error: (error: unknown) => this.error.set(toNhAssistantError(error))
    }));
  }

  remove(server: McpServer): void {
    this.confirmingDelete.set(null);
    this.error.set(null);
    this.actions.add(this.admin.deleteMcpServer(server.id).subscribe({
      complete: () => this.servers.update(servers => servers.filter(item => item.id !== server.id)),
      error: (error: unknown) => this.error.set(toNhAssistantError(error))
    }));
  }

  private setTest(serverId: string, state: TestState): void {
    this.tests.update(tests => ({ ...tests, [serverId]: state }));
  }
}
