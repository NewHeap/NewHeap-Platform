import {Inject, Injectable, InjectionToken, Optional, REQUEST_CONTEXT, Type} from '@angular/core';
import {TranslateService} from "@ngx-translate/core";
import {
  ActivatedRoute,
  ActivatedRouteSnapshot,
  NavigationBehaviorOptions,
  NavigationEnd,
  NavigationExtras,
  NavigationSkipped,
  NavigationSkippedCode,
  NavigationStart,
  Router,
  Routes,
  UrlCreationOptions,
  UrlTree,
} from "@angular/router";
import {NhCommonModuleConfig} from "../models/config.models";
import {filter, map} from "rxjs";
import {NhRouteInfo, NhRouteNavigationItem, NhRouterSetupService} from "./nh-router-setup.service";
import {NhRouterLink} from "../models/misc.models";

export const NH_ROUTER_ROOT_ROUTES = new InjectionToken<() => Routes>('NhRouterRootRoutes');
export const NH_ROUTER_LANGUAGE_CHANGE_METHOD = new InjectionToken<(newLanguage: string) => Promise<void>>('NhRouterLanguageChangeMethod');

export class NhBreadCrumbItem {
  text!: string;
  nhRouterLink?: NhRouterLink;

  public constructor(init?: Partial<NhBreadCrumbItem>) {
    Object.assign(this, init);
  }
}

export class NhBreadCrumb {
  renderedItems?: string;
  items: NhBreadCrumbItem[] = [];
  public constructor(init?: Partial<NhBreadCrumb>) {
    Object.assign(this, init);
  }
}

export class NhSitemapEntryItem {
  language!: string;
  isPrimary!: boolean;
  path!: string;

  public constructor(init?: Partial<NhSitemapEntryItem>) {
    Object.assign(this, init);
  }
}

export class NhSitemapEntry {
  id!: string;
  items: NhSitemapEntryItem[] = [];
  public constructor(init?: Partial<NhSitemapEntry>) {
    Object.assign(this, init);
  }
}

export class NhSitemap {
  entries: NhSitemapEntry[] = [];
  public constructor(init?: Partial<NhSitemap>) {
    Object.assign(this, init);
  }
}

@Injectable({
  providedIn: 'root',
})
export class NhRouterService {
  private activeLanguage: string = '';
  private lastKnownActivatedRoute: ActivatedRoute | undefined;

  constructor(
    private moduleConfig: NhCommonModuleConfig,
    private translateService: TranslateService,
    private routerSetupService: NhRouterSetupService,
    private router: Router,
    private activatedRoute: ActivatedRoute,
    @Optional() @Inject(NH_ROUTER_ROOT_ROUTES) private getRootRoutes: () => Routes,
    @Optional() @Inject(NH_ROUTER_LANGUAGE_CHANGE_METHOD) private changeLanguageMethod: (newLanguage: string) => Promise<void>,
    @Optional() @Inject(REQUEST_CONTEXT) private requestContext: any
  ) {
    this.activeLanguage = this.translateService.currentLang;
    this.translateService.onLangChange.subscribe((langChangeEvent) => {
      this.activeLanguage = langChangeEvent.lang;
    });

    this.router.events.pipe(
      filter((event) => (event instanceof NavigationEnd || (event instanceof NavigationSkipped && event.code === NavigationSkippedCode.IgnoredSameUrlNavigation))),
      map(() => NhRouterSetupService.rootRoute(this.activatedRoute)),
      filter((route: ActivatedRoute) => route.outlet === 'primary'),
    ).subscribe(async (route: ActivatedRoute) => {
      this.lastKnownActivatedRoute = route;
    });

    if(this?.requestContext?.response) {
      this.router.events.subscribe(async event => {
        if (event instanceof NavigationStart
          && this.moduleConfig.language !== this.moduleConfig.defaultLanguage) {
          if(event.url?.trim() === '/' || event.url?.trim() === '') {
            this.redirectServer(`/${this.moduleConfig.language}`);
          }
        }
      });
    }

  }

  public redirectServer(location: string, statusCode: number = 301) {
    if(this?.requestContext?.response) {
      (<any>this?.requestContext?.response).setHeader('Location', location);
      (<any>this?.requestContext?.response).statusCode = statusCode;
    }
  }

  public platformSpecificRedirect(location: NhRouterLink, statusCode: number = 301) {
    if(this?.requestContext?.response) {

      const url = this.createUrlForNavigationItem(location);
      console.log('Server side redirect: ' + url);
      this.redirectServer(url, statusCode);
    } else {
      this.navigate(location).then();
    }
  }

