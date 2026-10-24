import { EnvironmentInjector, Injectable, inject, runInInjectionContext } from '@angular/core';
import { TranslateService } from '@ngx-translate/core';
import { Observable, Subscriber } from 'rxjs';
import { NhAssistantClientErrorCodes } from '../models/assistant-sse.models';
import { NH_ASSISTANT_CONFIG, NH_ASSISTANT_FETCH, NH_ASSISTANT_UPLOAD, NhAssistantConfig } from '../nh-assistant.config';

/**
 * The request header every upload carries. A browser cannot send it with a cross-site form, so the
 * upload endpoint keeps the posture of the JSON endpoints.
 */
export const NH_ASSISTANT_UPLOAD_HEADER = 'X-NH-Assistant-Upload';

/** A failed JSON request of the assistant API. Carries a stable code, never response text. */
export class NhAssistantApiError extends Error {
  constructor(
    readonly status: number,
    readonly code: string,
    readonly messageKey: string,
    /**
     * The field errors of the error body, whatever its code (for example `assistant-validation` or
     * `assistant-run-filter-invalid`): camelCase field names, dotted for nested settings such as
     * `oauth.clientId`, with stable values such as `required`, `invalid`, `too-long` or `not-found`.
     * Empty when the body names none.
     */
    readonly fieldErrors: Readonly<Record<string, readonly string[]>> = {}
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

/** A file the assistant API answered, as its text with the response's headers (internal). */
export interface NhAssistantFileResponse {
  text: string;
  headers: Headers;
}

const safeCode = /^[a-z0-9][a-z0-9.-]{0,79}$/;
const safeField = /^[a-zA-Z][a-zA-Z0-9.]{0,63}$/;
/** A field of one item of a list, such as `readers[0].toolName`. */
const safeItemField = /^[a-zA-Z][a-zA-Z0-9]{0,31}\[\d{1,4}\](\.[a-zA-Z][a-zA-Z0-9]{0,31})?$/;

/**
 * Whether a value the server or a callback page sent is a code the library may show: lowercase
 * letters, digits, dots and dashes, at most 80 characters. Every reader of untrusted codes uses this
 * one check, so a code never carries free text into a translation key.
 */
export function nhAssistantIsSafeCode(value: unknown): value is string {
  return typeof value === 'string' && safeCode.test(value);
}

/**
 * Shared transport of the assistant services: `fetch` with the host's token, the current
 * language and stable failure codes. Internal to the library.
 */
@Injectable()
export class NhAssistantTransport {
  private readonly config = inject(NH_ASSISTANT_CONFIG);
  private readonly fetchFn = inject(NH_ASSISTANT_FETCH);
  private readonly uploadFn = inject(NH_ASSISTANT_UPLOAD);
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

  /**
   * A cold request with a JSON body that answers the response's text and headers, for an endpoint that
   * answers a file with details in a header, such as the export of an agent package. It aborts on
   * unsubscribe and errors as `json` does: with the code and field errors of a refused request's error
   * body, the endpoint's status mapping without one, and `assistant-network` when the request did not
   * reach the server.
   */
  file(method: string, path: string, body: unknown, accept: string, statusCodes: NhAssistantStatusCodes = {}): Observable<NhAssistantFileResponse> {
    return new Observable<NhAssistantFileResponse>(subscriber => {
      const abort = new AbortController();

      void this.executeFile(method, path, body, accept, statusCodes, abort.signal, subscriber);

      return () => abort.abort();
    });
  }

  async fetch(method: string, path: string, accept: string, body: unknown, signal: AbortSignal): Promise<Response> {
    const init = await this.createInit(method, accept, body, signal);
    return this.fetchFn(this.url(path), init);
  }

  /**
   * Uploads `form` with progress and resolves with the parsed JSON answer. Rejects with
   * `NhAssistantApiError`, never with raw text: the code of a refused upload's error body, the
   * generic status mapping without one and `assistant-network` when it did not reach the server.
   * An aborted upload rejects with an `AbortError`.
   */
  async upload<T>(path: string, form: FormData, onProgress: (loaded: number, total: number) => void, signal: AbortSignal): Promise<T> {
    const headers = await this.headers('application/json');
    headers[NH_ASSISTANT_UPLOAD_HEADER] = '1';
    let response: Response;
    try {
      response = await this.uploadFn({ url: this.url(path), headers, body: form, signal, onProgress });
    } catch (error) {
      if (signal.aborted) {
        throw error;
      }
      throw createApiError(0, nhAssistantCodeForStatus(0));
    }

    if (!response.ok) {
      const failure = await this.failure(response, {});
      throw createApiError(response.status, failure.code, failure.fieldErrors);
    }

    try {
      return JSON.parse(await response.text()) as T;
    } catch {
      throw createApiError(response.status, NhAssistantClientErrorCodes.invalidResponse);
    }
  }

  /**
   * Downloads a file of the assistant API. Rejects with `NhAssistantApiError`, never with raw text:
   * the code and field errors of a refused request's error body (whatever its content type), the
   * endpoint's status mapping without one, `assistant-network` when the request did not reach the
   * server and `assistant-stream-interrupted` when the file broke off while it was read.
   */
  async download(path: string, accept: string, statusCodes: NhAssistantStatusCodes = {}): Promise<Blob> {
    let response: Response;
    try {
      response = await this.fetch('GET', path, accept, undefined, new AbortController().signal);
    } catch {
      throw createApiError(0, nhAssistantCodeForStatus(0));
    }

    if (!response.ok) {
      const failure = await this.failure(response, statusCodes);
      throw createApiError(response.status, failure.code, failure.fieldErrors);
    }

    try {
      return await response.blob();
    } catch {
      throw createApiError(response.status, NhAssistantClientErrorCodes.streamInterrupted);
    }
  }

  /**
   * The failure code of a response: a safe `code` field of a JSON error body wins, then the
   * endpoint's status mapping, then the generic mapping. Response text is never used.
   */
  async failureCode(response: Response, statusCodes: NhAssistantStatusCodes = {}): Promise<string> {
    return (await this.failure(response, statusCodes)).code;
  }

  /**
   * The failure code of a response and the field errors of its body, both checked against safe
   * patterns. Response text is never used.
   */
  private async failure(
    response: Response,
    statusCodes: NhAssistantStatusCodes
  ): Promise<{ code: string; fieldErrors: Record<string, string[]> }> {
    const fieldErrors: Record<string, string[]> = {};
    try {
      const text = await response.text();
      const body = text.length > 0 ? JSON.parse(text) as unknown : null;
      const record = body && typeof body === 'object' ? body as Record<string, unknown> : null;
      const errors = record?.['errors'];
      if (errors && typeof errors === 'object' && !Array.isArray(errors)) {
        for (const [field, values] of Object.entries(errors as Record<string, unknown>)) {
          const safe = Array.isArray(values) ? values.filter(nhAssistantIsSafeCode) : [];
          if ((safeField.test(field) || safeItemField.test(field)) && safe.length > 0) {
            fieldErrors[field] = safe;
          }
        }
      }
      const code = record?.['code'];
      if (nhAssistantIsSafeCode(code)) {
        return { code, fieldErrors };
      }
    } catch {
      // A non-JSON error body carries no code.
    }

    return { code: statusCodes[response.status] ?? nhAssistantCodeForStatus(response.status), fieldErrors };
  }

  private async executeFile(
    method: string,
    path: string,
    body: unknown,
    accept: string,
    statusCodes: NhAssistantStatusCodes,
    signal: AbortSignal,
    subscriber: Subscriber<NhAssistantFileResponse>
  ): Promise<void> {
    let response: Response;
    try {
      response = await this.fetch(method, path, accept, body, signal);
    } catch {
      if (!signal.aborted) {
        subscriber.error(createApiError(0, nhAssistantCodeForStatus(0)));
      }
      return;
    }

    if (!response.ok) {
      const failure = await this.failure(response, statusCodes);
      subscriber.error(createApiError(response.status, failure.code, failure.fieldErrors));
      return;
    }

    try {
      const text = await response.text();
      subscriber.next({ text, headers: response.headers });
      subscriber.complete();
    } catch {
      if (!signal.aborted) {
        subscriber.error(createApiError(response.status, NhAssistantClientErrorCodes.streamInterrupted));
      }
    }
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
      const failure = await this.failure(response, statusCodes);
      subscriber.error(createApiError(response.status, failure.code, failure.fieldErrors));
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
    const headers = await this.headers(accept);
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

  /** The host's extra headers, `Accept`, the bearer token and the current language. */
  private async headers(accept: string): Promise<Record<string, string>> {
    const headers: Record<string, string> = await nhAssistantRequestHeaders(this.config, this.injector);
    headers['Accept'] = accept;

    const token = await runInInjectionContext(this.injector, () => this.config.getAccessToken());
    if (token) {
      headers['Authorization'] = `Bearer ${token}`;
    }

    const language = this.translate?.getCurrentLang();
    if (language) {
      headers['Accept-Language'] = language;
    }

    return headers;
  }

  private url(path: string): string {
    const base = this.config.apiBaseUrl.replace(/\/+$/, '');
    return `${base}/${path}`;
  }
}

function createApiError(status: number, code: string, fieldErrors: Record<string, string[]> = {}): NhAssistantApiError {
  return new NhAssistantApiError(status, code, nhAssistantErrorMessageKey(code), fieldErrors);
}

const reservedRequestHeaders = new Set(['authorization', 'accept', 'accept-language', 'content-type']);

/**
 * The host's extra request headers from `NhAssistantConfig.getRequestHeaders`, without the
 * headers the library sets itself and without empty values.
 */
export async function nhAssistantRequestHeaders(
  config: NhAssistantConfig,
  injector: EnvironmentInjector
): Promise<Record<string, string>> {
  const headers: Record<string, string> = {};
  if (!config.getRequestHeaders) {
    return headers;
  }

  const extra = await runInInjectionContext(injector, () => config.getRequestHeaders!());
  for (const [name, value] of Object.entries(extra ?? {})) {
    if (typeof value === 'string' && value.length > 0 && !reservedRequestHeaders.has(name.toLowerCase())) {
      headers[name] = value;
    }
  }
  return headers;
}
