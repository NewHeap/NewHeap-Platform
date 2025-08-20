import {Inject, Injectable, InjectionToken, OnDestroy, Optional} from "@angular/core";
import {NhCommonModuleConfig, NhSentryErrorLoggingNhCommonModuleConfig} from "../models/config.models";
import * as Sentry from "@sentry/angular";
import * as SentryCore from "@sentry/core";
import {Subscription} from "rxjs";
import {NhAuthorization} from "../models/auth.models";
import {NhSentryTraceService} from "./nh-sentry-trace.service";
import {NhErrorHandlerSentryService} from "./nh-error-handler-sentry.service";
import {NhAuthService} from "./nh-auth.service";

export type NhSentryBeforeSendHook = (event: Sentry.ErrorEvent, hint: Sentry.EventHint) => Sentry.ErrorEvent | null | PromiseLike<Sentry.ErrorEvent | null>;
export type NhSentryBeforeSendLogHook = (event: SentryCore.Log) => SentryCore.Log | null;
export type NhSentryBeforeSendSpanHook = (span: SentryCore.SpanJSON) => SentryCore.SpanJSON;
export type NhSentryBeforeSendTransactionHook = (span: SentryCore.TransactionEvent, hint: Sentry.EventHint) => SentryCore.TransactionEvent | null | PromiseLike<SentryCore.TransactionEvent | null>;
export type NhSentryBeforeBreadcrumbHook = (span: Sentry.Breadcrumb, hint?: Sentry.BreadcrumbHint) => Sentry.Breadcrumb | null;

export const NH_SENTRY_BEFORE_SEND_HOOKS = new InjectionToken<NhSentryBeforeSendHook[]>("NH_SENTRY_BEFORE_SEND_HOOKS");
export const NH_SENTRY_BEFORE_SEND_LOG_HOOKS = new InjectionToken<NhSentryBeforeSendLogHook[]>("NH_SENTRY_BEFORE_SEND_LOG_HOOKS");
export const NH_SENTRY_BEFORE_SEND_SPAN_HOOKS = new InjectionToken<NhSentryBeforeSendSpanHook[]>("NH_SENTRY_BEFORE_SEND_SPAN_HOOKS");
export const NH_SENTRY_BEFORE_SEND_TRANSACTION_HOOKS = new InjectionToken<NhSentryBeforeSendTransactionHook[]>("NH_SENTRY_BEFORE_SEND_TRANSACTION_HOOKS");
export const NH_SENTRY_BEFORE_BREADCRUMB_HOOKS = new InjectionToken<NhSentryBeforeBreadcrumbHook[]>("NH_SENTRY_BEFORE_BREADCRUMB_HOOKS");

@Injectable({
  providedIn: "root"
})
export class NhSentryInitializerService implements OnDestroy {
  private _didInitialize: boolean = false;
  private _sentryConfig: NhSentryErrorLoggingNhCommonModuleConfig;
  private authorization: NhAuthorization | undefined;
  private $auth: Subscription;

  private runtimeHooksBeforeSend: NhSentryBeforeSendHook[] = [];
  private runtimeHooksBeforeSendLog: NhSentryBeforeSendLogHook[] = [];
  private runtimeHooksBeforeSendSpan: NhSentryBeforeSendSpanHook[] = [];
  private runtimeHooksBeforeSendTransaction: NhSentryBeforeSendTransactionHook[] = [];
  private runtimeHooksBeforeBreadcrumb: NhSentryBeforeBreadcrumbHook[] = [];

  get hooksBeforeSend() {
    return [...this.providedHooksBeforeSend, ...this.runtimeHooksBeforeSend];
  }

  get hooksBeforeSendLog() {
    return [...this.providedHooksBeforeSendLog, ...this.runtimeHooksBeforeSendLog];
  }

  get hooksBeforeSendSpan() {
    return [...this.providedHooksBeforeSendSpan, ...this.runtimeHooksBeforeSendSpan];
  }

  get hooksBeforeSendTransaction() {
    return [...this.providedHooksBeforeSendTransaction, ...this.runtimeHooksBeforeSendTransaction];
  }