  public notFound() {
    this.platformSpecificRedirect({id: 'home/not-found'}, 301);
  }

  getActivatedNhLink(activatedRouteSnapshot:ActivatedRouteSnapshot) {
    if(activatedRouteSnapshot === null) {
      throw new Error('activatedRouteSnapshot is null');
    }

    let id = '';
    const nhRouteData = activatedRouteSnapshot.data[NhRouterSetupService.Nh_ROUTE_DATA_KEY] as NhRouteInfo;

    if(nhRouteData.parentIds) {
      for(let i = 1; i < nhRouteData.parentIds!.length; i++) {
        id += nhRouteData.parentIds![i] + '/';
      }
    }

    const nhRouterLink = <NhRouterLink>{
      id: id + nhRouteData.id,
      arguments: activatedRouteSnapshot.params,
    }
    return nhRouterLink;
  }

  createUrlForNavigationItem(nhRouterLink: NhRouterLink): string {
    return this.routerSetupService.createUrlForNavigationItem(nhRouterLink);
  }

  async navigate(nhRouterLink: NhRouterLink, navigationExtras?: NavigationExtras) {
    if(nhRouterLink && nhRouterLink.language && nhRouterLink.language !== this.translateService.currentLang) {
      if(!this.changeLanguageMethod) {
        throw new Error('Nh_ROUTER_LANGUAGE_CHANGE_METHOD is not defined via DI injection token.');
      }
      // We detected a language change, change the language and reload the routes so navigation works.
      await this.changeLanguageMethod(nhRouterLink.language);
      this.reloadRoutes();
    }

    const url = this.createUrlForNavigationItem(nhRouterLink);

    return await this.router.navigate([url], navigationExtras);
  }

  createUrlTree(nhRouterLink: NhRouterLink): UrlTree {
    return this.router.parseUrl(this.createUrlForNavigationItem(nhRouterLink));
  }

  parseUrl(nhRouterLink: NhRouterLink): UrlTree {
    return this.router.parseUrl(this.createUrlForNavigationItem(nhRouterLink));
  }

  serializeUrl(nhRouterLink: NhRouterLink): string {
    return this.router.serializeUrl(this.parseUrl(nhRouterLink));
  }

  async getRoutePathForLanguage(activatedRouteSnapshot: ActivatedRouteSnapshot|null, language: string): Promise<string> {
    //
    // TODO: due to route params, we want to access an API service that reads our current url and returns it from (for example) the Sitemap API.
    //
    if(activatedRouteSnapshot === null) {
      throw new Error('activatedRouteSnapshot is null');
    }

    let newRoutePaths: string[] = [];
    do {
      if((activatedRouteSnapshot?.url?.length ?? 0)  < 1) {
        activatedRouteSnapshot = activatedRouteSnapshot?.parent;
        continue;
      }

      let path = '';
      if(activatedRouteSnapshot.data && activatedRouteSnapshot.data[NhRouterSetupService.Nh_ROUTE_DATA_KEY]) {
        const nhRouteData = activatedRouteSnapshot.data[NhRouterSetupService.Nh_ROUTE_DATA_KEY] as NhRouteInfo;
        const route = nhRouteData.routes.find(r => r.language === language);
        path = route!.path;
      } else {
        path = activatedRouteSnapshot.routeConfig?.path || '';
      }

      // Resolve route params (/some/path/:id/:name) back to actual values
      const sourceRouteParamsMap = activatedRouteSnapshot.paramMap;
      if(sourceRouteParamsMap.keys.length > 0) {
        for(const key of sourceRouteParamsMap.keys) {
          path = path.replace(`:${key}`, sourceRouteParamsMap.get(key) ?? '');
        }
      }

      const pathParts = path.split('/').map(x => x?.trim()).filter(x => (x?.length ?? 0) > 0).reverse();
      for(const pathPart of pathParts) {
        newRoutePaths.unshift(pathPart);
      }
      activatedRouteSnapshot = activatedRouteSnapshot.parent;
    } while (activatedRouteSnapshot?.parent);

    newRoutePaths = newRoutePaths.filter(x => (x.length ?? 0) > 0);

    if(language !== this.routerSetupService.getDefaultLanguage()) {
      if(newRoutePaths.length > 0 && newRoutePaths[0] !== language) {
        newRoutePaths.unshift(language);
      } else {
        newRoutePaths.unshift(language);
      }
    }

    const newUrlTree = this.router.createUrlTree(newRoutePaths, {
      queryParams: activatedRouteSnapshot!.queryParams,
      fragment: activatedRouteSnapshot?.fragment ?? undefined
    });

    let url = this.router.serializeUrl(newUrlTree);
    const rootRouteDefaultLanguageDetected = newRoutePaths.length === 0;
    if(rootRouteDefaultLanguageDetected) {
      url = url.split('/')[0];
    }

    return url;
  }

