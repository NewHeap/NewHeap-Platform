import {
  inject,
  importProvidersFrom,
  makeEnvironmentProviders,
  provideEnvironmentInitializer,
  TransferState
} from '@angular/core';
import { registerLocaleData } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import localeEn from '@angular/common/locales/en';
import localeNl from '@angular/common/locales/nl';
import { provideAnimations } from '@angular/platform-browser/animations';
import { provideRouter, Routes, withInMemoryScrolling } from '@angular/router';
import {
  NhAuthorization,
  AuthenticationNhCommonModuleConfig,
  NhCommonModule,
  NhCommonModuleConfig,
  NhErrorLoggingNhCommonModuleConfig,
  NhFormDropDownNhCommonModuleConfig,
  NhHttpNhCommonModuleConfig,
  NhSentryErrorLoggingNhCommonModuleConfig,
  NhTranslationNhCommonModuleConfig,
  nhTranslateBrowserLoaderFactory,
  UserNotificationNhCommonModuleConfig
} from '@newheap/platform-common';
import {
  provideTranslateService,
  TranslateLoader,
  TranslateModule
} from '@ngx-translate/core';
import { ToastrModule } from 'ngx-toastr';
import { SampleAuthService } from './sample-auth.service';
import { SampleAuthSessionService } from './sample-auth-session.service';

registerLocaleData(localeNl, 'nl');
registerLocaleData(localeEn, 'en');

export function provideSampleProjectManagement(routes: Routes = []) {
  const newHeapConfig = new NhCommonModuleConfig({
    appDisplayName: 'Sample Project Management',
    baseUrl: window.location.origin,
    apiBaseUrl: '/api',
    authApiBaseUrl: '/api',
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
    errorLogging: new NhErrorLoggingNhCommonModuleConfig({
      sentry: new NhSentryErrorLoggingNhCommonModuleConfig({
        errorLoggingEnabled: true,
        tracingEnabled: true,
        options: {
          enabled: false,
          environment: 'sample',
          release: 'sample-project-management@0.1.0',
          tracesSampleRate: 1
        }
      })
    }),
    userNotification: new UserNotificationNhCommonModuleConfig({
      urlSuffix: '/project-user-notifications',
      pollingInterval: 5000
    })
  });

  return makeEnvironmentProviders([
    SampleAuthService,
    provideEnvironmentInitializer(() => {
      inject(SampleAuthSessionService).start();
    }),
    provideAnimations(),
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
