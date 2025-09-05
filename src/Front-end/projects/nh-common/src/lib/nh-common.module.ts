import {
  ErrorHandler,
  inject,
  ModuleWithProviders,
  NgModule,
  Optional,
  provideAppInitializer,
  SkipSelf,
  Type
} from "@angular/core";
import * as Sentry from "@sentry/angular";
import {CommonModule} from "@angular/common";
import {
  HTTP_INTERCEPTORS,
  provideHttpClient,
  withFetch,
  withInterceptorsFromDi
} from "@angular/common/http";
import {
  IsAuthenticatedPipe,
  IsClaimGrantedPipe, IsGrantedRolePipe, IsOneActiveDivisionPermissionGrantedPipe, IsOneActiveDivisionRoleGrantedPipe,
  IsOneClaimGrantedPipe, IsOneDivisionPermissionGrantedPipe, IsOneDivisionRoleGrantedPipe,
  IsOnePermissionGrantedPipe, IsOneRoleGrantedPipe
} from "./pipes/auth.pipes";
import {NhDatePipe, NhDateTimePipe, NhDateUtcPipe} from "./pipes/date.pipes";
import {NhBooleanToStringPipe} from "./pipes/primitive-type.pipes";
import {NhUrlEncodePipe} from "./pipes/encode.pipes";
import {NhEncodeHttpParamsInterceptor} from "./interceptors/nh-encode-http-params.interceptor";
import {NhCommonModuleConfig} from "./models/config.models";
import {NhToHeadDirective} from "./directives/nh-to-head.directive";
import {NhButtonDebounceDirective, NhDebounceDirective} from "./directives/nh-debounce.directives";
import {NhModalComponent} from "./components/nh-modal/component";
import {NhModalContentDirective} from "./directives/nh-modal.directives";
import {NhLoaderComponent} from "./components/nh-loader/component";
import {NhTaskResultFormValidationService} from "./services/nh-task-result-form.validator";
import {NhJsonLdComponent} from "./components/nh-json-ld/nh-json-ld.component";
import {NhModalLoadingComponent} from "./components/nh-loading-modal/component";
import {NhModalConfirmComponent} from "./components/nh-confirm-modal/component";
import {TranslateModule} from "@ngx-translate/core";
import {SafeHtmlPipe, SafeResourceUrlPipe, SafeUrlPipe} from "./pipes/safe-html.pipes";
import {NhCookieService} from "./services/nh-cookie.service";
import {NhRouterLinkDirective} from "./directives/nh-router-link.directive";
import {NhServerHttpInterceptor} from "./interceptors/nh-server-http.interceptor";
import {NhCanCancelNavigationGuard} from "./guards/nh-cancel-navigation.guard";
import {NhInternetConnectionService} from "./services/nh-internet-connection.service";
import {NhConfigCommonService} from "./services/nh-config.service";
import {Observable} from "rxjs";
import {NhActiveDivisionInterceptor} from "./interceptors/nh-active-division.interceptor";
import {NhFormDropDownComponent} from "./components/nh-form-dropdown/form-dropdown.component";
import {NgxBootstrapMultiselectModule} from "ngx-bootstrap-multiselect";
import {FormsModule} from "@angular/forms";
import {NhServerSideFormValidationService} from "./services/nh-server-side-form-validator.service";
import {NhFormErrorMessageComponent} from "./components/nh-form-error-message/form-error-message.component";
import {NhApiAuthInterceptor} from "./interceptors/nh-api.auth.interceptor";
import {NhErrorComponent} from "./components/nh-error/component";
import {BaseNhAuthService, NhAuthService} from "./services/nh-auth.service";
import {INhAuthorization} from "./models/auth.models";
import {NH_ERROR_HANDLERS, NhErrorHandlerService} from "./services/nh-error-handler.service";
import {NhErrorHandlerSentryService} from "./services/nh-error-handler-sentry.service";
import {Router} from "@angular/router";
import {NhSentryTraceService} from "./services/nh-sentry-trace.service";


