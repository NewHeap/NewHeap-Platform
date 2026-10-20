import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
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
  McpBuiltInAuthMode,
  McpConnectionScope,
  McpCustomAuthMode,
  McpDiscovery,
  McpHeadlessUse,
  McpOAuthInput,
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
import { focusFirstInvalid } from '../agents/run-config/run-config-shared';

/**
 * The longest values the server accepts per field of a server (`POST`/`PUT admin/mcp-servers`); the
 * editor's fields stop there.
 */
const NH_ASSISTANT_MCP_SERVER_LIMITS = {
  id: 40,
  displayName: 200,
  url: 2048,
  headerName: 128,
  secret: 2000,
  secretReference: 233,
  userName: 256,
  clientCertificateReference: 100,
  requiredPolicy: 256,
  scopes: 1024,
  clientId: 512,
  clientSecret: 2000,
  organizationAccountLabel: 200
} as const;

/** The fields of a server the server reports errors for, as the editor shows them. */
const serverFields = [
  'id',
  'displayName',
  'url',
  'authMode',
  'headerName',
  'secret',
  'secretReference',
  'userName',
  'clientCertificateReference',
  'transport',
  'connectionScope',
  'oauth.scopes',
  'oauth.clientId',
  'oauth.clientSecret',
  'oauth.clientSecretReference',
  'oauth.organizationAccountLabel',
  'oauth.confirmAuthorization',
  'requiredPolicy',
  'headlessUse'
] as const;

/** A header name the server accepts: 1 to 128 letters, digits and dashes. */
const headerNamePattern = /^[A-Za-z0-9-]{1,128}$/;

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
/** Space-separated scope tokens: printable ASCII without quotes or backslashes (RFC 6749). */
const scopesPattern = /^[\x21\x23-\x5B\x5D-\x7E]+( [\x21\x23-\x5B\x5D-\x7E]+)*$/;

let nextId = 0;

