import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, computed, signal } from '@angular/core';

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