@NgModule({
  imports: [
    CommonModule,
    TranslateModule,
    NgxBootstrapMultiselectModule,
    FormsModule
  ],
  declarations: [
    // Components
    NhModalComponent,
    NhLoaderComponent,
    NhJsonLdComponent,
    NhModalLoadingComponent,
    NhModalConfirmComponent,
    NhFormDropDownComponent,
    NhFormErrorMessageComponent,
    NhErrorComponent,
    // Pipes
    NhDatePipe,
    NhDateTimePipe,
    NhDateUtcPipe,
    NhBooleanToStringPipe,
    IsAuthenticatedPipe,
    IsOnePermissionGrantedPipe,
    IsClaimGrantedPipe,
    IsOneClaimGrantedPipe,
    IsOneRoleGrantedPipe,
    IsGrantedRolePipe,
    IsOneDivisionPermissionGrantedPipe,
    IsOneDivisionRoleGrantedPipe,
    IsOneActiveDivisionPermissionGrantedPipe,
    IsOneActiveDivisionRoleGrantedPipe,
    NhUrlEncodePipe,
    SafeHtmlPipe,
    SafeUrlPipe,
    SafeResourceUrlPipe,
    // Directives
    NhToHeadDirective,
    NhDebounceDirective,
    NhButtonDebounceDirective,
    NhModalContentDirective,
    NhRouterLinkDirective
  ],
  exports: [
    // Components
    NhModalComponent,
    NhLoaderComponent,
    NhJsonLdComponent,
    NhModalLoadingComponent,
    NhModalConfirmComponent,
    NhFormDropDownComponent,
    NhFormErrorMessageComponent,
    NhErrorComponent,
    // Pipes
    NhDatePipe,
    NhDateTimePipe,
    NhDateUtcPipe,
    NhBooleanToStringPipe,
    IsAuthenticatedPipe,
    IsOnePermissionGrantedPipe,
    IsClaimGrantedPipe,
    IsOneClaimGrantedPipe,
    IsOneRoleGrantedPipe,
    IsOneDivisionPermissionGrantedPipe,
    IsOneDivisionRoleGrantedPipe,
    IsOneActiveDivisionPermissionGrantedPipe,
    IsOneActiveDivisionRoleGrantedPipe,
    IsGrantedRolePipe,
    NhUrlEncodePipe,
    SafeHtmlPipe,
    SafeUrlPipe,
    SafeResourceUrlPipe,
    // Directives
    NhToHeadDirective,
    NhDebounceDirective,
    NhButtonDebounceDirective,
    NhModalContentDirective,
    NhRouterLinkDirective
  ],
  providers: [
    provideAppInitializer(() => {
      const configService = inject(NhConfigCommonService);
      const nhSentryTraceService = inject(NhSentryTraceService);
      return new Observable<unknown>((observer) => {
        //
        // We use APP_INITIALIZER to load the configuration before the application starts. (Cuz DEPS calls for AppConfigService it is loaded soon in the lifecycle of the app.)
        //
        configService.initialize().then(() => {
          observer.next({});
          observer.complete();
        }, (err) => {
          observer.error(err);
          observer.complete();
        });
      });
    }),
    // Interceptors
    {
      provide: HTTP_INTERCEPTORS,
      useClass: NhActiveDivisionInterceptor,
      multi: true
    },
    {
      provide: HTTP_INTERCEPTORS,
      useClass: NhApiAuthInterceptor,
      multi: true
    },
    {
      provide: HTTP_INTERCEPTORS,
      useClass: NhEncodeHttpParamsInterceptor,
      multi: true
    },
    {
      provide: HTTP_INTERCEPTORS,
      useClass: NhServerHttpInterceptor,
      multi: true
    },
    // Tracing
    {
      provide: Sentry.TraceService,
      useFactory: (nhSentryTraceService: NhSentryTraceService) => {
        return nhSentryTraceService.sentryTraceService;
      },
      deps: [NhSentryTraceService]
    },
    //Error handlers
    {
      provide: ErrorHandler, // Make support for multiple error handlers
      useExisting: NhErrorHandlerService
    },
    {
      provide: NH_ERROR_HANDLERS, // Register the default
      useClass: ErrorHandler,
      multi: true,
    },
    {
      provide: NH_ERROR_HANDLERS, // Register Sentry error handler
      useClass: NhErrorHandlerSentryService,
      multi: true,
    },
    provideHttpClient(withInterceptorsFromDi(), withFetch()),
    // Guards
    NhCanCancelNavigationGuard,
    // Services
    NhTaskResultFormValidationService,
    NhCookieService,
    NhInternetConnectionService,
    NhServerSideFormValidationService
  ]
})
export class NhCommonModule {
  constructor(@Optional() @SkipSelf() parentModule: NhCommonModule) {
    // Import guard
    if (parentModule) {
    }
  }

  static forRoot<TAuthorization extends INhAuthorization, TAuthService extends BaseNhAuthService<TAuthorization>>(config: NhCommonModuleConfig, authService: Type<TAuthService>): ModuleWithProviders<NhCommonModule> {
    return {
      ngModule: NhCommonModule,
      providers: [
        {provide: NhCommonModuleConfig, useValue: config},
        {provide: NhAuthService, useExisting: authService},
      ]
    };
  }
}

