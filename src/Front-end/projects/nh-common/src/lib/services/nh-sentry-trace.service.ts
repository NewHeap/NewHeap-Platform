import {Injectable, OnDestroy} from "@angular/core";
import {NhCommonModuleConfig} from "../models/config.models";
import * as Sentry from "@sentry/angular";
import {Router} from "@angular/router";
import {NhSentryInitializerService} from "./nh-sentry.service";

@Injectable({
  providedIn: "root"
})
export class NhSentryTraceService implements OnDestroy {
  private _sentryTraceService: Sentry.TraceService | undefined;
  private _didInitialize: boolean = false;

  get sentryTraceService() {
    return this._sentryTraceService;
  }

  constructor(
    private readonly moduleConfig: NhCommonModuleConfig,
    private readonly sentryInitializerService: NhSentryInitializerService,
    private readonly router: Router
  ){
    this.initialize();
  }

  initialize() {
    if(!this.moduleConfig.errorLogging.sentry.tracingEnabled) {

      this._sentryTraceService?.ngOnDestroy();
      this._sentryTraceService = undefined;
      return;
    }

    if(this._didInitialize) {
      return;
    }

    this.sentryInitializerService.initialize();

    this._sentryTraceService = new Sentry.TraceService(this.router);
    this._didInitialize = true;
  }

  ngOnDestroy(): void {
    this._sentryTraceService?.ngOnDestroy();
    this._sentryTraceService = undefined;
  }
}
