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
  McpAuthMode,
  McpServer,
  McpServerInput,
  ɵNH_ASSISTANT_DASH_CASE as NH_ASSISTANT_DASH_CASE,
  NhAssistantAdminApiService,
  NhAssistantError,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';

/**
 * What happens to the stored secret on save. `keep` omits `secret`, `replace` sends the
 * new value and `clear` sends `""`. The stored value is never shown.
 */
export type NhAssistantSecretAction = 'keep' | 'replace' | 'clear';

let nextId = 0;

/** Creates or edits one MCP server with a write-only secret. */
@Component({
  selector: 'nh-assistant-admin-mcp-server-editor',
  standalone: true,
  imports: [TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-admin-mcp-server-editor.component.html',
  styleUrl: '../nh-assistant-admin-shared.scss'
})
export class NhAssistantAdminMcpServerEditorComponent {
  private readonly admin = inject(NhAssistantAdminApiService);
  private subscription?: Subscription;

  /** The server to edit, or null for a new server. */
  readonly server = input<McpServer | null>(null);
  readonly saved = output<McpServer>();
  readonly cancelled = output<void>();

  readonly id = `nh-assistant-server-editor-${nextId++}`;
  readonly authModes: readonly McpAuthMode[] = ['none', 'bearer', 'api-key', 'forward-user-token'];

  readonly serverId = signal('');
  readonly displayName = signal('');
  readonly url = signal('');
  readonly authMode = signal<McpAuthMode>('none');
  readonly headerName = signal('');
  readonly requiredPolicy = signal('');
  readonly isEnabled = signal(true);
  readonly secretAction = signal<NhAssistantSecretAction>('keep');
  /** The new secret while typing. Never filled from the server. */
  readonly secret = signal('');
  readonly submitted = signal(false);
  readonly saving = signal(false);
  readonly error = signal<NhAssistantError | null>(null);

  readonly isNew = computed(() => this.server() === null);
  readonly hasSecret = computed(() => this.server()?.hasSecret === true);
  readonly usesSecret = computed(() => this.authMode() === 'bearer' || this.authMode() === 'api-key');
  readonly showSecretInput = computed(() => this.usesSecret() && (!this.hasSecret() || this.secretAction() === 'replace'));
  readonly idInvalid = computed(() => this.isNew() && (this.serverId().length > 40 || !NH_ASSISTANT_DASH_CASE.test(this.serverId())));
  readonly nameMissing = computed(() => this.displayName().trim().length === 0);
  readonly urlInvalid = computed(() => !isAllowedUrl(this.url().trim()));
  readonly valid = computed(() => !this.idInvalid() && !this.nameMissing() && !this.urlInvalid());
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));

  constructor() {
    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());

    effect(() => {
      const server = this.server();
      untracked(() => this.reset(server));
    });
  }

  value(event: Event): string {
    return (event.target as HTMLInputElement | HTMLSelectElement).value;
  }

  checked(event: Event): boolean {
    return (event.target as HTMLInputElement).checked;
  }

  setSecretAction(action: NhAssistantSecretAction): void {
    this.secretAction.set(action);
    this.secret.set('');
  }

  onSecretInput(event: Event): void {
    this.secret.set(this.value(event));
    if (!this.hasSecret()) {
      this.secretAction.set(this.secret().length > 0 ? 'replace' : 'keep');
    }
  }

  save(): void {
    this.submitted.set(true);
    if (!this.valid() || this.saving()) {
      return;
    }

    const authMode = this.authMode();
    const policy = this.requiredPolicy().trim();
    const header = this.headerName().trim();
    const input: McpServerInput = {
      id: this.isNew() ? this.serverId() : this.server()!.id,
      displayName: this.displayName().trim(),
      url: this.url().trim(),
      authMode,
      headerName: authMode === 'api-key' && header.length > 0 ? header : null,
      requiredPolicy: policy.length > 0 ? policy : null,
      isEnabled: this.isEnabled()
    };

    // Write-only secret: omitted keeps the stored value, "" clears it.
    const action = this.secretAction();
    if (!this.usesSecret() && this.hasSecret()) {
      input.secret = '';
    } else if (action === 'clear') {
      input.secret = '';
    } else if (action === 'replace' && this.secret().length > 0) {
      input.secret = this.secret();
    }

    const existing = this.server();
    const request = existing ? this.admin.updateMcpServer(existing.id, input) : this.admin.createMcpServer(input);

    this.saving.set(true);
    this.error.set(null);
    this.subscription?.unsubscribe();
    this.subscription = request.subscribe({
      next: server => {
        this.saving.set(false);
        this.secret.set('');
        this.saved.emit(server);
      },
      error: (error: unknown) => {
        this.saving.set(false);
        this.error.set(toNhAssistantError(error));
      }
    });
  }

  private reset(server: McpServer | null): void {
    this.serverId.set(server?.id ?? '');
    this.displayName.set(server?.displayName ?? '');
    this.url.set(server?.url ?? 'https://');
    this.authMode.set(server?.authMode ?? 'none');
    this.headerName.set(server?.headerName ?? '');
    this.requiredPolicy.set(server?.requiredPolicy ?? '');
    this.isEnabled.set(server?.isEnabled ?? true);
    this.secretAction.set('keep');
    this.secret.set('');
    this.submitted.set(false);
    this.error.set(null);
  }
}

function isAllowedUrl(value: string): boolean {
  try {
    const url = new URL(value);
    return url.protocol === 'https:' || (url.protocol === 'http:' && url.hostname === 'localhost');
  } catch {
    return false;
  }
}
