import {NgModule} from '@angular/core';
import {Routes, RouterModule, ROUTES} from '@angular/router';
import {TranslateService} from "@ngx-translate/core";
import {NhConfigCommonService, NhRegisterRoute, NhRoute, NhRouterSetupService} from "nh-common";
import {LoginAuthPage} from "./pages/login/page";

export function routes(configService: NhConfigCommonService, translateService: TranslateService, nhRouterSetupService: NhRouterSetupService): Routes {
  const config = configService.getConfig();
  const language = config.languageCode;

  let routes: Routes = [
    ...nhRouterSetupService.createRoute(new NhRegisterRoute({
      id: 'login',
      parentIds: ['root', 'auth'],
      routes: [
        new NhRoute({ language: 'nl', path: 'login' }),
        new NhRoute({ language: 'en', path: 'login' }),
        new NhRoute({ language: 'de', path: 'login' }),
      ],
      pathMatch: 'full',
      component: LoginAuthPage,
      data: {
        breadcrumb: 'app.cart.login.title'
      }
    })),
  ];

  return routes;
}

@NgModule({
  imports: [RouterModule.forChild([])],
  exports: [RouterModule],
  providers: [
    { provide: ROUTES, useFactory: routes, deps: [NhConfigCommonService, TranslateService, NhRouterSetupService], multi: true },
  ],
})
export class AuthRoutingModule {
}
