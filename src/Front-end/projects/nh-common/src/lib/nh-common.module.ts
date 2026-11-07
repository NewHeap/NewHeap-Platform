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
import {CommonModule} from "@angular/common";
import {
  HTTP_INTERCEPTORS,
  provideHttpClient,
  withFetch,
  withInterceptorsFromDi
} from "@angular/common/http";
import {
  IsAuthenticatedPipe,
  IsAllPermissionsGrantedPipe,
  IsClaimGrantedPipe, IsGrantedRolePipe, IsOneActiveDivisionPermissionGrantedPipe, IsOneActiveDivisionRoleGrantedPipe,
  IsOneClaimGrantedPipe, IsOneDivisionPermissionGrantedPipe, IsOneDivisionRoleGrantedPipe,
  IsOnePermissionGrantedPipe, IsOneRoleGrantedPipe
} from "./pipes/auth.pipes";
import {NhDatePipe, NhDateTimePipe, NhDateUtcPipe, NhRelativeTimePipe} from "./pipes/date.pipes";
import {NhBooleanToStringPipe} from "./pipes/primitive-type.pipes";
import {NhUrlEncodePipe} from "./pipes/encode.pipes";
import {NhEncodeHttpParamsInterceptor} from "./interceptors/nh-encode-http-params.interceptor";
import {NhCommonModuleConfig} from "./models/config.models";
import {NhToHeadDirective} from "./directives/nh-to-head.directive";
import {NhButtonDebounceDirective, NhDebounceDirective} from "./directives/nh-debounce.directives";
import {NhModalComponent} from "./components/nh-modal/component";
import {NhModalContentDirective} from "./directives/nh-modal.directives";
import {NhLoaderComponent} from "./components/nh-loader/component";
import {NhJsonLdComponent} from "./components/nh-json-ld/nh-json-ld.component";
import {NhModalLoadingComponent} from "./components/nh-loading-modal/component";
import {NhModalConfirmComponent} from "./components/nh-confirm-modal/component";
import {TranslateDirective, TranslatePipe} from "@ngx-translate/core";
import {SafeHtmlPipe, SafeResourceUrlPipe, SafeUrlPipe} from "./pipes/safe-html.pipes";
import {NhRouterLinkDirective} from "./directives/nh-router-link.directive";
import {NhServerHttpInterceptor} from "./interceptors/nh-server-http.interceptor";
import {NhConfigCommonService} from "./services/nh-config.service";
import {Observable} from "rxjs";
import {NhActiveDivisionInterceptor} from "./interceptors/nh-active-division.interceptor";
import {NhDeduplicateGetRequestsInterceptor} from "./interceptors/nh-deduplicate-get-requests.interceptor";
import {NhFormDropDownComponent} from "./components/nh-form-dropdown/form-dropdown.component";
import {NgxBootstrapMultiselectModule} from "ngx-bootstrap-multiselect";
import {FormsModule} from "@angular/forms";
import {NhFormErrorMessageComponent} from "./components/nh-form-error-message/form-error-message.component";
import {NhApiAuthInterceptor} from "./interceptors/nh-api.auth.interceptor";
import {NhSessionRejectionInterceptor} from "./interceptors/nh-session-rejection.interceptor";
import {NhErrorComponent} from "./components/nh-error/component";
import {BaseNhAuthService, NhAuthService} from "./services/nh-auth.service";
import {INhAuthorization} from "./models/auth.models";
import {NH_ERROR_HANDLERS, NhErrorHandlerService} from "./services/nh-error-handler.service";
import {NhPageSizeComponent} from "./components/nh-page-size/page-size.component";
import {NhSearchInputComponent} from "./components/nh-search-input/search-input.component";


@NgModule({
  imports: [
    CommonModule,
    TranslateDirective,
    TranslatePipe,
    NgxBootstrapMultiselectModule,
    FormsModule,
    // Standalone pipes
    NhRelativeTimePipe
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
    NhPageSizeComponent,
    NhSearchInputComponent,
    // Pipes
    NhDatePipe,
    NhDateTimePipe,
    NhDateUtcPipe,
    NhBooleanToStringPipe,
    IsAuthenticatedPipe,
    IsAllPermissionsGrantedPipe,
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
    NhPageSizeComponent,
    NhSearchInputComponent,
    // Pipes
    NhDatePipe,
    NhDateTimePipe,
    NhDateUtcPipe,
    NhRelativeTimePipe,
    NhBooleanToStringPipe,
    IsAuthenticatedPipe,
    IsAllPermissionsGrantedPipe,
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
  ]
})
export class NhCommonModule {
  constructor(@Optional() @SkipSelf() parentModule: NhCommonModule) {
    // Import guard
    if (parentModule) {
    }
  }

  /**
   * Registers NewHeap's application-wide providers: `HttpClient` with the NewHeap interceptors, the
   * `ErrorHandler` mapping and the configuration initializer. Call it once, at the application root.
   * Features and standalone components import plain `NhCommonModule`, which registers no providers,
   * so they share the root `HttpClient`, its interceptors and the NewHeap services.
   */
  static forRoot<TAuthorization extends INhAuthorization, TAuthService extends BaseNhAuthService<TAuthorization>>(config: NhCommonModuleConfig, authService: Type<TAuthService>): ModuleWithProviders<NhCommonModule> {
    return {
      ngModule: NhCommonRootModule,
      providers: [
        {provide: NhCommonModuleConfig, useValue: config},
        {provide: NhAuthService, useExisting: authService},
      ]
    };
  }
}

/**
 * Carries the providers that belong to the application root. Because `forRoot` returns this module, an
 * injector registers its providers once, at the position of the root registration, even when `forRoot`
 * is imported twice. A feature or standalone component that imports plain `NhCommonModule` never
 * registers them, so it cannot get a second `HttpClient` without the application's own interceptors.
 */
@NgModule({
  imports: [NhCommonModule],
  exports: [NhCommonModule],
  providers: [
    provideAppInitializer(() => {
      const configService = inject(NhConfigCommonService);
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
      useClass: NhSessionRejectionInterceptor,
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
    {
      provide: HTTP_INTERCEPTORS,
      useClass: NhDeduplicateGetRequestsInterceptor,
      multi: true
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
    provideHttpClient(withInterceptorsFromDi(), withFetch())
  ]
})
class NhCommonRootModule {
}

