import {
  AdminAgent,
  McpAuthMode,
  McpAuthorization,
  McpAuthorizationStart,
  McpCompletion,
  McpConnection,
  McpConnectionScope,
  McpConnectionStatus,
  McpDiscovery,
  McpHeadlessUse,
  McpOrganizationConnection,
  McpServer,
  McpServerInput,
  McpServerTestResult,
  McpTool,
  McpToolEffect,
  NhAssistantMcpAuthorizationOutcome,
  ToolCatalogEntry
} from '@newheap/platform-ai-chat';
import { NhAssistantMockRemoteTool, NhAssistantMockScenario } from './nh-assistant-mock.models';
import {
  NhAssistantMockResult,
  mockDashCase,
  mockFailure as failure,
  mockValidationFailure as validationFailure
} from './nh-assistant-mock-admin-shared';

interface StoredServer {
  server: Omit<McpServer, 'hasSecret' | 'assignedAgentIds' | 'organizationConnection'>;
  /** A stored secret; a secret reference is `server.secretReference`. They exclude each other. */
  secret: string | null;
  /** The OAuth client secret; never returned. */
  oauthSecret: string | null;
  /** The organization's OAuth connection; null while not connected. */
  organization: McpOrganizationConnection | null;
  /** The caller's own OAuth connection of a server each person connects to; null while not connected. */
  personal: McpOrganizationConnection | null;
  remoteTools: NhAssistantMockRemoteTool[];
  tools: Map<string, McpTool & { inputSchemaHash: string }>;
}

/** An authorization the mock started; the mock authorization window plays its callback. */
interface StoredFlow {
  serverId: string;
  owner: 'organization' | 'user';
  completionCode: string;
  status: 'pending' | 'exchanged' | 'completed' | 'failed';
  allowBackgroundUse: boolean;
  expiresAt: number;
}

/** What the MCP servers read of the agents and the caller. */
export interface NhAssistantMockMcpContext {
  /** Every agent, for the servers assigned to them. */
  agents(): AdminAgent[];
  /** The ids of the agents the caller may use in the chat. */
  chatAgentIds(): string[];
  /** Removes a deleted server from the agents it was assigned to. */
  removeServer(serverId: string): void;
  /** Whether the caller passes the administration policy. */
  canAdminister(): boolean;
}

const authModes = ['none', 'bearer', 'api-key', 'basic', 'forward-user-token', 'oauth', 'oauth-client-credentials'];
const secretModes = ['bearer', 'api-key', 'basic'];
const oauthModes = ['oauth', 'oauth-client-credentials'];
const connectionScopes: McpConnectionScope[] = ['organization', 'user'];
const scopeTokens = /^[\x21\x23-\x5B\x5D-\x7E]+( [\x21\x23-\x5B\x5D-\x7E]+)*$/;
const flowLifetimeMs = 10 * 60_000;
const headlessUses: McpHeadlessUse[] = ['never', 'organization', 'personal'];
const transports = ['streamable-http', 'auto'];
const secretReference = /^[a-z][a-z0-9-]{0,31}:[A-Za-z0-9][A-Za-z0-9._/-]{0,199}$/;
const certificateName = /^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$/;

/**
 * The MCP servers of the mock administration and the caller's own connections: secrets are
 * write-only, new tools start disabled as mutations, a changed remote schema disables a tool, OAuth
 * servers are discovered and confirmed, and authorizations play through the mock window. The mock
 * has one person, the caller, so "personal connections" are the caller's.
 */
export class NhAssistantMockAdminMcp {
  private readonly servers = new Map<string, StoredServer>();
  /** The caller's consent per server, with the server's address and authentication it holds for. */
  private readonly consents = new Map<string, { fingerprint: string; consentedAt: string }>();
  /** Authorizations started through the mock, by flow id. */
  private readonly flows = new Map<string, StoredFlow>();
  /** What the mock authorization window answers: the person approves, denies or closes the window. */
  private authorizationOutcome: 'approve' | 'deny' | 'cancel';

  constructor(
    private readonly scenario: NhAssistantMockScenario,
    private readonly now: () => string,
    private readonly context: NhAssistantMockMcpContext
  ) {
    const admin = scenario.admin ?? {};
    this.authorizationOutcome = admin.mcpAuthorization ?? 'approve';

    for (const { remoteTools, secret, organizationConnected, personalConnected, ...input } of admin.mcpServers ?? []) {
      const stored: StoredServer = {
        server: { ...toServerFields(input), ...newServerState(input), ...oauthFields(input, null, null) },
        secret: secret ?? null,
        oauthSecret: input.oauth?.clientSecret || null,
        organization: null,
        personal: null,
        remoteTools: remoteTools ?? [],
        tools: new Map()
      };
      if (oauthModes.includes(input.authMode)) {
        // A preconfigured OAuth server was confirmed by an administrator before.
        const found = this.discovery(input.url);
        stored.server.authorization = typeof found === 'string'
          ? null
          : { issuer: found.issuer, resource: found.resource, confirmedAt: now(), confirmedBy: 'current-user' };
        if (organizationConnected) {
          stored.organization = activeConnection(now(), 'current-user', input.oauth?.scopes ?? null);
        }
        if (personalConnected) {
          stored.personal = activeConnection(now(), 'current-user', input.oauth?.scopes ?? null);
        }
      }
      this.servers.set(input.id, stored);
    }
    for (const consent of admin.mcpConnections ?? []) {
      const stored = this.servers.get(consent.serverId);
      if (stored && consent.allowBackgroundUse) {
        this.consents.set(consent.serverId, { fingerprint: consentFingerprint(stored), consentedAt: now() });
      }
    }
  }

