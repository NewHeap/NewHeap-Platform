import {
  HttpErrorResponse,
  HttpEvent,
  HttpHandler,
  HttpInterceptor,
  HttpRequest,
  HttpResponse
} from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable, tap } from 'rxjs';

/** The path every request to the sample API starts with. */
export const SAMPLE_API_BASE_URL = '/api';

export type SampleApiConnectionMode = 'checking' | 'connected' | 'offline' | 'failed';

@Injectable({ providedIn: 'root' })
export class SampleApiConnectionStateService {
  readonly mode = signal<SampleApiConnectionMode>('checking');
  readonly connected = computed(() => this.mode() === 'connected');
  readonly demoMode = computed(() => this.mode() === 'offline');
  readonly labelKey = computed(() => {
    switch (this.mode()) {
      case 'connected':
        return 'project.api-connected';
      case 'offline':
        return 'project.demo-data';
      case 'failed':
        return 'project.api-error';
      default:
        return 'project.api-checking';
    }
  });

  markConnected(): void {
    this.mode.set('connected');
  }

  markFailure(error: unknown): void {
    if (error instanceof HttpErrorResponse) {
      // Any HTTP response proves that the API is reachable. Only status 0 is
      // the browser's network/offline failure and may enable local demo mode.
      this.mode.set(error.status === 0 ? 'offline' : 'connected');
      return;
    }

    this.mode.set('failed');
  }
}

/**
 * Marks the API as connected when any request to it gets an answer, so the header's connection state
 * is right on pages that load no projects. Only the project pages decide on offline demo mode.
 */
@Injectable()
export class SampleApiConnectionInterceptor implements HttpInterceptor {
  private readonly state = inject(SampleApiConnectionStateService);

  intercept(request: HttpRequest<unknown>, next: HttpHandler): Observable<HttpEvent<unknown>> {
    if (!isSampleApiRequest(request.url)) {
      return next.handle(request);
    }

    return next.handle(request).pipe(tap({
      next: event => {
        if (event instanceof HttpResponse) {
          this.state.markConnected();
        }
      },
      error: (error: unknown) => {
        // An HTTP status proves the API is reachable; status 0 is left to the project pages.
        if (error instanceof HttpErrorResponse && error.status !== 0) {
          this.state.markConnected();
        }
      }
    }));
  }
}

function isSampleApiRequest(url: string): boolean {
  const target = new URL(url, window.location.origin);
  return target.origin === window.location.origin
    && (target.pathname === SAMPLE_API_BASE_URL || target.pathname.startsWith(SAMPLE_API_BASE_URL + '/'));
}
