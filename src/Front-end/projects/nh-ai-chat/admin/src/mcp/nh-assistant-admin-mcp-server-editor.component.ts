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
  McpHeadlessUse,
  McpServer,
  McpServerInput,
  McpTransport,
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
 * new value and `clear` sends `""`. The stored value is never shown. The editor offers no `clear`:
 * the API refuses a mode with a secret that has none, so a stored secret goes by choosing a reference
 * or a mode without a secret.
 */
export type NhAssistantSecretAction = 'keep' | 'replace' | 'clear';

/** Where the secret is: stored here, encrypted, or a reference to a secret of the application. */
export type NhAssistantSecretSource = 'stored' | 'reference';

/** `<prefix>:<name>` as the server accepts it; the name never climbs a path. */
const secretReferencePattern = /^[a-z][a-z0-9-]{0,31}:[A-Za-z0-9][A-Za-z0-9._/-]{0,199}$/;
const certificateNamePattern = /^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$/;

let nextId = 0;

/**
 * Creates or edits one MCP server: the authentication with a write-only secret or a reference to
 * one, an optional client certificate, the transport and the use in background runs.
 */
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
  readonly authModes: readonly McpAuthMode[] = ['none', 'bearer', 'api-key', 'basic', 'forward-user-token'];
  readonly transports: readonly McpTransport[] = ['streamable-http', 'auto'];
  readonly secretSources: readonly NhAssistantSecretSource[] = ['stored', 'reference'];

  readonly serverId = signal('');
  readonly displayName = signal('');
  readonly url = signal('');
  readonly authMode = signal<McpAuthMode>('none');
  readonly headerName = signal('');
  readonly requiredPolicy = signal('');
  readonly isEnabled = signal(true);
  readonly headlessUse = signal<McpHeadlessUse>('never');
  readonly userName = signal('');
  readonly secretSource = signal<NhAssistantSecretSource>('stored');
  readonly secretReference = signal('');
  readonly clientCertificateReference = signal('');
  readonly transport = signal<McpTransport>('streamable-http');
  readonly secretAction = signal<NhAssistantSecretAction>('keep');
  /** The new secret while typing. Never filled from the server. */
  readonly secret = signal('');
  readonly submitted = signal(false);
  readonly saving = signal(false);
  readonly error = signal<NhAssistantError | null>(null);

  readonly isNew = computed(() => this.server() === null);
  /** A stored secret that a save keeps; a reference replaces it. */
  readonly hasSecret = computed(() => this.server()?.hasSecret === true);
  readonly usesSecret = computed(() => this.authMode() === 'bearer' || this.authMode() === 'api-key' || this.authMode() === 'basic');
  readonly usesReference = computed(() => this.usesSecret() && this.secretSource() === 'reference');
  /**
   * The organization's credential may serve background runs; a forwarded user token only as the
   * short-lived token of the person a run acts for, after that person allowed it.
   */
  readonly headlessUses = computed<readonly McpHeadlessUse[]>(() =>
    this.authMode() === 'forward-user-token' ? ['never', 'personal'] : ['never', 'organization']);
  readonly effectiveHeadlessUse = computed(() => this.headlessUses().includes(this.headlessUse()) ? this.headlessUse() : 'never');
  readonly showSecretInput = computed(() => this.usesSecret() && !this.usesReference() && (!this.hasSecret() || this.secretAction() === 'replace'));
  readonly idInvalid = computed(() => this.isNew() && (this.serverId().length > 40 || !NH_ASSISTANT_DASH_CASE.test(this.serverId())));
  readonly nameMissing = computed(() => this.displayName().trim().length === 0);
  readonly urlInvalid = computed(() => !isAllowedUrl(this.url().trim()));
  readonly userNameInvalid = computed(() => this.authMode() === 'basic' &&
    (this.userName().trim().length === 0 || this.userName().length > 256 || this.userName().includes(':')));
  readonly secretReferenceInvalid = computed(() => this.usesReference() && !isSecretReference(this.secretReference().trim()));
  readonly certificateInvalid = computed(() => {
    const name = this.clientCertificateReference().trim();
    return name.length > 0 && !certificateNamePattern.test(name);
  });
  readonly valid = computed(() => !this.idInvalid() && !this.nameMissing() && !this.urlInvalid() &&
    !this.userNameInvalid() && !this.secretReferenceInvalid() && !this.certificateInvalid());
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

  setSecretSource(source: NhAssistantSecretSource): void {
    this.secretSource.set(source);
    this.secret.set('');
    this.secretAction.set('keep');
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
      isEnabled: this.isEnabled(),
      headlessUse: this.effectiveHeadlessUse(),
      transport: this.transport(),
      // "" clears the certificate; the server ignores the user name outside basic authentication.
      clientCertificateReference: this.clientCertificateReference().trim(),
      userName: authMode === 'basic' ? this.userName().trim() : null
    };

    // Write-only secret: omitted keeps the stored value, "" clears it. A reference and a stored
    // secret exclude each other: sending one replaces the other.
    const action = this.secretAction();
    const storedReference = this.server()?.secretReference ?? null;
    if (this.usesReference()) {
      input.secretReference = this.secretReference().trim();
    } else if (!this.usesSecret()) {
      if (this.hasSecret()) {
        input.secret = '';
      }
      if (storedReference !== null) {
        input.secretReference = '';
      }
    } else {
      if (storedReference !== null) {
        input.secretReference = '';
      }
      if (action === 'clear') {
        input.secret = '';
      } else if (action === 'replace' && this.secret().length > 0) {
        input.secret = this.secret();
      }
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
    this.headlessUse.set(server?.headlessUse ?? 'never');
    this.userName.set(server?.userName ?? '');
    this.secretSource.set(server?.secretReference ? 'reference' : 'stored');
    this.secretReference.set(server?.secretReference ?? '');
    this.clientCertificateReference.set(server?.clientCertificateReference ?? '');
    this.transport.set(server?.transport ?? 'streamable-http');
    this.secretAction.set('keep');
    this.secret.set('');
    this.submitted.set(false);
    this.error.set(null);
  }
}

function isSecretReference(value: string): boolean {
  return secretReferencePattern.test(value) && !value.includes('..');
}

function isAllowedUrl(value: string): boolean {
  try {
    const url = new URL(value);
    return url.protocol === 'https:' || (url.protocol === 'http:' && url.hostname === 'localhost');
  } catch {
    return false;
  }
}