  hasServer(serverId: string): boolean {
    return this.servers.has(serverId);
  }

  /** What the mock authorization window answers next. */
  setAuthorizationOutcome(outcome: 'approve' | 'deny' | 'cancel'): void {
    this.authorizationOutcome = outcome;
  }

  /**
   * Simulates what happens to a server's OAuth connections outside the application: the
   * authorization server refused a refresh (`reauthorization-required`), or the server named another
   * authorization server (`issuer-changed`).
   */
  setConnectionStatus(serverId: string, status: Exclude<McpConnectionStatus, 'not-connected' | 'active'>): void {
    const stored = this.servers.get(serverId);
    const failure = status === 'reauthorization-required' ? 'assistant-mcp-reauthorization-required' : null;
    if (stored?.organization) {
      stored.organization = { ...stored.organization, status, lastFailureCode: failure };
    }
    if (stored?.personal) {
      stored.personal = { ...stored.personal, status, lastFailureCode: failure };
    }
  }

  /**
   * Why the caller has to connect a server each person connects to before a turn can use it, or null
   * when the caller's connection is active.
   */
  connectionNeeded(serverId: string): { displayName: string; reason: 'not-connected' | 'reauthorization-required' | 'insufficient-scope' } | null {
    const stored = this.servers.get(serverId);
    if (!stored) {
      return null;
    }
    const personal = stored.personal;
    if (personal?.status === 'active') {
      return personal.requiredScopes ? { displayName: stored.server.displayName, reason: 'insufficient-scope' } : null;
    }
    return {
      displayName: stored.server.displayName,
      reason: personal?.status === 'reauthorization-required' ? 'reauthorization-required' : 'not-connected'
    };
  }

  /**
   * Plays the callback of a started authorization as the authorization server and the callback page
   * would: the person approves, denies or closes the window.
   */
  authorizeFlow(flowId: string): NhAssistantMcpAuthorizationOutcome {
    const flow = this.flows.get(flowId);
    if (!flow || flow.status !== 'pending' || flow.expiresAt < Date.parse(this.now())) {
      return { flowId: null, code: 'assistant-mcp-authorization-expired' };
    }
    if (this.authorizationOutcome === 'cancel') {
      return { flowId, code: 'assistant-mcp-authorization-cancelled' };
    }
    if (this.authorizationOutcome === 'deny') {
      flow.status = 'failed';
      return { flowId, code: 'assistant-mcp-authorization-failed' };
    }
    flow.status = 'exchanged';
    return { flowId, completionCode: flow.completionCode };
  }

  /** Replaces the tool list of a simulated remote server; the next sync picks it up. */
  setRemoteTools(serverId: string, tools: NhAssistantMockRemoteTool[]): void {
    const stored = this.servers.get(serverId);
    if (stored) {
      stored.remoteTools = structuredClone(tools);
    }
  }

  /** The enabled MCP tools for the tool catalog of the administration. */
  catalogEntries(): ToolCatalogEntry[] {
    const catalog: ToolCatalogEntry[] = [];
    for (const stored of this.servers.values()) {
      for (const tool of stored.tools.values()) {
        if (tool.isEnabled) {
          catalog.push({ id: tool.localId, source: 'mcp', effect: tool.effect, description: tool.descriptionOverride ?? tool.description });
        }
      }
    }
    return catalog;
  }

  /**
   * `GET mcp-connections` and `PUT mcp-connections/{serverId}`: the caller's consent to background
   * use of servers that run with the caller's own account. A consent holds for the server's address
   * and authentication as they were; a change shows it as not allowed again.
   */
  handleConnections(method: string, segments: string[], body: unknown): NhAssistantMockResult {
    if (segments.length === 2 && segments[1] === 'complete') {
      return method === 'POST' ? this.complete(body) : { status: 405 };
    }
    const personal = this.personalServers();
    if (segments.length === 1) {
      return method === 'GET' ? { status: 200, body: personal } : { status: 405 };
    }
    const serverId = segments[1];
    const listed = personal.find(connection => connection.serverId === serverId);
    if (segments.length === 3 && segments[2] === 'authorize') {
      if (method !== 'POST') {
        return { status: 405 };
      }
      if (!listed) {
        return failure(404, 'assistant-mcp-server-not-found');
      }
      if (listed.connectionStatus === null) {
        return validationFailure('serverId');
      }
      const request = body as { returnUrl?: unknown; allowBackgroundUse?: unknown } | undefined;
      if (request?.returnUrl !== undefined && request.returnUrl !== null && !this.allowedReturnUrl(request.returnUrl)) {
        return validationFailure('returnUrl');
      }
      return this.startFlow(this.servers.get(serverId)!, 'user', request?.allowBackgroundUse === true && listed.offersBackgroundUse);
    }
    if (segments.length === 2 && method === 'DELETE') {
      if (!listed) {
        return failure(404, 'assistant-mcp-server-not-found');
      }
      if (listed.connectionStatus === null) {
        return validationFailure('serverId');
      }
      // The consent to background use stays until the person withdraws it.
      this.servers.get(serverId)!.personal = null;
      return { status: 204 };
    }
    if (segments.length !== 2 || method !== 'PUT') {
      return segments.length === 2 ? { status: 405 } : failure(404, 'assistant-not-found');
    }

    if (!listed) {
      return failure(404, 'assistant-mcp-server-not-found');
    }
    if (!listed.offersBackgroundUse) {
      return failure(400, 'assistant-mcp-headless-not-allowed');
    }
    const allow = (body as { allowBackgroundUse?: unknown } | undefined)?.allowBackgroundUse;
    if (typeof allow !== 'boolean') {
      return failure(400, 'assistant-validation', { allowBackgroundUse: ['required'] });
    }
    if (allow) {
      this.consents.set(serverId, { fingerprint: consentFingerprint(this.servers.get(serverId)!), consentedAt: this.now() });
    } else {
      this.consents.delete(serverId);
    }
    return { status: 200, body: this.personalServers().find(connection => connection.serverId === serverId) };
  }

