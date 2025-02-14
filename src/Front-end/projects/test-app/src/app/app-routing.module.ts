import {NgModule} from '@angular/core';
import {Route, RouterModule, ROUTES, Routes} from '@angular/router';
import {AppDefaultLayoutComponent} from "./layout/default-layout/default-layout";
import {environment} from "../environments/environment";
import {TranslateService} from "@ngx-translate/core";
import {NhConfigCommonService, NhRegisterRoute, NhRoute, NhRouterSetupService} from "nh-common";

import {routes as getHomeRoutes} from "./modules/home/home-routing";

export function routes(configService: NhConfigCommonService, translateService: TranslateService, nhRouterSetupService: NhRouterSetupService): Routes {
  const config = configService.getConfig();
  const language = config.languageCode;
  const defaultLanguage = environment.defaultLanguage;
  const isDefaultLanguage = language === defaultLanguage;
  const prefix = isDefaultLanguage ? '' : language;

  nhRouterSetupService.setDefaultLanguage(defaultLanguage);
  nhRouterSetupService.setSupportedLanguages(environment.supportedLanguages);

  let routes: Routes = [
    ...nhRouterSetupService.createRoute(new NhRegisterRoute({
      id: 'root',
      parentIds: undefined, // Mark root
      routes: [
        new NhRoute({ language: 'nl', path: (defaultLanguage === 'nl') ? '' : 'nl' }),
        new NhRoute({ language: 'en', path: (defaultLanguage === 'en') ? '' : 'en' }),
        new NhRoute({ language: 'de', path: (defaultLanguage === 'de') ? '' : 'de' }),
      ],
      component: AppDefaultLayoutComponent,
      canActivate: [],
      children: [
        ...nhRouterSetupService.createRoute(new NhRegisterRoute({
          id: 'home',
          parentIds: ['root'],
          routes: [
            new NhRoute({ language: 'nl', path: '' }),
            new NhRoute({ language: 'en', path: '' }),
            new NhRoute({ language: 'de', path: '' }),
          ],
          canActivate: [],
          loadChildren: () => import('./modules/home/home.module').then(m => m.HomeModule),
        }))
      ]
    })),
    // Fallback when no prior route is matched
    {path: '**', redirectTo: prefix + '/404', pathMatch: 'full'}
  ];

  if(language !== environment.defaultLanguage) {
    // Register a root route redirect to the default language.
    routes.unshift({path: '', redirectTo: prefix, pathMatch: 'full',  });
  }

  const rootRoutes = routes;
  const homeRoutes = getHomeRoutes(configService, translateService, nhRouterSetupService);

  const allRoutes = [
    ...rootRoutes,
    ...homeRoutes
  ];

  nhRouterSetupService.processRegisteredRoutes();

  return routes;
}

@NgModule({
  providers: [
    { provide: ROUTES, useFactory: routes, deps: [NhConfigCommonService, TranslateService, NhRouterSetupService], multi: true },
  ],
  imports: [RouterModule.forRoot([])],
  exports: [RouterModule]
})
export class AppRoutingModule { }
