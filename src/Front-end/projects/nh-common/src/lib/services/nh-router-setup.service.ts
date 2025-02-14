import {Injectable, Type} from '@angular/core';
import {TranslateService} from "@ngx-translate/core";
import {
  ActivatedRoute,
  ActivatedRouteSnapshot,
  CanActivateChildFn,
  CanActivateFn, CanMatchFn, Data, DefaultExport,
  LoadChildren, NavigationEnd, NavigationSkipped, NavigationSkippedCode, RedirectFunction,
  Route, Router, Routes, UrlMatchResult, UrlSegment, UrlSegmentGroup,
} from "@angular/router";
import {NhCommonModuleConfig} from "../models/config.models";
import {filter, map, Observable} from "rxjs";
import {NhRouterLink} from "../models/misc.models";

export class NhRoute {
  language!: string;
  path!: string;

  public constructor(init?: Partial<NhRoute>) {
    Object.assign(this, init);
  }
}

export class NhRegisterRoute {
  id!: string;
  parentIds: string[]|undefined = []; // Should be in order of parent to child
  routes: NhRoute[] = [];
  children?: Routes;
  loadChildren?: LoadChildren;
  component?: Type<any>;
  loadComponent?: () => Type<unknown> | Observable<Type<unknown> | DefaultExport<Type<unknown>>> | Promise<Type<unknown> | DefaultExport<Type<unknown>>>;
  canActivate?: Array<CanActivateFn>;
  canMatch?: Array<CanMatchFn>;
  canActivateChild?: Array<CanActivateChildFn>;
  redirectTo?: string | RedirectFunction;
  pathMatch?: 'prefix' | 'full';
  data?: Data;
  excludeFromSitemap?: boolean;

  public constructor(init?: Partial<NhRegisterRoute>) {
    Object.assign(this, init);
  }
}

export class NhRouteInfo {
  id!: string;
  parentIds: string[]|undefined;
  activeRoute!: NhRoute;
  routes: NhRoute[] = [];
  excludeFromSitemap?: boolean;

  public constructor(init?: Partial<NhRouteInfo>) {
    Object.assign(this, init);
  }
}

export class NhRouteNavigationItem {
  id!: string;
  path!: string;
  route!: Route;
  parent: NhRouteNavigationItem|undefined;
  children: NhRouteNavigationItem[] = [];

  public constructor(init?: Partial<NhRouteNavigationItem>) {
    Object.assign(this, init);
  }

}

@Injectable({
  providedIn: 'root',
})
export class NhRouterSetupService {
  public static readonly Nh_ROUTE_DATA_KEY = '__nh_route_data_key__';
  private supportedLanguages: string[] = [];
  private defaultLanguage: string = '';
  private activeLanguage: string = '';
  private lastKnownActivatedRoute: ActivatedRoute | undefined;
  private registeredRoutes: Routes = [];
  private rootRouteNavigationItem: NhRouteNavigationItem = new NhRouteNavigationItem();

  constructor(
    private moduleConfig: NhCommonModuleConfig,
    private translateService: TranslateService
  ) {
    this.activeLanguage = this.translateService.currentLang;
    this.translateService.onLangChange.subscribe((langChangeEvent) => {
      this.activeLanguage = langChangeEvent.lang;
    });
  }

  public getRootRouteNavigationItem(): NhRouteNavigationItem {
    return this.rootRouteNavigationItem;
  }

  private getNormalizedNavigationItemId(id: string): string {
    if(id.startsWith('/')) {
      id = id.substring(1);
    }

    if(id.endsWith('/')) {
      id = id.substring(0, id.length - 1);
    }

    if(!id.startsWith('root/')) {
      id = 'root/' + id;
    }

    return id;
  }

  public getRouteNavigationItem(nhRouterLink: NhRouterLink): NhRouteNavigationItem|undefined {
    const id = this.getNormalizedNavigationItemId(nhRouterLink.id);
    const search = (item: NhRouteNavigationItem): NhRouteNavigationItem|undefined => {
      if(item.id === id) {
        return item;
      }

      for(const child of item.children) {
        const result = search(child);
        if(result) {
          return result;
        }
      }

      return undefined;
    };

    return search(this.rootRouteNavigationItem);
  }

  public createUrlForNavigationItem(nhRouterLink: NhRouterLink): string {
    const navigationItem = this.getRouteNavigationItem(nhRouterLink);
    if(!navigationItem) {
      throw new Error(`Navigation item with id ${nhRouterLink.id} not found.`);
    }

    const language = nhRouterLink.language ?? this.translateService.currentLang;
    const parts: string[] = [];
    let current: NhRouteNavigationItem|undefined = navigationItem;
    while(current) {
      const nhRouteInfo = (<any>current.route).data[NhRouterSetupService.Nh_ROUTE_DATA_KEY] as NhRouteInfo;
      const nhRoute = nhRouteInfo.routes.find(x => x.language === language);
      if(!nhRoute) {
        throw new Error(`No route found for language ${language}`);
      }
      parts.unshift(nhRoute!.path);
      current = current.parent;
    }

    let resolvedPath = '/' + parts.filter(x => x.trim() !== '').join('/');

    if(nhRouterLink.arguments) {
      for(const key of Object.keys(nhRouterLink.arguments)) {
        resolvedPath = resolvedPath.replace(`:${key}`, nhRouterLink.arguments[key]);
      }
    }

    return resolvedPath;
  }