  /** `admin/mcp-servers` and everything below it. */
  serversEndpoint(
    method: string,
    id: string | undefined,
    action: string | undefined,
    child: string | undefined,
    body: unknown,
    length: number
  ): NhAssistantMockResult {
    if (id === 'discover' && length === 3 && method === 'POST') {
      const url = (body as { url?: unknown } | undefined)?.url;
      if (typeof url !== 'string' || url.trim().length === 0) {
        return failure(400, 'assistant-validation', { url: ['required'] });
      }
      const found = this.discovery(url.trim());
      return typeof found === 'string' ? failure(discoveryStatus(found), found) : { status: 200, body: found };
    }
    if (id === undefined) {
      if (method === 'GET') {
        return { status: 200, body: [...this.servers.values()].map(stored => this.toServer(stored)) };
      }
      if (method !== 'POST') {
        return { status: 405 };
      }

      const input = body as McpServerInput;
      if (!validServer(input)) {
        return validationFailure();
      }
      if (this.servers.has(input.id)) {
        return failure(409, 'assistant-mcp-server-exists');
      }
      const credentialFailure = this.credentialFailure(input, null);
      if (credentialFailure) {
        return credentialFailure;
      }
      const oauth = this.oauthCheck(input, null);
      if ('failure' in oauth) {
        return oauth.failure;
      }

      const stored: StoredServer = {
        server: { ...toServerFields(input), ...newServerState(input), ...oauthFields(input, null, oauth.authorization) },
        secret: secretModes.includes(input.authMode) && input.secret ? input.secret : null,
        oauthSecret: oauthModes.includes(input.authMode) && input.oauth?.clientSecret ? input.oauth.clientSecret : null,
        organization: null,
        personal: null,
        remoteTools: [],
        tools: new Map()
      };
      this.servers.set(input.id, stored);
      return { status: 201, body: this.toServer(stored) };
    }

    const stored = this.servers.get(id);
    if (!stored) {
      return failure(404, 'assistant-mcp-server-not-found');
    }

    if (length === 3) {
      if (method === 'PUT') {
        const input = body as McpServerInput;
        if (!validServer(input) || input.id !== id) {
          return validationFailure();
        }
        // An omitted headless use, transport or user name keeps the stored value, as on the server.
        const headlessUse = input.headlessUse ?? stored.server.headlessUse;
        if (!fitsHeadlessUse(headlessUse, input.authMode, input.connectionScope ?? stored.server.connectionScope)) {
          return validationFailure('headlessUse');
        }
        const credentialFailure = this.credentialFailure(input, stored);
        if (credentialFailure) {
          return credentialFailure;
        }
        const oauth = this.oauthCheck(input, stored);
        if ('failure' in oauth) {
          return oauth.failure;
        }
        const before = stored.server;
        stored.server = {
          ...stored.server,
          ...toServerFields(input, stored.server),
          ...oauthFields(input, stored.server, oauth.authorization),
          headlessUse,
          transport: input.transport ?? stored.server.transport
        };
        if (!oauthModes.includes(input.authMode)) {
          stored.oauthSecret = null;
        } else if (input.oauth?.clientSecret) {
          stored.oauthSecret = input.oauth.clientSecret;
        } else if (input.oauth?.clientSecretReference) {
          stored.oauthSecret = null;
        }
        stored.server = { ...stored.server, oauth: stored.server.oauth && { ...stored.server.oauth, hasClientSecret: stored.oauthSecret !== null } };
        if (oauthChanged(before, stored.server)) {
          // The tokens no longer fit the server: they are revoked and everyone connects again.
          stored.organization = null;
          stored.personal = null;
        } else if (oauth.authorization !== null && before.authorization?.issuer === oauth.authorization.issuer &&
          stored.organization?.status === 'issuer-changed') {
          stored.organization = { ...stored.organization, status: 'active' };
        }
        if (!secretModes.includes(input.authMode)) {
          stored.secret = null;
          stored.server.secretReference = null;
        } else if (input.secret) {
          stored.secret = input.secret;
          stored.server.secretReference = null;
        } else if (input.secret === '') {
          stored.secret = null;
        }
        if (input.secretReference && secretModes.includes(input.authMode)) {
          stored.secret = null;
        }
        return { status: 200, body: this.toServer(stored) };
      }
      if (method === 'DELETE') {
        this.servers.delete(id);
        this.context.removeServer(id);
        return { status: 204 };
      }
      return { status: 405 };
    }

    if (action === 'connection' && length === 4) {
      if (method === 'GET') {
        return { status: 200, body: this.toServer(stored).organizationConnection ?? notConnected() };
      }
      if (method === 'DELETE') {
        stored.organization = null;
        return { status: 204 };
      }
      return { status: 405 };
    }
    if (action === 'connection' && child === 'authorize' && length === 5) {
      return method === 'POST' ? this.startAuthorization(stored, body) : { status: 405 };
    }
    if (action === 'connections' && length === 4) {
      if (method !== 'DELETE') {
        return { status: 405 };
      }
      stored.personal = null;
      return { status: 204 };
    }
    if (action === 'connections' && child === 'summary' && length === 5) {
      // The mock has one person: the caller.
      const status = stored.personal?.status;
      return method === 'GET'
        ? {
          status: 200,
          body: {
            active: status === 'active' ? 1 : 0,
            reauthorizationRequired: status === 'reauthorization-required' ? 1 : 0,
            issuerChanged: status === 'issuer-changed' ? 1 : 0
          }
        }
        : { status: 405 };
    }

    if (length === 4 && action === 'test' && method === 'POST') {
      const code = this.connectionFailure(stored);
      const result: McpServerTestResult = code
        ? { ok: false, code, toolCount: null }
        : { ok: true, code: null, toolCount: stored.remoteTools.length };
      stored.server = { ...stored.server, lastConnectAt: this.now(), lastConnectCode: code };
      return { status: 200, body: result };
    }
    if (length === 4 && action === 'sync' && method === 'POST') {
      const code = this.connectionFailure(stored);
      const syncedAt = this.now();
      stored.server = {
        ...stored.server,
        lastSyncAt: syncedAt,
        lastSyncStatus: code ? 'failed' : 'ok',
        lastConnectAt: syncedAt,
        lastConnectCode: code
      };
      if (code) {
        // A blocked host is a configuration error; connection failures are upstream errors.
        return failure(code === 'assistant-mcp-host-blocked' ? 400 : 502, code);
      }
      this.sync(stored);
      return { status: 200, body: toTools(stored) };
    }
    if (length === 4 && action === 'tools' && method === 'GET') {
      return { status: 200, body: toTools(stored) };
    }
    if (length === 5 && action === 'tools' && method === 'PUT' && child !== undefined) {
      const tool = stored.tools.get(child);
      const input = body as { isEnabled?: unknown; effect?: unknown; descriptionOverride?: unknown } | undefined;
      if (!tool) {
        return failure(404, 'assistant-mcp-tool-not-found');
      }
      if (typeof input?.isEnabled !== 'boolean' || (input.effect !== 'read-only' && input.effect !== 'mutation') ||
        (input.descriptionOverride !== null && typeof input.descriptionOverride !== 'string')) {
        return validationFailure();
      }
      if (input.isEnabled && tool.status === 'missing') {
        return validationFailure('isEnabled');
      }

      const override = typeof input.descriptionOverride === 'string' && input.descriptionOverride.trim().length > 0
        ? input.descriptionOverride.slice(0, 1_000)
        : null;
      const updated = {
        ...tool,
        isEnabled: input.isEnabled,
        effect: input.effect as McpToolEffect,
        descriptionOverride: override,
        status: input.isEnabled && tool.status === 'schema-changed' ? 'available' as const : tool.status
      };
      stored.tools.set(child, updated);
      return { status: 200, body: toTool(updated) };
    }

    return failure(404, 'assistant-not-found');
  }

