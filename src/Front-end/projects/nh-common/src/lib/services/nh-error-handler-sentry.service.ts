import {ErrorHandler, Injectable, OnDestroy} from "@angular/core";
import {NhCommonModuleConfig} from "../models/config.models";
import * as Sentry from "@sentry/angular";

@Injectable({
  providedIn: "root"
})
export class NhErrorHandlerSentryService implements ErrorHandler, OnDestroy {
  sentryErrorHandler: Sentry.SentryErrorHandler | undefined;
  didInitialize: boolean = false;

  constructor(
    private readonly moduleConfig: NhCommonModuleConfig
  ){

  }

  initialize() {
    if(!this.moduleConfig.errorLogging.sentry.errorLoggingEnabled) {
      this.sentryErrorHandler?.ngOnDestroy();
      this.sentryErrorHandler = undefined;
      return;
    }

    if(this.didInitialize) {
      return;
    }

    if(!Sentry.isInitialized()) {
      Sentry.init(this.moduleConfig.errorLogging.sentry.options);
    }

    this.sentryErrorHandler = Sentry.createErrorHandler(this.moduleConfig.errorLogging.sentry.errorHandlerOptions);

    this.didInitialize = true;
  }

  handleError(error: any): void {
    this.initialize();

    if(!this.moduleConfig.errorLogging.sentry.errorLoggingEnabled) {
      return;
    }

    this.sentryErrorHandler?.handleError(error);
  }

  ngOnDestroy(): void {
    this.sentryErrorHandler?.ngOnDestroy();
    this.sentryErrorHandler = undefined;
  }
}