  public setDefaultLanguage(language: string): void {
    this.defaultLanguage = language;
  }

  public getDefaultLanguage(): string {
    return this.defaultLanguage;
  }

  public setSupportedLanguages(languages: string[]): void {
    this.supportedLanguages = languages;
  }

  public getRouteTranslationKey(path: string) {
    return `_nh_route_path_${path}`;
  }

  public createRoute(options: NhRegisterRoute): Route[] {
    for(const supportedLanguage of this.supportedLanguages) {
      if(!options.routes.find(x => x.language === supportedLanguage)) {
        throw new Error(`No route found for supported language ${supportedLanguage}. All supported languages must have a route defined.`);
      }
    }

    const routes: Route[] = [];

    for(const supportedLanguage of this.supportedLanguages) {
      const route: Route = {
        path: undefined,
        pathMatch: options.pathMatch,
        data: options.data ?? {},
        canActivate: options.canActivate,
        children: options.children,
        loadChildren: options.loadChildren,
        component: options.component,
        loadComponent: options.loadComponent,
        canMatch: options.canMatch,
        canActivateChild: options.canActivateChild,
        redirectTo: options.redirectTo,
      };

      const nhRouteData = new NhRouteInfo({
        id: options.id,
        parentIds: options.parentIds,
        activeRoute: options.routes.find(x => x.language === supportedLanguage),
        routes: options.routes,
        excludeFromSitemap: options.excludeFromSitemap
      });

      route.path = nhRouteData.activeRoute.path;
      (<any>route.data)[NhRouterSetupService.Nh_ROUTE_DATA_KEY] = nhRouteData;

      this.registeredRoutes.push(route);
      routes.push(route);
    }

    return routes;
  }

  public processRegisteredRoutes() {
    const routes = this.registeredRoutes;

    const processRoutes = (route: Route, parentIds: string[], parentRouteNavigationItem: NhRouteNavigationItem|undefined, routeNavigationItem: NhRouteNavigationItem) => {
      const primaryLang = 'en';
      const nhRouteInfo = (<any>route).data[NhRouterSetupService.Nh_ROUTE_DATA_KEY] as NhRouteInfo;
      const myId = nhRouteInfo.id;
      const newParentIds = [...parentIds, myId];
      const searchId = newParentIds.join('/');
      const childRoutes = routes.filter(x => (<any>x).data[NhRouterSetupService.Nh_ROUTE_DATA_KEY].parentIds?.join('/') === searchId) as Routes;

      const resolvePath = (): string => {
        const nhEngRoute = nhRouteInfo.routes.find(x => x.language === primaryLang);
        if(myId === 'root') {
          return '';
        }

        const pathParts: string[] = [nhEngRoute!.path ?? ''];

        if(parentRouteNavigationItem) {
          let currentParent: NhRouteNavigationItem|undefined = parentRouteNavigationItem;
          while(currentParent) {
            const currentParentNhRouteInfo = (<any>currentParent.route).data[NhRouterSetupService.Nh_ROUTE_DATA_KEY] as NhRouteInfo;
            const currentParentNhEngRoute = currentParentNhRouteInfo.routes.find(x => x.language === primaryLang);

            let currentParentNhEngRoutePath = currentParentNhEngRoute?.path ?? '';
            if(currentParentNhRouteInfo.id === 'root') {
              currentParentNhEngRoutePath = '';
            }
            pathParts.unshift(currentParentNhEngRoutePath);
            currentParent = currentParent.parent;
          }
        }

        return '/' + pathParts.filter(x => x.trim() !== '' && x).join('/');
      };

      routeNavigationItem.id = searchId;
      routeNavigationItem.path = resolvePath();
      routeNavigationItem.route = route;
      routeNavigationItem.parent = parentRouteNavigationItem;
      routeNavigationItem.children = [];

      for(const childRoute of childRoutes) {
        const childNavigationItem = new NhRouteNavigationItem();

        routeNavigationItem.children.push(childNavigationItem);
        processRoutes(childRoute, newParentIds, routeNavigationItem, childNavigationItem);
      }
    };

    const rootRoute = routes.find(x => (<any>x).data[NhRouterSetupService.Nh_ROUTE_DATA_KEY].id === 'root') as Route;
    const routeNavRootItem = new NhRouteNavigationItem();

    processRoutes(rootRoute, [], undefined, routeNavRootItem);
    this.rootRouteNavigationItem = routeNavRootItem;
  }

  public static rootRoute(activatedRoute: ActivatedRoute): ActivatedRoute {
    while (activatedRoute.firstChild) {
      activatedRoute = activatedRoute.firstChild;
    }
    return activatedRoute;
  }
}
