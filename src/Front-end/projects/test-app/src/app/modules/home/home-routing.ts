import {NgModule} from '@angular/core';
import {Routes, RouterModule, ROUTES} from '@angular/router';
import {TranslateService} from "@ngx-translate/core";
import {NhConfigCommonService, NhRegisterRoute, NhRoute, NhRouterSetupService} from "nh-common";
import {IndexHomePage} from "./pages/index/page";
import {SitemapXmlHomePage} from "../sitemap-xml/page";

export function routes(configService: NhConfigCommonService, translateService: TranslateService, nhRouterSetupService: NhRouterSetupService): Routes {
  const config = configService.getConfig();
  const language = config.languageCode;

  let routes: Routes = [
    ...nhRouterSetupService.createRoute(new NhRegisterRoute({
      id: 'index',
      parentIds: ['root', 'home'],
      routes: [
        new NhRoute({ language: 'nl', path: '' }),
        new NhRoute({ language: 'en', path: '' }),
        new NhRoute({ language: 'de', path: '' }),
      ],
      pathMatch: 'full',
      canActivate: [],
      component: IndexHomePage,
      data: {
        breadcrumb: translateService.instant('app.routes.home.base')
      }
    })),
    ...nhRouterSetupService.createRoute(new NhRegisterRoute({
      id: 'sitemap-xml',
      parentIds: ['root', 'home'],
      routes: [
        new NhRoute({ language: 'nl', path: 'sitemap.xml' }),
        new NhRoute({ language: 'en', path: 'sitemap.xml' }),
        new NhRoute({ language: 'de', path: 'sitemap.xml' }),
      ],
      pathMatch: 'full',
      canActivate: [],
      component: SitemapXmlHomePage,
      data: {
        breadcrumb: 'app.routes.home.sitemap-xml'
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
export class HomeRouting {
}
