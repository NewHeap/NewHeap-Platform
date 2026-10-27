import {
  inject,
  importProvidersFrom,
  makeEnvironmentProviders,
  provideEnvironmentInitializer,
  TransferState
} from '@angular/core';
import { registerLocaleData } from '@angular/common';
import { HTTP_INTERCEPTORS, HttpClient } from '@angular/common/http';
import localeEn from '@angular/common/locales/en';
import localeNl from '@angular/common/locales/nl';
import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';
import { provideRouter, Routes, withInMemoryScrolling } from '@angular/router';
import {
  NhAuthorization,
  AuthenticationNhCommonModuleConfig,
  BackgroundOperationsNhCommonModuleConfig,
  NhCommonModule,
  NhCommonModuleConfig,
  NhFormDropDownNhCommonModuleConfig,
  NhHttpNhCommonModuleConfig,
  NH_ERROR_HANDLERS,
  NhTranslationNhCommonModuleConfig,
  nhTranslateBrowserLoaderFactory,
  UserNotificationNhCommonModuleConfig
} from '@newheap/platform-common';
import {
  provideTranslateService,
  TranslateLoader,
  TranslateModule
} from '@ngx-translate/core';
import { provideNhToastr } from '@newheap/nh-toastr';
import { ToastrModule } from 'ngx-toastr';
import { SAMPLE_API_BASE_URL, SampleApiConnectionInterceptor } from './sample-api-connection-state.service';
import { SampleAuthService } from './sample-auth.service';
import { SampleAuthSessionService } from './sample-auth-session.service';
import { SampleFrontendErrorHandler } from './sample-frontend-error-handler';

registerLocaleData(localeNl, 'nl');
registerLocaleData(localeEn, 'en');

export function provideSampleProjectManagement(routes: Routes = []) {
  const newHeapConfig = new NhCommonModuleConfig({
    appDisplayName: 'Sample Project Management',
    baseUrl: window.location.origin,
    // The access token and the active division go only to requests under these bases: on this
    // origin, with a path that is /api or continues after /api/. Other origins receive neither.
    apiBaseUrl: SAMPLE_API_BASE_URL,
    authApiBaseUrl: SAMPLE_API_BASE_URL,
    language: 'en',
    defaultLanguage: 'en',
    supportedLanguages: ['en', 'nl'],
    culture: 'en-US',
    defaultCulture: 'en-US',
    authentication: new AuthenticationNhCommonModuleConfig({
      loginPath: '/auth/login'
    }),
    environment: 'sample',
    defaultItemsPerPage: 50,
    translation: new NhTranslationNhCommonModuleConfig({
      browserLoaderPrefix: './i18n/'
    }),
    http: new NhHttpNhCommonModuleConfig({
      deduplicateGetRequests: true
    }),
    formDropdown: new NhFormDropDownNhCommonModuleConfig({
      deferLazyLoadUntilOpened: true
    }),
    userNotification: new UserNotificationNhCommonModuleConfig({
      urlSuffix: '/project-user-notifications',
      pollingInterval: 5000
    }),
    backgroundOperations: new BackgroundOperationsNhCommonModuleConfig({
      urlSuffix: '/background-operations',
      hubBaseUrl: '/api',
      hubUrlSuffix: '/hub/background-operations',
      pollingInterval: 5000,
      liveUpdatesEnabled: true,
      listPageSize: 100
    })
  });

  return makeEnvironmentProviders([
    SampleAuthService,
    {
      provide: NH_ERROR_HANDLERS,
      useClass: SampleFrontendErrorHandler,
      multi: true
    },
    {
      provide: HTTP_INTERCEPTORS,
      useClass: SampleApiConnectionInterceptor,
      multi: true
    },
    provideNhToastr({ positionClass: 'toast-bottom-right' }),
    provideEnvironmentInitializer(() => {
      inject(SampleAuthSessionService).start();
    }),
    // The animation engine loads after the first render instead of with the initial bundle.
    provideAnimationsAsync(),
    provideRouter(
      routes,
      withInMemoryScrolling({
        anchorScrolling: 'enabled',
        scrollPositionRestoration: 'enabled'
      })
    ),
    importProvidersFrom(
      TranslateModule,
      ToastrModule.forRoot({ positionClass: 'toast-bottom-right' }),
      NhCommonModule.forRoot<NhAuthorization, SampleAuthService>(
        newHeapConfig,
        SampleAuthService
      )
    ),
    provideTranslateService({
      fallbackLang: 'nl',
      lang: 'nl',
      loader: {
        provide: TranslateLoader,
        useFactory: nhTranslateBrowserLoaderFactory,
        deps: [HttpClient, TransferState]
      }
    })
  ]);
}
