/*
 * Wire models of the assistant preference and administration endpoints.
 *
 * Literal translation of the assistant API contract (preferences and `admin/*`). Keep the
 * field names identical to the JSON the server returns.
 */

export type AssistantStyle = 'default' | 'direct' | 'personal' | 'detailed';

export type AssistantAddressForm = 'informal' | 'formal';

export type AssistantResponseLength = 'short' | 'normal' | 'long';

export interface AssistantPreferences {
  style: AssistantStyle;
  addressForm: AssistantAddressForm;
  responseLength: AssistantResponseLength;
  /** At most 1,000 characters. Steers style only; it never overrides the fixed rules. */
  customInstructions: string | null;
}

export interface ApplicationContext {
  text: string;
  version: number;
  hash: string;
  updatedAt: string;
  updatedBy: string | null;
}

export interface ApplicationContextVersion {
  version: number;
  hash: string;
  updatedAt: string;
  updatedBy: string | null;
}

/** Body of `PUT admin/context`. */
export interface UpdateApplicationContextRequest {
  text: string;
  expectedVersion: number;
}

export type ToolCatalogEffect = 'read-only' | 'idempotent-mutation' | 'mutation' | 'destructive';

export interface ToolCatalogEntry {
  id: string;
  source: 'local' | 'bridge' | 'mcp';
  effect: ToolCatalogEffect;
  description: string;
}

/** Equals `NhAiAutonomyLevel`. */
export type AssistantAutonomy = 'observe' | 'explain' | 'propose' | 'simulate' | 'execute';

export interface AdminAgentInput {
  id: string;
  displayName: string;
  description: string;
  instructions: string;
  toolSelectors: string[];
  mcpServerIds: string[];
  requiredPolicy: string | null;
  autonomy: AssistantAutonomy;
  isEnabled: boolean;
}

/** Body of `PUT admin/agents/{id}`. */
export type UpdateAdminAgentRequest = AdminAgentInput & { expectedVersion: number };

export interface AdminAgent extends AdminAgentInput {
  version: number;
  source: 'code' | 'admin';
  isOverridden: boolean;
  instructionsHash: string;
  updatedAt: string;
}

export type McpAuthMode = 'none' | 'bearer' | 'api-key' | 'forward-user-token';

export interface McpServerInput {
  id: string;
  displayName: string;
  url: string;
  authMode: McpAuthMode;
  headerName: string | null;
  /** Write-only. Omit to keep the stored secret, `""` to clear it. */
  secret?: string | null;
  requiredPolicy: string | null;
  isEnabled: boolean;
}

export interface McpServer {
  id: string;
  displayName: string;
  url: string;
  authMode: McpAuthMode;
  headerName: string | null;
  hasSecret: boolean;
  requiredPolicy: string | null;
  isEnabled: boolean;
  lastSyncAt: string | null;
  lastSyncStatus: 'ok' | 'failed' | null;
  assignedAgentIds: string[];
}

export interface McpServerTestResult {
  ok: boolean;
  code: string | null;
  toolCount: number | null;
}

export type McpToolEffect = 'read-only' | 'mutation';

export type McpToolStatus = 'available' | 'schema-changed' | 'missing';

export interface McpTool {
  remoteName: string;
  localId: string;
  description: string;
  descriptionOverride: string | null;
  isEnabled: boolean;
  effect: McpToolEffect;
  status: McpToolStatus;
  /** Remote annotation. Untrusted: shown as a hint only, never used as a default. */
  readOnlyHint: boolean | null;
}

/** Body of `PUT admin/mcp-servers/{id}/tools/{remoteName}`. */
export interface UpdateMcpToolRequest {
  isEnabled: boolean;
  effect: McpToolEffect;
  descriptionOverride: string | null;
}