  get hooksBeforeBreadcrumb() {
    return [...this.providedHooksBeforeBreadcrumb, ...this.runtimeHooksBeforeBreadcrumb];
  }

  get isEnabled() {
    return this._sentryConfig.errorLoggingEnabled || this._sentryConfig.tracingEnabled;
  }

  get isInitializedAndEnabled() {
    return this._didInitialize && this.isEnabled;
  }

  get sentry() {
    return Sentry;
  }

  constructor(
    private readonly moduleConfig: NhCommonModuleConfig,
    private readonly authService: NhAuthService,
    @Optional() @Inject(NH_SENTRY_BEFORE_SEND_HOOKS)  private providedHooksBeforeSend: NhSentryBeforeSendHook[] = [],
    @Optional() @Inject(NH_SENTRY_BEFORE_SEND_LOG_HOOKS)  private providedHooksBeforeSendLog: NhSentryBeforeSendLogHook[] = [],
    @Optional() @Inject(NH_SENTRY_BEFORE_SEND_SPAN_HOOKS)  private providedHooksBeforeSendSpan: NhSentryBeforeSendSpanHook[] = [],
    @Optional() @Inject(NH_SENTRY_BEFORE_SEND_TRANSACTION_HOOKS)  private providedHooksBeforeSendTransaction: NhSentryBeforeSendTransactionHook[] = [],
    @Optional() @Inject(NH_SENTRY_BEFORE_BREADCRUMB_HOOKS)  private providedHooksBeforeBreadcrumb: NhSentryBeforeBreadcrumbHook[] = [],
  ){
    this._sentryConfig = this.moduleConfig.errorLogging.sentry;
    this.$auth = this.authService.authSubject.subscribe(async (authorization) => {
      await this.authChanged(authorization);
    });
    this.initialize();
  }

  private async authChanged(authorization: NhAuthorization | undefined) {
    this.authorization = authorization;

    if(!this.isInitializedAndEnabled) {
      return;
    }
  }

  registerHookBeforeSend(hook: NhSentryBeforeSendHook) {
    this.runtimeHooksBeforeSend.push(hook);
  }

  registerHookBeforeSendLog(hook: NhSentryBeforeSendLogHook) {
    this.runtimeHooksBeforeSendLog.push(hook);
  }

  registerHookBeforeSendSpan(hook: NhSentryBeforeSendSpanHook) {
    this.runtimeHooksBeforeSendSpan.push(hook);
  }

  registerHookBeforeSendTransaction(hook: NhSentryBeforeSendTransactionHook) {
    this.runtimeHooksBeforeSendTransaction.push(hook);
  }

  registerHookBeforeBreadcrumb(hook: NhSentryBeforeBreadcrumbHook) {
    this.runtimeHooksBeforeBreadcrumb.push(hook);
  }

  private async ourBeforeSendHook(event: Sentry.ErrorEvent, hint: Sentry.EventHint): Promise<Sentry.ErrorEvent | null> {

    if(this._sentryConfig.beforeSendAddAuthServiceInformation
      && this.authorization
    ) {
      const user = <any>event.user ?? {};

      user.id = this.authorization.user!.id!,
      user.email = this.authorization.user!.email!,
      user.username = this.authorization.user!.email!,
      user.roles = (this.authorization.user!.roles ?? []).join(', ');

      if(this.authorization.activeDivision) {
        user.activeDivisionId = this.authorization.activeDivision!.id!;
        user.activeDivisionName = this.authorization.activeDivision!.name!;
      }

      event.user = user;
    }

    return event;
  }

  initialize() {
    if(this._didInitialize) {
      return;
    }

    this.registerHookBeforeSend(this.ourBeforeSendHook.bind(this));

    if(!this.isEnabled) {
      if(!Sentry.isInitialized()) {
        const options = this.moduleConfig.errorLogging.sentry.options;
        Sentry.init({
          ...options,
          beforeSend: (event, hint) => this.runHooksBeforeSend(event, hint),
          beforeSendLog: (event) => this.runHooksBeforeSendLog(event),
          beforeSendSpan: (event) => this.runHooksBeforeSendSpan(event),
          beforeSendTransaction: (event, hint) => this.runHooksBeforeSendTransaction(event, hint),
          beforeBreadcrumb: (event, hint) => this.runHooksBeforeBreadcrumb(event, hint),
        });
      }
    }

    this._didInitialize = true;
  }