  private sync(stored: StoredServer): void {
    const seen = new Set<string>();
    for (const remote of stored.remoteTools) {
      seen.add(remote.remoteName);
      const existing = stored.tools.get(remote.remoteName);
      const description = remote.description.slice(0, 1_000);
      if (!existing) {
        stored.tools.set(remote.remoteName, {
          remoteName: remote.remoteName,
          localId: `mcp.${stored.server.id}.${kebab(remote.remoteName)}`,
          description,
          descriptionOverride: null,
          isEnabled: false,
          effect: 'mutation',
          status: 'available',
          readOnlyHint: remote.readOnlyHint ?? null,
          inputSchemaHash: remote.inputSchemaHash
        });
        continue;
      }

      const changed = existing.inputSchemaHash !== remote.inputSchemaHash;
      stored.tools.set(remote.remoteName, {
        ...existing,
        description,
        readOnlyHint: remote.readOnlyHint ?? null,
        inputSchemaHash: remote.inputSchemaHash,
        isEnabled: changed ? false : existing.isEnabled,
        status: changed ? 'schema-changed' : existing.status === 'missing' ? 'available' : existing.status
      });
    }

    for (const [name, tool] of stored.tools) {
      if (!seen.has(name)) {
        stored.tools.set(name, { ...tool, isEnabled: false, status: 'missing' });
      }
    }
  }

