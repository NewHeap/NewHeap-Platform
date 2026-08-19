import {ErrorHandler, Injectable, OnDestroy} from "@angular/core";
import {NhCommonModuleConfig} from "../models/config.models";
import * as Sentry from "@sentry/angular";
import {NhSentryInitializerService} from "./nh-sentry.service";

@Injectable({
  providedIn: "root"
})
export class NhErrorHandlerSentryService implements ErrorHandler, OnDestroy {
  private _sentryErrorHandler: Sentry.SentryErrorHandler | undefined;
  private _didInitialize: boolean = false;

  get sentryErrorHandler() {
    return this._sentryErrorHandler;
  }


  constructor(
    private readonly moduleConfig: NhCommonModuleConfig,
    private readonly sentryInitializerService: NhSentryInitializerService
  ){
    this.initialize();
  }

  initialize() {
    if(!this.moduleConfig.errorLogging.sentry.errorLoggingEnabled) {
      this._sentryErrorHandler?.ngOnDestroy();
      this._sentryErrorHandler = undefined;
      return;
    }

    if(this._didInitialize) {
      return;
    }

    this.sentryInitializerService.initialize();

    this._sentryErrorHandler = Sentry.createErrorHandler(this.moduleConfig.errorLogging.sentry.errorHandlerOptions);

    this._didInitialize = true;
  }

  handleError(error: any): void {
    this.initialize();

    if(!this.moduleConfig.errorLogging.sentry.errorLoggingEnabled) {
      return;
    }

    this._sentryErrorHandler?.handleError(error);
  }

  ngOnDestroy(): void {
    this._sentryErrorHandler?.ngOnDestroy();
    this._sentryErrorHandler = undefined;
  }
}
