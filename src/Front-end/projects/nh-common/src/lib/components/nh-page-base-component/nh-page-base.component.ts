import {DOCUMENT} from "@angular/common";
import {
  AfterContentInit, AfterViewInit,
  Component,
  inject,
  OnChanges,
  OnDestroy,
  OnInit, PLATFORM_ID, Type
} from "@angular/core";
import {ActivatedRoute, NavigationEnd, ParamMap, Params, Router} from "@angular/router";
import {filter, Subscription} from "rxjs";

import {TranslateService} from "@ngx-translate/core";
import {NhPageService, NhPageSettings, TypedNhPageSettings} from "../../services/nh-page.service";
import {NhConfigCommonService} from "../../services/nh-config.service";
import {BaseNhAuthService, NhAuthService} from "../../services/nh-auth.service";
import {NhModalService} from "../../services/nh-modal.service";
import {NhRouterService} from "../../services/nh-router.service";
import {NhCommonConfig, NhCommonConfigChanged} from "../../models/config.models";
import {INhAuthorization, NhAuthorization} from "../../models/auth.models";


@Component({
    selector: 'nh-page-base-component-type',
    template: ``,
    standalone: false
})
export abstract class NhPageTypeBaseComponent<TAuthorization extends INhAuthorization, TAuthService extends BaseNhAuthService<TAuthorization>>
  implements
    OnChanges,
    OnInit,
    AfterContentInit,
    AfterViewInit,
    OnDestroy
{
  readonly QUERY_PARAM_KEY_PAGE = 'p';
  readonly QUERY_PARAM_KEY_ITEMS_PER_PAGE = 'ipp';
  readonly QUERY_PARAM_KEY_SEARCH_QUERY = 'sq';
  readonly QUERY_PARAM_KEY_ORDER_BY = 'ob';
  readonly QUERY_PARAM_KEY_VIEW = 'vw';

  protected get pageSettings(): NhPageSettings {
    return this.pageService.activePageSettings;
  };

  protected configService: NhConfigCommonService = inject(NhConfigCommonService);
  protected authService: TAuthService;
  protected translateService: TranslateService = inject(TranslateService);
  protected pageService: NhPageService = inject(NhPageService);
  protected modalService: NhModalService = inject(NhModalService);
  protected nhRouterService: NhRouterService = inject(NhRouterService);
  protected activatedRoute: ActivatedRoute = inject(ActivatedRoute);
  protected document: Document = inject(DOCUMENT)
  protected platformId: Object = inject(PLATFORM_ID);
  protected router: Router = inject(Router);
  private $routeChanged: Subscription|undefined;
  protected config: NhCommonConfig = this.configService.getConfig();
  private $config: Subscription|undefined;
  protected authorization: NhAuthorization | undefined;
  private $auth: Subscription;
  private $activeRouteParams: Subscription|undefined;
  protected activeRouteParams: Params = {};
  private $activeRouteParamMap: Subscription|undefined;
  protected activeRouteParamMap?: ParamMap;
  private $activeQueryParams: Subscription|undefined;
  protected activeQueryParams: Params = {};
  private $activeQueryParamMap: Subscription|undefined;
  protected activeQueryParamMap?: ParamMap;
  private $activeUrlFragment: Subscription|undefined;
  private activeUrlFragment: string|null = null;
  private onInitDidRunOnce: boolean = false;

  get pageData(): any {
    return this.pageSettings.pageData;
  }

  constructor(authServiceType: Type<TAuthService>) {
    this.authService = inject(authServiceType);

    this.pageService.setBreadcrumbOverrideText(undefined);

    this.$config = this.configService.configSubject.subscribe(async (configChanged) => {
      await this.configChanged(configChanged);
    });

    this.$auth = this.authService.authSubject.subscribe(async (authorization) => {
      await this.authChanged(authorization);
    });

    this.pageSettings.pageData = this._getInitialPageData();
    this.pageService.restoreTransferState();
    this.pageSettings.pageData ??= this._getInitialPageData();
  }

  protected _getInitialPageData(): unknown {
    return {};
  }

  isPlatformServer(): boolean {
    return this.pageService.isPlatformServer()
  }

  isPlatformBrowser(): boolean {
    return this.pageService.isPlatformBrowser()
  }

  originatedFromServer(): boolean {
    return this.pageService.originatedFromServer();
  }

  isPlatformBrowserInitial() {
    return this.pageService.isPlatformBrowserInitial();
  }

  private async configChanged(configChanged: NhCommonConfigChanged) {
    this.config = configChanged.config;
  }

  private async authChanged(authorization: NhAuthorization | undefined) {
    this.authorization = authorization;
  }

  async flushMeta() {
    await this.pageService.flushMeta(this.pageSettings);
  }

  async ngOnInit() {
    this.$routeChanged?.unsubscribe();
    this.$routeChanged = this.router.events.pipe(
      filter(event => event instanceof NavigationEnd)
    ).subscribe(async (event: unknown) => {
      // Angular has ngOnInit, but it will not be called if u navigate to a page that uses the same component.
      // So we use the route change detection to flush the meta-data again.
      this.flushMeta().then();
      this.pageService.requestBreadcrumbUpdate();
    });

    this.appOnInit().then(x => {
      this.appOnInitAndLoad().then(async () => {
        this._appOnInitAndLoadWithSkipBrowserInitial().then(async () => {
          this.flushMeta().then();
          this.pageService.requestBreadcrumbUpdate();
        });
      });

      this.onInitDidRunOnce = true;
    });
  }

  async ngOnChanges() {
    this.appOnChanges().then();
  }

  async ngAfterContentInit() {
    this.appAfterContentInit().then();
  }

  async ngAfterViewInit() {
    this.activeRouteParams = this.activatedRoute?.snapshot?.params ?? {};
    this.$activeRouteParams?.unsubscribe();
    this.$activeRouteParams = this.activatedRoute.params.subscribe(p => {
      this.activeRouteParams = p;
      if(this.onInitDidRunOnce && this.pageService.isAppStable()) {
        this.appOnInitAndLoad().then(async () => {
          this._appOnInitAndLoadWithSkipBrowserInitial().then(async () => {
            this.flushMeta().then();
            this.pageService.requestBreadcrumbUpdate();
          });
          this.flushMeta().then();
          this.pageService.requestBreadcrumbUpdate();
        });
      }
    });

    this.$activeRouteParamMap?.unsubscribe();
    this.$activeRouteParamMap = this.activatedRoute.paramMap.subscribe(p => {
      this.activeRouteParamMap = p;
    });

    this.$activeQueryParams?.unsubscribe();
    this.$activeQueryParams = this.activatedRoute.queryParams.subscribe(p => {
      this.activeQueryParams = p;
    });

    this.$activeQueryParamMap?.unsubscribe();
    this.$activeRouteParamMap = this.activatedRoute.queryParamMap.subscribe(p => {
      this.activeQueryParamMap = p;
    });

    this.$activeUrlFragment?.unsubscribe();
    this.$activeUrlFragment = this.activatedRoute.fragment.subscribe(p => {
      this.activeUrlFragment = p;
    });

    this.appAfterViewInit().then();
  }

  async ngOnDestroy() {
    this.appOnDestroy().then(async() => {
      this.$routeChanged?.unsubscribe();
      this.$activeRouteParams?.unsubscribe();
      this.$activeRouteParamMap?.unsubscribe();
      this.$activeQueryParams?.unsubscribe();
      this.$activeQueryParamMap?.unsubscribe();
      this.$config?.unsubscribe();
      this.$auth?.unsubscribe();
    });
  }

  appOnInitAndLoad(): Promise<void> {
    return Promise.resolve();
  }

  private _appOnInitAndLoadWithSkipBrowserInitial(): Promise<void> {
    if(this.isPlatformBrowserInitial()) {
      return Promise.resolve();
    }

    return this.appOnInitAndLoadWithSkipBrowserInitial();
  }
  appOnInitAndLoadWithSkipBrowserInitial(): Promise<void> {
    return Promise.resolve();
  }

  appOnChanges(): Promise<void> {
    return Promise.resolve();
  }

  appOnInit(): Promise<void> {
    return Promise.resolve();
  }

  appAfterContentInit(): Promise<void> {
    return Promise.resolve();
  }

  appAfterViewInit(): Promise<void> {
    return Promise.resolve();
  }

  appOnDestroy(): Promise<void> {
    return Promise.resolve();
  }
}

