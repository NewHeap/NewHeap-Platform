import {NgModule} from '@angular/core';
import {Routes, RouterModule, ROUTES} from '@angular/router';
import {TranslateService} from "@ngx-translate/core";
import {OverviewAddressPage} from "./pages/overview/page";
import {ViewAddressPage} from "./pages/view/page";
import {NhConfigCommonService, NhRegisterRoute, NhRoute, NhRouterSetupService} from "nh-common";

export function routes(configService: NhConfigCommonService, translateService: TranslateService, nhRouterSetupService: NhRouterSetupService): Routes {
  const config = configService.getConfig();
  const language = config.languageCode;

  let routes: Routes = [
    ...nhRouterSetupService.createRoute(new NhRegisterRoute({
      id: 'overview',
      parentIds: ['root', 'address'],
      routes: [
        new NhRoute({ language: 'nl', path: '' }),
        new NhRoute({ language: 'en', path: '' }),
        new NhRoute({ language: 'de', path: '' }),
      ],
      pathMatch: 'full',
      canActivate: [],
      component: OverviewAddressPage,
      data: {
        breadcrumb: translateService.instant('app.routes.address.base')
      }
    })),
    ...nhRouterSetupService.createRoute(new NhRegisterRoute({
      id: 'view',
      parentIds: ['root', 'address'],
      routes: [
        new NhRoute({ language: 'nl', path: 'view/:id' }),
        new NhRoute({ language: 'en', path: 'view/:id' }),
        new NhRoute({ language: 'de', path: 'view/:id' }),
      ],
      pathMatch: 'full',
      canActivate: [],
      component: ViewAddressPage,
      data: {
        breadcrumb: translateService.instant('app.routes.address.base')
      }
    }))
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
export class AddressRoutingModule {
}