/**
 * Creates or edits one MCP server: the authentication with a write-only secret or a reference to
 * one, OAuth with discovery the administrator confirms, an optional client certificate, the
 * transport and the use in background runs. Every field stops at the length the server accepts; a
 * refused save shows each of the server's field errors at its field, for example a secret reference
 * the application cannot resolve (`not-found`), and moves the focus to the first of them.
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
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);
  private subscription?: Subscription;
  private discoverySubscription?: Subscription;
  private modesSubscription?: Subscription;
  /** Fields whose server error the administrator changed since the refused save. */
  private readonly touched = signal<ReadonlySet<string>>(new Set());

  /** The server to edit, or null for a new server. */
  readonly server = input<McpServer | null>(null);
  readonly saved = output<McpServer>();
  readonly cancelled = output<void>();

  readonly id = `nh-assistant-server-editor-${nextId++}`;
  readonly limits = NH_ASSISTANT_MCP_SERVER_LIMITS;
  readonly builtInAuthModes: readonly McpBuiltInAuthMode[] = ['none', 'bearer', 'api-key', 'basic', 'forward-user-token', 'oauth', 'oauth-client-credentials'];
  /** The modes the application registered in code; none until they are loaded or when loading fails. */
  readonly customAuthModes = signal<readonly McpCustomAuthMode[]>([]);
  /** The built-in modes, the application's own and, kept selectable, a stored mode no longer registered. */
  readonly authModes = computed<readonly McpAuthMode[]>(() => {
    const modes: McpAuthMode[] = [...this.builtInAuthModes, ...this.customAuthModes().map(item => item.mode)];
    const stored = this.server()?.authMode;
    return stored && !modes.includes(stored) ? [...modes, stored] : modes;
  });
  readonly transports: readonly McpTransport[] = ['streamable-http', 'auto'];
  readonly secretSources: readonly NhAssistantSecretSource[] = ['stored', 'reference'];
  readonly connectionScopes: readonly McpConnectionScope[] = ['organization', 'user'];

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
  readonly connectionScope = signal<McpConnectionScope>('organization');
  readonly oauthClientId = signal('');
  readonly oauthScopes = signal('');
  readonly organizationAccountLabel = signal('');
  readonly oauthSecretSource = signal<NhAssistantSecretSource>('stored');
  readonly oauthSecretReference = signal('');
  readonly oauthSecretAction = signal<NhAssistantSecretAction>('keep');
  /** The new client secret while typing. Never filled from the server. */
  readonly oauthSecret = signal('');
  /** What discovery found for the address; the administrator confirms it on save. */
  readonly discovery = signal<McpDiscovery | null>(null);
  readonly discovering = signal(false);
  readonly discoveryError = signal<NhAssistantError | null>(null);
  readonly confirmAuthorization = signal(false);
  readonly submitted = signal(false);
  readonly saving = signal(false);
  readonly error = signal<NhAssistantError | null>(null);

  readonly isNew = computed(() => this.server() === null);
  /** A stored secret that a save keeps; a reference replaces it. */
  readonly hasSecret = computed(() => this.server()?.hasSecret === true);
  /** The server's own secret; OAuth modes have a client secret instead. */
  readonly usesSecret = computed(() => this.authMode() === 'bearer' || this.authMode() === 'api-key' || this.authMode() === 'basic');
  readonly usesReference = computed(() => this.usesSecret() && this.secretSource() === 'reference');
  /**
   * The organization's credential may serve background runs; a forwarded user token only as the
   * short-lived token of the person a run acts for, after that person allowed it.
   */
  readonly headlessUses = computed<readonly McpHeadlessUse[]>(() =>
    this.personalCredential() ? ['never', 'personal'] : ['never', 'organization']);
  readonly isOAuth = computed(() => this.authMode() === 'oauth' || this.authMode() === 'oauth-client-credentials');
  readonly isClientCredentials = computed(() => this.authMode() === 'oauth-client-credentials');
  /** One authorization of the organization's account that everyone uses. */
  readonly isOrganizationOAuth = computed(() => this.authMode() === 'oauth' && this.connectionScope() === 'organization');
  /** Whose credential the application's own mode sends; null for a built-in mode. */
  readonly customOwnership = computed(() =>
    this.customAuthModes().find(item => item.mode === this.authMode())?.ownership ?? null);
  /**
   * The server runs with each person's own account: a forwarded sign-in, a personal OAuth connection
   * or a mode of the application that sends a person's credential.
   */
  readonly personalCredential = computed(() =>
    this.authMode() === 'forward-user-token' || (this.authMode() === 'oauth' && this.connectionScope() === 'user') ||
    this.customOwnership() === 'person');
  readonly hasOAuthSecret = computed(() => this.server()?.oauth?.hasClientSecret === true);
  readonly usesOAuthReference = computed(() => this.isOAuth() && this.oauthSecretSource() === 'reference');
  readonly showOAuthSecretInput = computed(() => this.isOAuth() && !this.usesOAuthReference() &&
    (!this.hasOAuthSecret() || this.oauthSecretAction() === 'replace'));
  /**
   * The confirmed authorization server stays valid while the address and the OAuth use stay; a new
   * server, a new address or a switch to OAuth needs discovery and the administrator's confirmation.
   */
  readonly needsConfirmation = computed(() => {
    const server = this.server();
    return this.isOAuth() && (server === null || server.authorization === null ||
      server.url !== this.url().trim() || (server.authMode !== 'oauth' && server.authMode !== 'oauth-client-credentials'));
  });
  readonly pkceMissing = computed(() => this.authMode() === 'oauth' && this.discovery()?.pkceSupported === false);
  readonly confirmMissing = computed(() => this.needsConfirmation() && (!this.confirmAuthorization() || this.discovery() === null));
  readonly scopesInvalid = computed(() => {
    const scopes = this.oauthScopes().trim();
    return scopes.length > 1_024 || (scopes.length > 0 && !scopesPattern.test(scopes));
  });
  readonly clientIdMissing = computed(() => this.isClientCredentials() && this.oauthClientId().trim().length === 0);
  readonly labelMissing = computed(() => this.isOrganizationOAuth() &&
    (this.organizationAccountLabel().trim().length === 0 || this.organizationAccountLabel().length > 200));
  readonly oauthReferenceInvalid = computed(() => this.usesOAuthReference() && !isSecretReference(this.oauthSecretReference().trim()));
  /** The registration discovery offers when no client id is entered. */
  readonly registration = computed(() => {
    const found = this.discovery();
    if (!found) {
      return null;
    }
    return found.clientIdMetadataDocumentSupported ? 'metadata-document' : found.dynamicRegistrationSupported ? 'dynamic' : 'manual';
  });
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
  readonly oauthInvalid = computed(() => this.isOAuth() && (this.confirmMissing() || this.pkceMissing() || this.scopesInvalid() ||
    this.clientIdMissing() || this.labelMissing() || this.oauthReferenceInvalid()));
  readonly headerNameInvalid = computed(() => {
    const header = this.headerName().trim();
    return this.authMode() === 'api-key' && header.length > 0 && !headerNamePattern.test(header);
  });
  readonly valid = computed(() => !this.idInvalid() && !this.nameMissing() && !this.urlInvalid() && !this.headerNameInvalid() &&
    !this.userNameInvalid() && !this.secretReferenceInvalid() && !this.certificateInvalid() && !this.oauthInvalid());
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));
  /** The server's first error per field of the refused save, until the administrator changes the field. */
  readonly serverErrors = computed<Readonly<Record<string, string>>>(() => {
    const touched = this.touched();
    const errors: Record<string, string> = {};
    for (const [field, values] of Object.entries(this.error()?.fieldErrors ?? {})) {
      if (values.length > 0 && !touched.has(field)) {
        errors[field] = values[0];
      }
    }
    return errors;
  });
  /** Field errors of fields the editor does not show, listed with the form's error. */
  readonly otherServerErrors = computed(() => Object.entries(this.serverErrors())
    .filter(([field]) => !(serverFields as readonly string[]).includes(field))
    .map(([field, value]) => ({ field, keys: this.valueKeys(value) })));
  readonly discoveryErrorKeys = computed(() => nhAssistantErrorKeys(this.discoveryError()));

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.subscription?.unsubscribe();
      this.discoverySubscription?.unsubscribe();
      this.modesSubscription?.unsubscribe();
    });
    // The application's own modes; without them the editor offers the built-in modes.
    this.modesSubscription = this.admin.getMcpAuthModes().subscribe({
      next: modes => this.customAuthModes.set(modes),
      error: () => this.customAuthModes.set([])
    });

    effect(() => {
      const server = this.server();
      untracked(() => this.reset(server));
    });
  }

  value(event: Event): string {
    return (event.target as HTMLInputElement | HTMLSelectElement).value;
  }

  /** Whether the server refused the field in the last save. */
  serverInvalid(field: string): boolean {
    return this.serverErrors()[field] !== undefined;
  }

  /** The text of the server's error of a field: the text of its value, then the generic text. */
  serverErrorKeys(field: string): string[] {
    const value = this.serverErrors()[field];
    return value ? this.valueKeys(value) : [];
  }

  /** The id of a field's error, for `aria-describedby`. */
  errorId(field: string): string {
    return `${this.id}-${field.replace('.', '-')}-error`;
  }

  /** The ids a control describes itself with: its hint and, while the server refused it, its error. */
  describedBy(hintId: string | null, field: string): string | null {
    const ids = [hintId, this.serverInvalid(field) ? this.errorId(field) : null].filter((id): id is string => id !== null);
    return ids.length > 0 ? ids.join(' ') : null;
  }

  /** The administrator changed a field; its server error no longer applies. */
  touch(field: string): void {
    if (this.serverErrors()[field] !== undefined) {
      this.touched.set(new Set([...this.touched(), field]));
    }
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

  /** A new address makes earlier discovery meaningless. */
  onUrlInput(event: Event): void {
    this.url.set(this.value(event));
    this.discoverySubscription?.unsubscribe();
    this.discovering.set(false);
    this.discovery.set(null);
    this.confirmAuthorization.set(false);
  }

  setOAuthSecretSource(source: NhAssistantSecretSource): void {
    this.oauthSecretSource.set(source);
    this.oauthSecret.set('');
    this.oauthSecretAction.set('keep');
  }

  onOAuthSecretInput(event: Event): void {
    this.oauthSecret.set(this.value(event));
    if (!this.hasOAuthSecret()) {
      this.oauthSecretAction.set(this.oauthSecret().length > 0 ? 'replace' : 'keep');
    }
  }

  /** Reads the authorization of the address; the administrator confirms what it found. */
  discover(): void {
    if (this.urlInvalid() || this.discovering()) {
      return;
    }
    this.discovering.set(true);
    this.discovery.set(null);
    this.discoveryError.set(null);
    this.confirmAuthorization.set(false);
    this.discoverySubscription?.unsubscribe();
    this.discoverySubscription = this.admin.discoverMcpServer(this.url().trim()).subscribe({
      next: discovery => {
        this.discovering.set(false);
        this.discovery.set(discovery);
        if (this.oauthScopes().trim().length === 0 && discovery.challengedScopes.length > 0) {
          // The scopes the server asked for are a starting point the administrator can narrow.
          this.oauthScopes.set(discovery.challengedScopes.join(' '));
        }
      },
      error: (error: unknown) => {
        this.discovering.set(false);
        this.discoveryError.set(toNhAssistantError(error));
      }
    });
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
    if (this.isOAuth()) {
      input.connectionScope = authMode === 'oauth' ? this.connectionScope() : 'organization';
      input.oauth = this.oauthInput();
    }

    const existing = this.server();
    const request = existing ? this.admin.updateMcpServer(existing.id, input) : this.admin.createMcpServer(input);

    this.saving.set(true);
    this.error.set(null);
    this.touched.set(new Set());
    this.subscription?.unsubscribe();
    this.subscription = request.subscribe({
      next: server => {
        this.saving.set(false);
        this.secret.set('');
        this.oauthSecret.set('');
        this.saved.emit(server);
      },
      error: (error: unknown) => {
        this.saving.set(false);
        this.error.set(toNhAssistantError(error));
        this.focusFirstInvalid();
      }
    });
  }

  private focusFirstInvalid(): void {
    afterNextRender(() => focusFirstInvalid(this.host.nativeElement.querySelector('form')), { injector: this.injector });
  }

  private valueKeys(value: string): string[] {
    return [`nh-assistant.admin.run-config.field-errors.${value}`, 'nh-assistant.errors.generic'];
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
    this.connectionScope.set(server?.connectionScope ?? 'organization');
    this.oauthClientId.set(server?.oauth?.clientId ?? '');
    this.oauthScopes.set(server?.oauth?.scopes ?? '');
    this.organizationAccountLabel.set(server?.oauth?.organizationAccountLabel ?? '');
    this.oauthSecretSource.set(server?.oauth?.clientSecretReference ? 'reference' : 'stored');
    this.oauthSecretReference.set(server?.oauth?.clientSecretReference ?? '');
    this.oauthSecretAction.set('keep');
    this.oauthSecret.set('');
    this.discoverySubscription?.unsubscribe();
    this.discovering.set(false);
    this.discovery.set(null);
    this.discoveryError.set(null);
    this.confirmAuthorization.set(false);
    this.submitted.set(false);
    this.error.set(null);
    this.touched.set(new Set());
  }

  /**
   * The OAuth settings as the editor shows them: every value is sent, so an emptied field clears the
   * stored value. The client secret is write-only: sent only when replaced.
   */
  private oauthInput(): McpOAuthInput {
    const storedReference = this.server()?.oauth?.clientSecretReference ?? null;
    const input: McpOAuthInput = {
      clientId: this.oauthClientId().trim(),
      scopes: this.oauthScopes().trim(),
      organizationAccountLabel: this.isOrganizationOAuth() ? this.organizationAccountLabel().trim() : '',
      // Required when the address is new or changed; otherwise it pins what was read again.
      confirmAuthorization: this.discovery() !== null && this.confirmAuthorization()
    };
    if (this.usesOAuthReference()) {
      input.clientSecretReference = this.oauthSecretReference().trim();
    } else {
      if (storedReference !== null) {
        input.clientSecretReference = '';
      }
      if (this.oauthSecretAction() === 'replace' && this.oauthSecret().length > 0) {
        input.clientSecret = this.oauthSecret();
      }
    }
    return input;
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
