import { EnvironmentInjector, Injectable, inject, runInInjectionContext } from '@angular/core';
import { NH_ASSISTANT_CONFIG } from '../nh-assistant.config';

interface AssistantUiState {
  agentId: string | null;
  conversationId: string | null;
  panelOpen: boolean;
}

const emptyState: AssistantUiState = { agentId: null, conversationId: null, panelOpen: false };
const storagePrefix = 'nh-assistant-ui:v1:';

/** Browser-only UI pointers. Messages, drafts, tokens and page context are never stored. */
@Injectable()
export class NhAssistantUiState {
  private readonly config = inject(NH_ASSISTANT_CONFIG);
  private readonly injector = inject(EnvironmentInjector);
  private key: string | null = null;
  private state: AssistantUiState = { ...emptyState };
  private activated = false;
  private activationRevision = 0;

  async activate(): Promise<{ changed: boolean; state: Readonly<AssistantUiState> }> {
    const revision = ++this.activationRevision;
    let scope: string | null = null;
    try {
      scope = await runInInjectionContext(this.injector, () => this.config.getStateScope?.() ?? null);
    } catch {
      scope = null;
    }

    if (revision !== this.activationRevision) {
      return { changed: false, state: this.state };
    }

    const normalized = typeof scope === 'string' ? scope.trim() : '';
    const key = normalized.length > 0 && normalized.length <= 256
      ? `${storagePrefix}${encodeURIComponent(this.config.apiBaseUrl)}:${encodeURIComponent(normalized)}`
      : null;
    if (this.activated && key === this.key) {
      return { changed: false, state: this.state };
    }

    this.activated = true;
    this.key = key;
    this.state = this.read();
    return { changed: true, state: this.state };
  }

  deactivate(): void {
    this.activationRevision++;
    this.activated = false;
    this.key = null;
    this.state = { ...emptyState };
  }

  update(patch: Partial<AssistantUiState>): void {
    this.state = { ...this.state, ...patch };
    if (!this.key) {
      return;
    }

    try {
      globalThis.localStorage?.setItem(this.key, JSON.stringify(this.state));
    } catch {
      // Storage can be unavailable or full; the live assistant remains usable.
    }
  }

  private read(): AssistantUiState {
    if (!this.key) {
      return { ...emptyState };
    }

    try {
      const raw = globalThis.localStorage?.getItem(this.key);
      const value: unknown = raw ? JSON.parse(raw) : null;
      if (!value || typeof value !== 'object' || Array.isArray(value)) {
        return { ...emptyState };
      }

      const saved = value as Record<string, unknown>;
      return {
        agentId: validId(saved['agentId']),
        conversationId: validId(saved['conversationId']),
        panelOpen: saved['panelOpen'] === true
      };
    } catch {
      return { ...emptyState };
    }
  }
}

function validId(value: unknown): string | null {
  return typeof value === 'string' && value.length > 0 && value.length <= 256 ? value : null;
}
