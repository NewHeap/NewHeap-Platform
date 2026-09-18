import { EnvironmentInjector, Injectable, inject, runInInjectionContext } from '@angular/core';
import { TranslateService } from '@ngx-translate/core';
import { Observable, Subscriber } from 'rxjs';
import { NhAssistantClientErrorCodes } from '../models/assistant-sse.models';
import { NH_ASSISTANT_CONFIG, NH_ASSISTANT_FETCH } from '../nh-assistant.config';

/** A failed JSON request of the assistant API. Carries a stable code, never response text. */
export class NhAssistantApiError extends Error {
  constructor(
    readonly status: number,
    readonly code: string,
    readonly messageKey: string
  ) {
    super(code);
    this.name = 'NhAssistantApiError';
  }
}

/** Translation key that belongs to a failure code. */
export function nhAssistantErrorMessageKey(code: string): string {
  return `nh-assistant.errors.${code}`;
}

/** Maps an HTTP status of the assistant API to a stable client failure code. */
export function nhAssistantCodeForStatus(status: number): string {
  if (status === 0) {
    return NhAssistantClientErrorCodes.network;
  }
  if (status === 400) {
    return NhAssistantClientErrorCodes.validation;
  }
  if (status === 401) {
    return NhAssistantClientErrorCodes.unauthenticated;
  }
  if (status === 403) {
    return NhAssistantClientErrorCodes.forbidden;
  }
  if (status === 404) {
    return NhAssistantClientErrorCodes.notFound;
  }
  if (status === 409) {
    return NhAssistantClientErrorCodes.conversationBusy;
  }

  return NhAssistantClientErrorCodes.server;
}

/** Status-specific codes that replace the generic mapping for one endpoint. */
export type NhAssistantStatusCodes = Partial<Record<number, string>>;

const safeCode = /^[a-z0-9][a-z0-9.-]{0,79}$/;

/**
 * Shared transport of the assistant services: `fetch` with the host's token, the current
 * language and stable failure codes. Internal to the library.
 */
@Injectable()
export class NhAssistantTransport {
  private readonly config = inject(NH_ASSISTANT_CONFIG);
  private readonly fetchFn = inject(NH_ASSISTANT_FETCH);
  private readonly translate = inject(TranslateService, { optional: true });
  private readonly injector = inject(EnvironmentInjector);

  /** A cold JSON request that aborts on unsubscribe and errors with `NhAssistantApiError`. */
  json<T>(method: string, path: string, body?: unknown, statusCodes: NhAssistantStatusCodes = {}): Observable<T> {
    return new Observable<T>(subscriber => {
      const abort = new AbortController();

      void this.executeJson<T>(method, path, body, statusCodes, abort.signal, subscriber);

      return () => abort.abort();
    });
  }

  async fetch(method: string, path: string, accept: string, body: unknown, signal: AbortSignal): Promise<Response> {
    const init = await this.createInit(method, accept, body, signal);
    return this.fetchFn(this.url(path), init);
  }

  /**
   * The failure code of a response: a safe `code` field of a JSON error body wins, then the
   * endpoint's status mapping, then the generic mapping. Response text is never used.
   */
  async failureCode(response: Response, statusCodes: NhAssistantStatusCodes = {}): Promise<string> {
    try {
      const text = await response.text();
      const body = text.length > 0 ? JSON.parse(text) as unknown : null;
      const code = body && typeof body === 'object' ? (body as Record<string, unknown>)['code'] : null;
      if (typeof code === 'string' && safeCode.test(code)) {
        return code;
      }
    } catch {
      // A non-JSON error body carries no code.
    }

    return statusCodes[response.status] ?? nhAssistantCodeForStatus(response.status);
  }

  private async executeJson<T>(
    method: string,
    path: string,
    body: unknown,
    statusCodes: NhAssistantStatusCodes,
    signal: AbortSignal,
    subscriber: Subscriber<T>
  ): Promise<void> {
    let response: Response;
    try {
      response = await this.fetch(method, path, 'application/json', body, signal);
    } catch {
      if (!signal.aborted) {
        subscriber.error(createApiError(0, nhAssistantCodeForStatus(0)));
      }
      return;
    }

    if (!response.ok) {
      const code = await this.failureCode(response, statusCodes);
      subscriber.error(createApiError(response.status, code));
      return;
    }

    try {
      const text = response.status === 204 ? '' : await response.text();
      subscriber.next((text.length > 0 ? JSON.parse(text) : undefined) as T);
      subscriber.complete();
    } catch {
      if (!signal.aborted) {
        subscriber.error(createApiError(response.status, NhAssistantClientErrorCodes.invalidResponse));
      }
    }
  }

  private async createInit(method: string, accept: string, body: unknown, signal: AbortSignal): Promise<RequestInit> {
    const headers: Record<string, string> = { Accept: accept };

    const token = await runInInjectionContext(this.injector, () => this.config.getAccessToken());
    if (token) {
      headers['Authorization'] = `Bearer ${token}`;
    }

    const language = this.translate?.getCurrentLang();
    if (language) {
      headers['Accept-Language'] = language;
    }

    if (body !== undefined) {
      headers['Content-Type'] = 'application/json';
    }

    return {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
      signal,
      cache: 'no-store'
    };
  }

  private url(path: string): string {
    const base = this.config.apiBaseUrl.replace(/\/+$/, '');
    return `${base}/${path}`;
  }
}

function createApiError(status: number, code: string): NhAssistantApiError {
  return new NhAssistantApiError(status, code, nhAssistantErrorMessageKey(code));
}
