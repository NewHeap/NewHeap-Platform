import {ErrorHandler, Injectable, InjectionToken, Injector} from "@angular/core";

export const NH_ERROR_HANDLERS = new InjectionToken<ErrorHandler[]>('NH_ERROR_HANDLERS');

@Injectable({providedIn: 'root'})
export class NhErrorHandlerService implements ErrorHandler {
  constructor(private injector: Injector){}

  handleError(error: any): void {
    const handlers = this.injector.get(NH_ERROR_HANDLERS, []);
    for (const handler of handlers) {
      try {
        handler.handleError(error);
      } catch {
        // A failing telemetry provider must not prevent the remaining handlers
        // from observing the original application error.
      }
    }
  }
}
