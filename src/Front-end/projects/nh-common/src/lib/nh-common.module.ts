import {inject, ModuleWithProviders, NgModule, Optional, provideAppInitializer, SkipSelf} from "@angular/core";
import {CommonModule} from "@angular/common";
import {
  HTTP_INTERCEPTORS,
  provideHttpClient,
  withFetch,
  withInterceptorsFromDi
} from "@angular/common/http";
import {
  IsAuthenticatedPipe,
  IsClaimGrantedPipe, IsGrantedRolePipe,
  IsOneClaimGrantedPipe,
  IsOnePermissionGrantedPipe, IsOneRoleGrantedPipe
} from "./pipes/auth.pipes";
import {NhDatePipe, NhDateUtcPipe} from "./pipes/date.pipes";
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


@NgModule({
  imports: [
    CommonModule,
    TranslateModule
  ],
  declarations: [
    // Components
    NhModalComponent,
    NhLoaderComponent,
    NhJsonLdComponent,
    NhModalLoadingComponent,
    NhModalConfirmComponent,
    // Pipes
    NhDatePipe,
    NhDateUtcPipe,
    NhBooleanToStringPipe,
    IsAuthenticatedPipe,
    IsOnePermissionGrantedPipe,
    IsClaimGrantedPipe,
    IsOneClaimGrantedPipe,
    IsOneRoleGrantedPipe,
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
  exports: [
    // Components
    NhModalComponent,
    NhLoaderComponent,
    NhJsonLdComponent,
    NhModalLoadingComponent,
    NhModalConfirmComponent,
    // Pipes
    NhDatePipe,
    NhDateUtcPipe,
    NhBooleanToStringPipe,
    IsAuthenticatedPipe,
    IsOnePermissionGrantedPipe,
    IsClaimGrantedPipe,
    IsOneClaimGrantedPipe,
    IsOneRoleGrantedPipe,
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
      return new Observable<unknown>((observer) => {
        //
        // We use APP_INITIALIZER to load the configuration before the application starts. (Cuz DEPS calls for AppConfigService it is loaded soon in the lifecycle of the app.)
        //
        configService.initialize().then(() => {
          observer.next();
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
      useClass: NhEncodeHttpParamsInterceptor,
      multi: true
    },
    {
      provide: HTTP_INTERCEPTORS,
      useClass: NhServerHttpInterceptor,
      multi: true
    },
    provideHttpClient(withInterceptorsFromDi(), withFetch()),
    // Guards
    NhCanCancelNavigationGuard,
    // Services
    NhTaskResultFormValidationService,
    NhCookieService,
    NhInternetConnectionService
  ]
})
export class NhCommonModule {
  constructor(@Optional() @SkipSelf() parentModule: NhCommonModule) {
    // Import guard
    if (parentModule) {
    }
  }

  static forRoot(config: NhCommonModuleConfig): ModuleWithProviders<NhCommonModule> {
    return {
      ngModule: NhCommonModule,
      providers: [
        {provide: NhCommonModuleConfig, useValue: config}
      ]
    };
  }
}