@Component({
  selector: 'nh-page-base-component',
  template: ``,
  standalone: false
})
export abstract class NhPageBaseComponent extends NhPageTypeBaseComponent<NhAuthorization, NhAuthService> {
  constructor() {
    super(NhAuthService);
  }
}

@Component({
    selector: 'nh-page-base-component-type-typed',
    template: ``,
    standalone: false
})
export abstract class NhTypedPageTypeBaseComponent<TPageData, TAuthorization extends INhAuthorization, TAuthService extends BaseNhAuthService<TAuthorization>> extends NhPageTypeBaseComponent<TAuthorization, TAuthService> {
  protected override get pageSettings(): TypedNhPageSettings<TPageData> {
    return this.pageService.activePageSettings as TypedNhPageSettings<TPageData>;
  };

  protected override _getInitialPageData(): unknown {
    return this.getInitialPageData();
  };

  protected abstract getInitialPageData(): TPageData|undefined;

  override get pageData(): TPageData|undefined {
    return this.pageSettings.pageData as TPageData;
  }

  constructor(authServiceType: Type<TAuthService>) {
    super(authServiceType);
  }
}

@Component({
  selector: 'nh-page-base-component-typed',
  template: ``,
  standalone: false
})
export abstract class NhTypedPageBaseComponent<TPageData> extends NhTypedPageTypeBaseComponent<TPageData, NhAuthorization, NhAuthService> {
  constructor() {
    super(NhAuthService);
  }
}