  /**
   * The OAuth part of a server change, as on the server: the connection scope, the scopes, a client
   * id and secret where needed, the account label of an organization connection, and discovery the
   * administrator confirms for a new server or a new address. Returns the authorization to pin.
   */
  private oauthCheck(input: McpServerInput, stored: StoredServer | null): { failure: NhAssistantMockResult } | { authorization: McpAuthorization | null } {
    if (!oauthModes.includes(input.authMode)) {
      return { authorization: null };
    }
    const scope = input.connectionScope ?? stored?.server.connectionScope ?? 'organization';
    if (!connectionScopes.includes(scope) || (input.authMode !== 'oauth' && scope !== 'organization')) {
      return { failure: validationFailure('connectionScope') };
    }
    const settings = stored?.server.oauth ?? null;
    const clientId = effective(input.oauth?.clientId, settings?.clientId ?? null);
    const scopes = effective(input.oauth?.scopes, settings?.scopes ?? null);
    const label = effective(input.oauth?.organizationAccountLabel, settings?.organizationAccountLabel ?? null);
    const reference = effective(input.oauth?.clientSecretReference, settings?.clientSecretReference ?? null);
    if (scopes !== null && (scopes.length > 1_024 || !scopeTokens.test(scopes.trim()))) {
      return { failure: validationFailure('oauth.scopes') };
    }
    if (input.authMode === 'oauth' && scope === 'organization' && (label === null || label.trim().length === 0)) {
      return { failure: failure(400, 'assistant-validation', { 'oauth.organizationAccountLabel': ['required'] }) };
    }
    if (input.authMode === 'oauth-client-credentials') {
      if (clientId === null || clientId.trim().length === 0) {
        return { failure: failure(400, 'assistant-validation', { 'oauth.clientId': ['required'] }) };
      }
      const hasSecret = input.oauth?.clientSecret === undefined || input.oauth?.clientSecret === null
        ? stored?.oauthSecret != null && !input.oauth?.clientSecretReference
        : input.oauth.clientSecret.length > 0;
      const certificate = input.clientCertificateReference === undefined || input.clientCertificateReference === null
        ? stored?.server.clientCertificateReference ?? null
        : input.clientCertificateReference || null;
      if (!hasSecret && reference === null && certificate === null) {
        return { failure: failure(400, 'assistant-validation', { 'oauth.clientSecret': ['required'] }) };
      }
    }
    const pinned = stored !== null && oauthModes.includes(stored.server.authMode) ? stored.server.authorization : null;
    if (input.oauth?.confirmAuthorization !== true) {
      return pinned !== null && stored!.server.url === input.url.trim()
        ? { authorization: pinned }
        : { failure: failure(400, 'assistant-validation', { 'oauth.confirmAuthorization': ['required'] }) };
    }
    const found = this.discovery(input.url.trim());
    if (typeof found === 'string') {
      return { failure: failure(discoveryStatus(found), found) };
    }
    if (input.authMode === 'oauth' && !found.pkceSupported) {
      return { failure: failure(400, 'assistant-mcp-pkce-unsupported') };
    }
    return { authorization: { issuer: found.issuer, resource: found.resource, confirmedAt: this.now(), confirmedBy: 'current-user' } };
  }

  /**
   * What discovery finds for an address: a host with `no-oauth` has no protected resource metadata,
   * `no-pkce` an authorization server without PKCE; `admin.mcpDiscovery` overrides per host.
   */
  private discovery(url: string): McpDiscovery | string {
    let parsed: URL;
    try {
      parsed = new URL(url);
    } catch {
      return 'assistant-validation';
    }
    const host = parsed.hostname.toLowerCase();
    if (host.startsWith('169.254.') || host.startsWith('[fe80') || host.includes('metadata')) {
      return 'assistant-mcp-host-blocked';
    }
    if (host.includes('no-oauth') || host.includes('unreachable')) {
      return 'assistant-mcp-discovery-failed';
    }
    const issuer = `https://auth.${host}`;
    const path = parsed.pathname.replace(/\/+$/, '');
    return {
      resource: `${parsed.protocol}//${parsed.host.toLowerCase()}${path}${parsed.search}`,
      issuer,
      authorizationServers: [issuer],
      challengedScopes: ['mcp:read'],
      scopesSupported: ['mcp:read', 'mcp:write', 'offline_access'],
      grantTypesSupported: ['authorization_code', 'refresh_token', 'client_credentials'],
      pkceSupported: !host.includes('no-pkce'),
      clientIdMetadataDocumentSupported: false,
      dynamicRegistrationSupported: true,
      tokenEndpointAuthMethods: ['client_secret_basic', 'private_key_jwt', 'none'],
      ...this.scenario.admin?.mcpDiscovery?.[host]
    };
  }

  /** `POST admin/mcp-servers/{id}/connection/authorize`: starts the organization's authorization. */
  private startAuthorization(stored: StoredServer, body: unknown): NhAssistantMockResult {
    if (stored.server.authMode !== 'oauth') {
      return validationFailure('authMode');
    }
    const returnUrl = (body as { returnUrl?: unknown } | undefined)?.returnUrl;
    if (returnUrl !== undefined && returnUrl !== null && !this.allowedReturnUrl(returnUrl)) {
      return validationFailure('returnUrl');
    }
    // For a server each person connects to, the administrator connects their own account.
    return this.startFlow(stored, stored.server.connectionScope === 'user' ? 'user' : 'organization', false);
  }

  private startFlow(stored: StoredServer, owner: 'organization' | 'user', allowBackgroundUse: boolean): NhAssistantMockResult {
    const authorization = stored.server.authorization;
    if (authorization === null) {
      return failure(409, 'assistant-mcp-authorization-not-confirmed');
    }
    const flowId = uuid();
    const expiresAt = Date.parse(this.now()) + flowLifetimeMs;
    this.flows.set(flowId, {
      serverId: stored.server.id,
      owner,
      completionCode: `mock-completion-${flowId.replace(/-/g, '')}`,
      status: 'pending',
      allowBackgroundUse,
      expiresAt
    });
    const query = new URLSearchParams({
      response_type: 'code',
      client_id: stored.server.oauth?.clientId ?? 'mock-registered-client',
      resource: authorization.resource,
      state: flowId
    });
    const start: McpAuthorizationStart = {
      flowId,
      authorizationUrl: `${authorization.issuer}/authorize?${query.toString()}`,
      expiresAt: new Date(expiresAt).toISOString(),
      callbackOrigin: 'https://assistant.mock.local'
    };
    return { status: 200, body: start };
  }