  private async runHooksBeforeSend(
    initialEvent: Sentry.ErrorEvent,
    hint: Sentry.EventHint
  ): Promise<Sentry.ErrorEvent | null> {
    const all = [...this.providedHooksBeforeSend, ...this.runtimeHooksBeforeSend];

    let current: Sentry.ErrorEvent | null = initialEvent;

    for (const hook of all) {
      if (!current) return null;
      current = await hook(current, hint);
    }

    return current;
  }

  private runHooksBeforeSendLog(
    initialEvent: SentryCore.Log
  ): SentryCore.Log | null {
    const all = [...this.providedHooksBeforeSendLog, ...this.runtimeHooksBeforeSendLog];

    let current: SentryCore.Log | null = initialEvent;

    for (const hook of all) {
      if (!current) return null;
      current = hook(current);
    }

    return current;
  }

  private runHooksBeforeSendSpan(
    initialSpan: SentryCore.SpanJSON
  ): SentryCore.SpanJSON {
    const all = [...this.providedHooksBeforeSendSpan, ...this.runtimeHooksBeforeSendSpan];

    let current: SentryCore.SpanJSON = initialSpan;

    for (const hook of all) {
      current = hook(current);
    }

    return current;
  }

  private async runHooksBeforeSendTransaction(
    initialEvent: SentryCore.TransactionEvent,
    hint: Sentry.EventHint
  ): Promise<SentryCore.TransactionEvent | null> {
    const all = [...this.providedHooksBeforeSendTransaction, ...this.runtimeHooksBeforeSendTransaction];

    let current: SentryCore.TransactionEvent | null = initialEvent;

    for (const hook of all) {
      if (!current) return null;
      current = await hook(current, hint);
    }

    return current;
  }

  private runHooksBeforeBreadcrumb(
    initialBreadcrumb: Sentry.Breadcrumb,
    hint?: Sentry.BreadcrumbHint
  ): Sentry.Breadcrumb | null {
    const all = [...this.providedHooksBeforeBreadcrumb, ...this.runtimeHooksBeforeBreadcrumb];

    let current: Sentry.Breadcrumb | null = initialBreadcrumb;

    for (const hook of all) {
      if (!current) return null;
      current = hook(current, hint);
    }

    return current;
  }

  ngOnDestroy(): void {
    this.$auth?.unsubscribe();
  }
}

@Injectable({
  providedIn: "root"
})
export class NhSentryService implements OnDestroy {
  private _sentryConfig: NhSentryErrorLoggingNhCommonModuleConfig;

  get isEnabled() {
    return this.sentryInitializerService.isEnabled;
  }

  get isInitializedAndEnabled() {
    return this.sentryInitializerService.isInitializedAndEnabled;
  }

  get sentry() {
    return Sentry;
  }

  constructor(
    private readonly moduleConfig: NhCommonModuleConfig,
    private readonly sentryInitializerService: NhSentryInitializerService
  ){
    this._sentryConfig = this.moduleConfig.errorLogging.sentry;
  }

  registerHookBeforeSend(hook: NhSentryBeforeSendHook) {
    this.sentryInitializerService.registerHookBeforeSend(hook);
  }

  registerHookBeforeSendLog(hook: NhSentryBeforeSendLogHook) {
    this.sentryInitializerService.registerHookBeforeSendLog(hook);
  }

  registerHookBeforeSendSpan(hook: NhSentryBeforeSendSpanHook) {
    this.sentryInitializerService.registerHookBeforeSendSpan(hook);
  }

  registerHookBeforeSendTransaction(hook: NhSentryBeforeSendTransactionHook) {
    this.sentryInitializerService.registerHookBeforeSendTransaction(hook);
  }

  registerHookBeforeBreadcrumb(hook: NhSentryBeforeBreadcrumbHook) {
    this.sentryInitializerService.registerHookBeforeBreadcrumb(hook);
  }

  ngOnDestroy(): void {
  }
}
