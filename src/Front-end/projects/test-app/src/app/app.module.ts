import {CSP_NONCE, inject, LOCALE_ID, NgModule, Optional, TransferState} from '@angular/core';
import {
  BrowserModule,
  provideClientHydration,
  withEventReplay, withHttpTransferCacheOptions,
  withNoHttpTransferCache
} from '@angular/platform-browser';

import {AppRoutingModule} from './app-routing.module';
import {AppComponent} from './app.component';
import {RouterModule} from "@angular/router";
import {LayoutModule} from "./layout/layout.module";
import {APP_BASE_HREF, registerLocaleData} from "@angular/common";
import localeNL from '@angular/common/locales/nl';
import localeDE from '@angular/common/locales/nl';
import localeEN from '@angular/common/locales/en';
import {TranslateLoader, TranslateModule, TranslateService} from "@ngx-translate/core";
import {HttpClient, provideHttpClient, withFetch, withInterceptorsFromDi} from "@angular/common/http";
import {translateBrowserLoaderFactory} from "./miscellaneous/translate-loaders/translate-browser.loader";
import {environment} from "../environments/environment";
import {
  NhCommonModuleConfig,
  NH_ROUTER_LANGUAGE_CHANGE_METHOD,
  NH_ROUTER_ROOT_ROUTES,
  NhCommonModule,
  NhRouterSetupService, NhConfigCommonService
} from "nh-common";
import {routes as getRootRoutes} from "./app-routing.module";
import {ToastrModule} from "ngx-toastr";
import {BrowserAnimationsModule} from "@angular/platform-browser/animations";

registerLocaleData(localeNL, 'nl');
registerLocaleData(localeEN, 'en');

//registerLocaleData(localeDE, 'de');

@NgModule({
  declarations: [
    AppComponent
  ],
  bootstrap: [AppComponent],
  imports: [
    RouterModule,
    BrowserModule,
    AppRoutingModule,
    BrowserAnimationsModule,
    LayoutModule.forRoot(),
    TranslateModule.forRoot({
      loader: {
        provide: TranslateLoader,
        useFactory: (translateBrowserLoaderFactory),
        deps: [HttpClient, TransferState]
      },
      defaultLanguage: environment.defaultLanguage
    }),
    NhCommonModule.forRoot(new NhCommonModuleConfig({
      baseUrl: environment.baseUrl,
      language: environment.defaultLanguage,
      defaultLanguage: environment.defaultLanguage,
      supportedLanguages: environment.supportedLanguages,
      culture: environment.defaultCulture,
      defaultCulture: environment.defaultCulture,
      environment: environment.name,
      cookieDomain: environment.cookieDomain
    })),
    ToastrModule.forRoot({
      positionClass:'toast-bottom-right'
    })
  ],
  providers: [
    provideClientHydration(withHttpTransferCacheOptions({
      includeRequestsWithAuthHeaders: true,
      includePostRequests: true
    })),
    {provide: APP_BASE_HREF, useValue: '/'},
    {
      provide: LOCALE_ID,
      deps: [TranslateService],
      useFactory: (translateService: TranslateService) => translateService.currentLang
    },
    {
      provide: NH_ROUTER_ROOT_ROUTES,
      useFactory: () => {
        const configService = inject(NhConfigCommonService);
        const translateService = inject(TranslateService);
        const nhRouterSetupService = inject(NhRouterSetupService);
        return () => getRootRoutes(configService, translateService, nhRouterSetupService);
      }
    },
    {
      provide: NH_ROUTER_LANGUAGE_CHANGE_METHOD,
      useFactory: () => {
        const configService = inject(NhConfigCommonService);
        return (language: string) => {
          return configService.changeLanguage(language);
        }
      }
    }
  ]
})
export class AppModule {
}