  /** `POST mcp-connections/complete`: activates the connection of a flow the caller started. */
  private complete(body: unknown): NhAssistantMockResult {
    const request = body as { flowId?: unknown; completionCode?: unknown } | undefined;
    if (typeof request?.flowId !== 'string' || request.flowId.length === 0) {
      return failure(400, 'assistant-validation', { flowId: ['required'] });
    }
    if (typeof request.completionCode !== 'string' || request.completionCode.length === 0) {
      return failure(400, 'assistant-validation', { completionCode: ['required'] });
    }
    const flow = this.flows.get(request.flowId);
    if (!flow || flow.status !== 'exchanged' || flow.expiresAt < Date.parse(this.now())) {
      return failure(400, 'assistant-mcp-authorization-expired');
    }
    if (flow.completionCode !== request.completionCode) {
      return failure(400, 'assistant-mcp-authorization-invalid');
    }
    if (flow.owner === 'organization' && !this.context.canAdminister()) {
      return failure(403, 'assistant-forbidden');
    }
    const stored = this.servers.get(flow.serverId);
    if (!stored) {
      return failure(404, 'assistant-mcp-server-not-found');
    }
    flow.status = 'completed';
    const connection = activeConnection(this.now(), 'current-user', stored.server.oauth?.scopes ?? null);
    if (flow.owner === 'user') {
      stored.personal = connection;
      if (flow.allowBackgroundUse) {
        this.consents.set(stored.server.id, { fingerprint: consentFingerprint(stored), consentedAt: this.now() });
      }
    } else {
      stored.organization = connection;
    }
    const completion: McpCompletion = { serverId: stored.server.id, ownerKind: flow.owner, status: 'active', grantedScopes: connection.grantedScopes };
    return { status: 200, body: completion };
  }

  /** By default every http(s) address; `admin.mcpReturnOrigins` limits the origins. */
  private allowedReturnUrl(value: unknown): boolean {
    if (typeof value !== 'string') {
      return false;
    }
    try {
      const url = new URL(value);
      const origins = this.scenario.admin?.mcpReturnOrigins;
      return (url.protocol === 'https:' || url.protocol === 'http:') && url.hash.length === 0 &&
        (origins === undefined || origins.includes(url.origin));
    } catch {
      return false;
    }
  }

  private connectionFailure(stored: StoredServer): string | null {
    const oauthFailure = this.oauthConnectionFailure(stored);
    if (oauthFailure !== undefined) {
      return oauthFailure;
    }
    let url: URL;
    try {
      url = new URL(stored.server.url);
    } catch {
      return 'assistant-mcp-unreachable';
    }

    const host = url.hostname.toLowerCase();
    if (host.startsWith('169.254.') || host.startsWith('[fe80') || host.includes('metadata')) {
      return 'assistant-mcp-host-blocked';
    }
    if (stored.server.authMode === 'forward-user-token' && !(this.scenario.admin?.forwardUserTokenHosts ?? []).includes(host)) {
      return 'assistant-mcp-host-blocked';
    }
    if (host.includes('unreachable')) {
      return 'assistant-mcp-unreachable';
    }
    if (secretModes.includes(stored.server.authMode) && !stored.secret && !stored.server.secretReference) {
      return 'assistant-mcp-unauthorized';
    }
    return null;
  }

  /**
   * The state of an OAuth server's organization connection for a test or sync: client credentials
   * connect on first use; an authorization needs an administrator to connect first. `undefined` for
   * servers without OAuth.
   */
  private oauthConnectionFailure(stored: StoredServer): string | null | undefined {
    const server = stored.server;
    if (!oauthModes.includes(server.authMode)) {
      return undefined;
    }
    if (server.authorization === null) {
      return 'assistant-mcp-authorization-not-confirmed';
    }
    if (server.authMode === 'oauth-client-credentials' && stored.organization === null) {
      stored.organization = activeConnection(this.now(), 'client-credentials', server.oauth?.scopes ?? null);
    }
    if (server.authMode === 'oauth' && server.connectionScope === 'user') {
      // Tests and syncs of a server each person connects to use the administrator's own account.
      switch (stored.personal?.status ?? 'not-connected') {
        case 'not-connected':
          return 'assistant-mcp-connection-required';
        case 'reauthorization-required':
          return 'assistant-mcp-reauthorization-required';
        case 'issuer-changed':
          return 'assistant-mcp-issuer-changed';
        default:
          return null;
      }
    }
    switch (stored.organization?.status ?? 'not-connected') {
      case 'not-connected':
        return 'assistant-mcp-connection-required';
      case 'reauthorization-required':
        return 'assistant-mcp-reauthorization-required';
      case 'issuer-changed':
        return 'assistant-mcp-issuer-changed';
      default:
        return null;
    }
  }

