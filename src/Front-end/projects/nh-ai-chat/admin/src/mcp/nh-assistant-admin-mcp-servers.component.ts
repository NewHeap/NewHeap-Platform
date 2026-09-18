import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import {
  McpServer,
  McpServerTestResult,
  NhAssistantAdminApiService,
  NhAssistantError,
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
 * MCP server administration: list, connection test, tool sync, the server editor and the
 * tool table of one server.
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
      next: result => this.setTest(server.id, { running: false, result }),
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