  async navigateToLanguage(language: string): Promise<void> {
    if(!this.lastKnownActivatedRoute?.snapshot) {
      throw new Error('lastKnownActivatedRoute is null');
    }

    const routePath = await this.getRoutePathForLanguage(this.lastKnownActivatedRoute?.snapshot, language);
    this.reloadRoutes();
    await this.router.navigateByUrl(routePath);
  }

  public reloadRoutes(){
    if(!this.getRootRoutes) {
      throw new Error('Nh_ROUTER_ROOT_ROUTES is not defined via DI injection token.');
    }

    const newRootRoutes = this.getRootRoutes();
    this.router.resetConfig(newRootRoutes);
  }

  //
  // DO NOT USE -> USE THE ONE FROM THE APP-PAGE SERVICE
  //
  async getBreadcrumb(activatedRouteSnapshot: ActivatedRouteSnapshot|null): Promise<NhBreadCrumb> {
    const breadCrumb = new NhBreadCrumb({
      items: []
    });

    if(activatedRouteSnapshot === null) {
      throw new Error('activatedRouteSnapshot is null');
    }

    do {
      if((activatedRouteSnapshot?.url?.length ?? 0)  < 1) {
        activatedRouteSnapshot = activatedRouteSnapshot?.parent;
        continue;
      }

      const nhLink = this.getActivatedNhLink(activatedRouteSnapshot);
      if(nhLink.id === 'root') {
        break;
      }

      const crumbHref = await this.getRoutePathForLanguage(activatedRouteSnapshot, this.activeLanguage);

      if(!crumbHref) {
        activatedRouteSnapshot = activatedRouteSnapshot?.parent;
        continue;
      }

      let crumbText = '';
      if(activatedRouteSnapshot.data && activatedRouteSnapshot.data['breadcrumb']) {
        crumbText = this.translateService.instant(activatedRouteSnapshot.data['breadcrumb'] as string);
      } else {
        crumbText = nhLink.id;
      }

      const crumb = new NhBreadCrumbItem({
        nhRouterLink: nhLink,
        text: crumbText ?? '',
      });

      breadCrumb.items.unshift(crumb);

      activatedRouteSnapshot = activatedRouteSnapshot.parent;
    } while (activatedRouteSnapshot?.parent);

    return breadCrumb;
  }

  createSitemap() {
    const sitemap: NhSitemap = new NhSitemap();

    const processRoute = (item: NhRouteNavigationItem) => {
      const nhRouteInfo = (<any>item.route).data[NhRouterSetupService.Nh_ROUTE_DATA_KEY] as NhRouteInfo;

      if(nhRouteInfo.excludeFromSitemap) {
        return;
      }

      for(const route of nhRouteInfo.routes) {
        if((item.children?.length ?? 0) < 1 && (item.route.component || item.route.loadComponent)) {

          if(!this.moduleConfig.supportedLanguages?.find(x => x === route.language)) {
            continue;
          }

          let path = this.createUrlForNavigationItem({ id: item.id, language: route.language });

          if(path.includes(':') || path.includes('?') || item.id === 'root/home/sitemap-xml') {
            continue;
          }

          if(!path.startsWith('/')) {
            path = '/' + path;
          }

          let sitemapEntry = sitemap.entries.find(x => x.id === item.id);
          if(!sitemapEntry) {
            sitemapEntry = new NhSitemapEntry({ id: item.id });
            sitemap.entries.push(sitemapEntry);
          }

          let sitemapEntryItem = sitemapEntry.items.find(x => x.language === route.language);
          if(!sitemapEntryItem) {
            sitemapEntryItem = new NhSitemapEntryItem({
              path: path,
              language: route.language,
              isPrimary: route.language === this.routerSetupService.getDefaultLanguage()
            });

            sitemapEntry.items.push(sitemapEntryItem);
          }
        }
      }

      for(const child of item.children) {
        processRoute(child);
      }
    };

    const rootRoute = this.routerSetupService.getRootRouteNavigationItem();
    processRoute(rootRoute);

    return sitemap;
  }
}