  /**
   * The contract failure of the input's credential: a basic user name, a secret or a reference that
   * names an available secret, and a configured client certificate.
   */
  private credentialFailure(input: McpServerInput, stored: StoredServer | null): NhAssistantMockResult | null {
    const admin = this.scenario.admin ?? {};
    const userName = input.userName ?? stored?.server.userName ?? null;
    if (input.authMode === 'basic' && (!userName || userName.trim().length === 0)) {
      return failure(400, 'assistant-validation', { userName: ['required'] });
    }
    if (userName && (userName.length > 256 || userName.includes(':'))) {
      return failure(400, 'assistant-validation', { userName: ['invalid'] });
    }
    if (input.secret && input.secretReference) {
      return failure(400, 'assistant-validation', { secretReference: ['invalid'] });
    }
    const reference = input.secretReference === undefined || input.secretReference === null
      ? (input.secret ? null : stored?.server.secretReference ?? null)
      : input.secretReference || null;
    if (reference !== null && (!secretReference.test(reference) || reference.includes('..'))) {
      return failure(400, 'assistant-validation', { secretReference: ['invalid'] });
    }
    if (secretModes.includes(input.authMode)) {
      // As on the server: bearer, api-key and basic need a stored secret or a reference. A new stored
      // secret replaces a reference, a new reference a stored secret.
      const storedSecret = input.secret === undefined || input.secret === null
        ? !!stored?.secret && !input.secretReference
        : input.secret.length > 0;
      if (!storedSecret && (reference === null || !!input.secret)) {
        return failure(400, 'assistant-validation', { secret: ['required'] });
      }
      // By default every config: reference resolves; a scenario can name the ones that do.
      if (reference !== null && !input.secret &&
        !(admin.secretReferences?.includes(reference) ?? reference.startsWith('config:'))) {
        return failure(400, 'assistant-mcp-secret-reference-invalid');
      }
    }
    const certificate = input.clientCertificateReference === undefined || input.clientCertificateReference === null
      ? stored?.server.clientCertificateReference ?? null
      : input.clientCertificateReference || null;
    if (certificate !== null && !certificateName.test(certificate)) {
      return failure(400, 'assistant-validation', { clientCertificateReference: ['invalid'] });
    }
    if (certificate !== null && !(admin.clientCertificates ?? []).includes(certificate)) {
      return failure(400, 'assistant-mcp-certificate-reference-invalid');
    }
    return null;
  }

  /** Servers with the caller's own account for background runs, on an agent the caller may use. */
  private personalServers(): McpConnection[] {
    const visible = new Set(this.context.chatAgentIds());
    const agents = this.context.agents();
    const result: McpConnection[] = [];
    for (const stored of this.servers.values()) {
      const server = stored.server;
      const agentIds = agents
        .filter(agent => visible.has(agent.id) && agent.mcpServerIds.includes(server.id))
        .map(agent => agent.id);
      const personalOAuth = server.authMode === 'oauth' && server.connectionScope === 'user';
      const forwardsToken = server.headlessUse === 'personal' && server.authMode === 'forward-user-token';
      if (!server.isEnabled || !(personalOAuth || forwardsToken) || agentIds.length === 0) {
        continue;
      }
      const consent = this.consents.get(server.id);
      const allowed = consent !== undefined && consent.fingerprint === consentFingerprint(stored);
      let host = '';
      try {
        host = new URL(server.url).hostname;
      } catch {
        host = '';
      }
      result.push({
        serverId: server.id,
        displayName: server.displayName,
        host,
        allowBackgroundUse: allowed,
        consentedAt: allowed ? consent.consentedAt : null,
        agentIds,
        connectionStatus: personalOAuth ? stored.personal?.status ?? 'not-connected' : null,
        grantedScopes: personalOAuth ? stored.personal?.grantedScopes ?? null : null,
        requiredScopes: personalOAuth ? stored.personal?.requiredScopes ?? null : null,
        connectedAt: personalOAuth ? stored.personal?.connectedAt ?? null : null,
        offersBackgroundUse: server.headlessUse === 'personal'
      });
    }
    return result;
  }

  private toServer(stored: StoredServer): McpServer {
    const server = stored.server;
    const organizationCredential = server.authMode === 'oauth-client-credentials' ||
      (server.authMode === 'oauth' && server.connectionScope === 'organization');
    return {
      ...stored.server,
      organizationConnection: organizationCredential ? stored.organization ?? notConnected() : null,
      hasSecret: stored.secret !== null,
      assignedAgentIds: this.context.agents()
        .filter(agent => agent.mcpServerIds.includes(stored.server.id))
        .map(agent => agent.id)
    };
  }
}

function toServerFields(
  input: McpServerInput,
  stored?: Omit<McpServer, 'hasSecret' | 'assignedAgentIds' | 'organizationConnection'>
): Pick<McpServer, 'id' | 'displayName' | 'url' | 'authMode' | 'headerName' | 'requiredPolicy' | 'isEnabled' | 'userName' | 'secretReference' | 'clientCertificateReference'> {
  const usesSecret = secretModes.includes(input.authMode);
  const reference = input.secretReference === undefined || input.secretReference === null
    ? (input.secret ? null : stored?.secretReference ?? null)
    : input.secretReference || null;
  const certificate = input.clientCertificateReference === undefined || input.clientCertificateReference === null
    ? stored?.clientCertificateReference ?? null
    : input.clientCertificateReference || null;
  return {
    id: input.id,
    displayName: input.displayName,
    url: input.url,
    authMode: input.authMode,
    headerName: input.authMode === 'api-key' ? (input.headerName || 'X-Api-Key') : null,
    requiredPolicy: input.requiredPolicy,
    isEnabled: input.isEnabled,
    userName: input.authMode === 'basic' ? (input.userName ?? stored?.userName ?? null)?.trim() ?? null : null,
    secretReference: usesSecret ? reference : null,
    clientCertificateReference: certificate
  };
}

/**
 * The OAuth fields of a server after an input: the connection scope, the settings without the
 * client secret, and the confirmed authorization.
 */
