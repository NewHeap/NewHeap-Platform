import {Injectable, OnDestroy} from "@angular/core";
import {NhCommonModuleConfig} from "../models/config.models";
import * as Sentry from "@sentry/angular";
import {Router} from "@angular/router";

@Injectable({
  providedIn: "root"
})
export class NhSentryTraceService implements OnDestroy {
  sentryTraceService: Sentry.TraceService | undefined;
  didInitialize: boolean = false;

  constructor(
    private readonly moduleConfig: NhCommonModuleConfig,
    private readonly router: Router
  ){
    this.initialize();
  }

  initialize() {
    if(!this.moduleConfig.errorLogging.sentry.tracingEnabled) {

      this.sentryTraceService?.ngOnDestroy();
      this.sentryTraceService = undefined;
      return;
    }

    if(this.didInitialize) {
      return;
    }

    if(!Sentry.isInitialized()) {
      Sentry.init(this.moduleConfig.errorLogging.sentry.options);
    }

    this.sentryTraceService = new Sentry.TraceService(this.router);
    this.didInitialize = true;
  }

  ngOnDestroy(): void {
    this.sentryTraceService?.ngOnDestroy();
    this.sentryTraceService = undefined;
  }
}
