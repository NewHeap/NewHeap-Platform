import { ErrorHandler, Injectable, signal } from '@angular/core';

export interface SampleFrontendErrorObservation {
  caseId: 'SPM-159';
  captured: boolean;
  errorType: string;
  rawMessageStored: false;
}

@Injectable({ providedIn: 'root' })
export class SampleFrontendErrorState {
  private readonly observationSignal = signal<SampleFrontendErrorObservation | null>(null);

  readonly observation = this.observationSignal.asReadonly();

  record(error: unknown): void {
    this.observationSignal.set({
      caseId: 'SPM-159',
      captured: true,
      errorType: error instanceof Error ? error.name : 'UnknownError',
      rawMessageStored: false
    });
  }
}

@Injectable()
export class SampleFrontendErrorHandler implements ErrorHandler {
  constructor(private readonly state: SampleFrontendErrorState) {}

  handleError(error: unknown): void {
    this.state.record(error);
  }
}