function oauthFields(
  input: McpServerInput,
  stored: Omit<McpServer, 'hasSecret' | 'assignedAgentIds' | 'organizationConnection'> | null,
  authorization: McpAuthorization | null
): Pick<McpServer, 'connectionScope' | 'oauth' | 'authorization'> {
  if (!oauthModes.includes(input.authMode)) {
    return { connectionScope: 'organization', oauth: null, authorization: null };
  }
  const settings = stored?.oauth ?? null;
  const scope = input.authMode === 'oauth' ? input.connectionScope ?? stored?.connectionScope ?? 'organization' : 'organization';
  const clientId = effective(input.oauth?.clientId, settings?.clientId ?? null)?.trim() || null;
  const scopes = effective(input.oauth?.scopes, settings?.scopes ?? null)?.trim() || null;
  const label = input.authMode === 'oauth' && scope === 'organization'
    ? effective(input.oauth?.organizationAccountLabel, settings?.organizationAccountLabel ?? null)?.trim() || null
    : null;
  const reference = clientId === null || input.oauth?.clientSecret
    ? null
    : effective(input.oauth?.clientSecretReference, settings?.clientSecretReference ?? null) || null;
  return {
    connectionScope: scope,
    oauth: {
      clientId,
      hasClientSecret: clientId !== null && (!!input.oauth?.clientSecret || (input.oauth?.clientSecret !== '' && reference === null && (settings?.hasClientSecret ?? false))),
      clientSecretReference: reference,
      scopes: scopes === null ? null : scopes.split(' ').filter(Boolean).join(' '),
      organizationAccountLabel: label
    },
    authorization
  };
}

/** Whether a change makes the server's stored tokens unusable. */
function oauthChanged(
  before: Omit<McpServer, 'hasSecret' | 'assignedAgentIds' | 'organizationConnection'>,
  after: Omit<McpServer, 'hasSecret' | 'assignedAgentIds' | 'organizationConnection'>
): boolean {
  return before.url !== after.url || before.authMode !== after.authMode || before.connectionScope !== after.connectionScope ||
    before.authorization?.issuer !== after.authorization?.issuer || before.oauth?.scopes !== after.oauth?.scopes ||
    before.oauth?.clientId !== after.oauth?.clientId;
}

/** The value after an input: undefined or null keeps the stored value, `""` clears it. */
function effective(input: string | null | undefined, stored: string | null): string | null {
  if (input === undefined || input === null) {
    return stored;
  }
  return input.length === 0 ? null : input;
}

function notConnected(): McpOrganizationConnection {
  return { status: 'not-connected', connectedAt: null, connectedBy: null, grantedScopes: null, requiredScopes: null, accessTokenExpiresAt: null, lastFailureCode: null };
}

function activeConnection(now: string, connectedBy: string, scopes: string | null): McpOrganizationConnection {
  return {
    status: 'active',
    connectedAt: now,
    connectedBy,
    grantedScopes: scopes ?? 'mcp:read',
    requiredScopes: null,
    accessTokenExpiresAt: new Date(Date.parse(now) + 3_600_000).toISOString(),
    lastFailureCode: null
  };
}

/** A blocked address is a configuration error; a failed discovery an upstream error. */
function discoveryStatus(code: string): number {
  return code === 'assistant-mcp-discovery-failed' ? 502 : 400;
}

function uuid(): string {
  return globalThis.crypto?.randomUUID?.() ?? 'xxxxxxxx-xxxx-4xxx-8xxx-xxxxxxxxxxxx'.replace(/x/g, () => Math.floor(Math.random() * 16).toString(16));
}

/** What a person's consent holds for: the server, its address and its authentication. */
function consentFingerprint(stored: StoredServer): string {
  return [stored.server.id, stored.server.url.trim(), stored.server.authMode, stored.server.authorization?.issuer ?? '', stored.server.oauth?.scopes ?? ''].join('\n');
}

/** The state of a server that was just added: never in headless runs unless the input says so. */
function newServerState(input: McpServerInput): Pick<McpServer, 'lastSyncAt' | 'lastSyncStatus' | 'headlessUse' | 'lastConnectAt' | 'lastConnectCode' | 'transport'> {
  return {
    lastSyncAt: null,
    lastSyncStatus: null,
    headlessUse: input.headlessUse ?? 'never',
    lastConnectAt: null,
    lastConnectCode: null,
    transport: input.transport ?? 'streamable-http'
  };
}

/**
 * `organization` needs a credential of the organization; `personal` is the forwarded token of the
 * person a run acts for.
 */
function fitsHeadlessUse(headlessUse: McpHeadlessUse, authMode: McpAuthMode, connectionScope: McpConnectionScope = 'organization'): boolean {
  const personal = authMode === 'forward-user-token' || (authMode === 'oauth' && connectionScope === 'user');
  return headlessUse === 'never'
    || (headlessUse === 'organization' && !personal)
    || (headlessUse === 'personal' && personal);
}

function validServer(input: McpServerInput | undefined): boolean {
  if (!input || typeof input.id !== 'string' || input.id.length > 40 || !mockDashCase.test(input.id) ||
    typeof input.displayName !== 'string' || input.displayName.trim().length === 0 ||
    !authModes.includes(input.authMode) || typeof input.isEnabled !== 'boolean') {
    return false;
  }
  if (input.headlessUse !== undefined && input.headlessUse !== null &&
    (!headlessUses.includes(input.headlessUse) || !fitsHeadlessUse(input.headlessUse, input.authMode, input.connectionScope))) {
    return false;
  }
  if (input.transport !== undefined && input.transport !== null && !transports.includes(input.transport)) {
    return false;
  }

  try {
    const url = new URL(input.url);
    return url.protocol === 'https:' || (url.protocol === 'http:' && url.hostname === 'localhost');
  } catch {
    return false;
  }
}

function toTool({ inputSchemaHash, ...tool }: McpTool & { inputSchemaHash: string }): McpTool {
  return tool;
}

function toTools(stored: StoredServer): McpTool[] {
  return [...stored.tools.values()].map(toTool);
}

function kebab(value: string): string {
  return value
    .replace(/([a-z0-9])([A-Z])/g, '$1-$2')
    .replace(/[^A-Za-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .toLowerCase();
}
