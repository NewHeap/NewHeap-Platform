import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import {
  McpServer,
  McpServerTestResult,
  NH_ASSISTANT_MCP_AUTHORIZATION_WINDOW,
  NhAssistantAdminApiService,
  NhAssistantError,
  nhAssistantConnectMcp,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  nhAssistantErrorMessageKey,
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
 * the server editor and the tool table of one server.
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
  private readonly authorizationWindow = inject(NH_ASSISTANT_MCP_AUTHORIZATION_WINDOW);
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
  }

  load(): void {
    this.loading.set(true);
    this.loadFailed.set(false);
    this.subscription?.unsubscribe();
    this.subscription = this.admin.getMcpServers().subscribe({
      next: servers => {
        this.servers.set(servers);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.loadFailed.set(true);
      }
    });
  }

  /** Reloads the list silently, for example after a sync changed the sync status. */
  refresh(): void {
    this.actions.add(this.admin.getMcpServers().subscribe({ next: servers => this.servers.set(servers), error: () => undefined }));
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
    return code ? [nhAssistantErrorMessageKey(code), 'nh-assistant.errors.generic'] : ['nh-assistant.errors.generic'];
  }

  /** Whether an administrator connects the organization's account to the server. */
  authorizesOrganization(server: McpServer): boolean {
    return server.authMode === 'oauth' && server.connectionScope === 'organization';
  }

  /**
   * Authorizes the organization's account in a window opened right away, so popup blockers allow
   * it, and completes the connection with this administrator's session.
   */
  connect(server: McpServer): void {
    const handle = this.authorizationWindow.open();
    this.error.set(null);
    this.notice.set(null);
    this.connecting.set(server.id);
    this.actions.add(nhAssistantConnectMcp(
      handle,
      this.admin.authorizeMcpOrganizationConnection(server.id),
      request => this.admin.completeMcpAuthorization(request)
    ).subscribe({
      next: () => {
        this.connecting.set(null);
        this.notice.set({ serverId: server.id, key: 'nh-assistant.admin.mcp.connect-done' });
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
