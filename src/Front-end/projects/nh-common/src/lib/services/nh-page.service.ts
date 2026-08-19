import {DOCUMENT} from "@angular/common";
import {
  ApplicationRef,
  Inject,
  Injectable,
  OnDestroy,
  PLATFORM_ID
} from "@angular/core";

import {TranslateService} from "@ngx-translate/core";
import {Subject, Subscription} from "rxjs";
import {NhConfigCommonService} from "./nh-config.service";
import {ActivatedRouteSnapshot, NavigationExtras} from "@angular/router";
import {NhJsonLdData, NhJsonLdService} from "./nh-json-ld.service";
import { NhAuthorization } from "../models/auth.models";
import {NhCommonConfig, NhCommonConfigChanged, NhCommonModuleConfig} from "../models/config.models";
import {NhAppService} from "./nh-app.service";
import {NhAuthService} from "./nh-auth.service";
import {NhTitleService} from "./nh-title.service";
import {NhBreadCrumb, NhRouterService} from "./nh-router.service";
import {NhMetaService} from "./nh-meta.service";
import {NhRouterLink} from "../models/misc.models";

export class NhPageSettings {
  title: string = '';
  description: string = '';
  jsonLdData: NhJsonLdData = new NhJsonLdData();
  breadCrumbOverrideText?: (currentText: string) => string;
  pageData: any;

  public constructor(init?: Partial<NhPageSettings>) {
    Object.assign(this, init);
  }
}

export class TypedNhPageSettings<TPageData> extends NhPageSettings {
  override pageData: TPageData|undefined;

  public constructor(init?: Partial<TypedNhPageSettings<TPageData>>) {
    super();
    Object.assign(this, init);
  }
}

const Nh_PAGE_SETTINGS_TRANSFER_STATE_KEY = 'Nh_PAGE_SETTINGS_TRANSFER_STATE_KEY';

@Injectable({
  providedIn: 'root'
})
export class NhPageService implements OnDestroy {
  private $appRefIsStable: Subscription|undefined;
  private $auth: Subscription;
  private authorization: NhAuthorization | undefined;
  private $config: Subscription;
  private config: NhCommonConfig = this.configService.getConfig();
  public activePageSettings: NhPageSettings = new NhPageSettings();
  private didRestoreTransferState: boolean = false;
  public readonly breadcrumbUpdateRequest = new Subject<void>();

  constructor(
    @Inject(DOCUMENT) private document: Document,
    @Inject(PLATFORM_ID) private platformId: Object,
    private moduleConfig: NhCommonModuleConfig,
    private appRef: ApplicationRef,
    private nhAppService: NhAppService,
    private configService: NhConfigCommonService,
    private authService: NhAuthService,
    private translateService: TranslateService,
    private titleService: NhTitleService,
    private metaService: NhMetaService,
    private jsonLdService: NhJsonLdService,
    private routerService: NhRouterService
  ) {
    this.$config = this.configService.configSubject.subscribe(async (configChanged) => {
      await this.configChanged(configChanged);
    });

    this.$auth = this.authService.authSubject.subscribe(async (authorization) => {
      await this.authChanged(authorization);
    });

    this.$appRefIsStable = this.appRef.isStable.subscribe((isStable) => {
      if(isStable) {
        if(this.isPlatformServer()) {
          this.updateTransferState();
        } else {
          // If No page with transferstate is loaded initially, mark as restored to (extra) prevent storing the state on a new page.
          // We only want to restore the initial render from server on first page.
          if(!this.didRestoreTransferState) {
            this.didRestoreTransferState = true;
          }
        }
      }
    });
  }

  public restoreTransferState() {
    if(this.isPlatformBrowser() && !this.didRestoreTransferState) {
      const activePageSettings = this.nhAppService.getStateTransferData(Nh_PAGE_SETTINGS_TRANSFER_STATE_KEY);
      if(activePageSettings) {
        this.activePageSettings = <any>activePageSettings;
      }
      this.didRestoreTransferState = true;
    }
  }

  public updateTransferState() {
    this.nhAppService.setStateTransferData(Nh_PAGE_SETTINGS_TRANSFER_STATE_KEY, this.activePageSettings);
  }

  isPlatformServer(): boolean {
    return this.nhAppService.isPlatformServer();
  }

  isPlatformBrowser(): boolean {
    return this.nhAppService.isPlatformBrowser();
  }

  isPlatformBrowserInitial() {
    return this.nhAppService.isPlatformBrowserInitial();
  }

  originatedFromServer(): boolean {
    return this.nhAppService.originatedFromServer();
  }

  isAppStable(): boolean {
    return this.nhAppService.isAppStable();
  }

  async ngOnDestroy() {
    await this.clear();
    this.$config?.unsubscribe();
    this.$auth?.unsubscribe();
    this.$appRefIsStable?.unsubscribe();
  }

  private async authChanged(authorization: NhAuthorization | undefined) {
    this.authorization = authorization;
    await this.flushMeta(this.activePageSettings);
  }

  private async configChanged(configChanged: NhCommonConfigChanged) {
    this.config = configChanged.config;
    await this.flushMeta(this.activePageSettings);
  }

  async clear() {
    this.activePageSettings = new NhPageSettings();
    await this.flushMeta(this.activePageSettings);
    this.requestBreadcrumbUpdate();
  }

  async flushMeta(activePageSettings: NhPageSettings) {
    this.setTitle(activePageSettings.title);
    this.setDescription(activePageSettings.description);
    this.metaService.updateTag({property: 'og:type', content: 'website'});
    this.metaService.updateTag({name: 'twitter:card', content: 'summary'});
    this.metaService.updateTag({property: 'og:site_name', content: this.moduleConfig.appDisplayName });
    this.metaService.updateTag({property: 'og:locale', content: this.config?.languageCode});

    this.jsonLdService.clear();

    if(activePageSettings.jsonLdData) {
      for(const jsonLdDataItem of activePageSettings.jsonLdData.items) {
        this.jsonLdService.addItem(jsonLdDataItem);
      }
    }
  }

  setTitle(title: string) {
    this.titleService.setTitle(title);
    this.metaService.updateTag({
      property: 'og:title',
      content: title
    });
  }

  setDescription(description: string) {
    this.metaService.updateTag({ name: 'description', content: description });
    this.metaService.updateTag({property: 'og:description', content: description});
  }

  // async addMetaTag(description: string) {
  //   this.metaService.(description);
  // }

  setBreadcrumbOverrideText(breadcrumbOverrideText?: (currentText: string) => string) {
    this.activePageSettings.breadCrumbOverrideText = breadcrumbOverrideText;
  }

  requestBreadcrumbUpdate() {
    this.breadcrumbUpdateRequest.next();
  }

  async getBreadcrumb(activatedRouteSnapshot: ActivatedRouteSnapshot|null): Promise<NhBreadCrumb> {
    const breadCrumb = await this.routerService.getBreadcrumb(activatedRouteSnapshot);
    if(this.activePageSettings?.breadCrumbOverrideText) {
      const item = breadCrumb.items[breadCrumb.items.length - 1];
      const text = this.activePageSettings.breadCrumbOverrideText(item.text);
      item.text = text
    }

    return breadCrumb;
  }

  navigateTo(nhRouterLink: NhRouterLink, navigationExtras?: NavigationExtras) {
    return this.routerService.navigate(nhRouterLink, navigationExtras);
  }

  navigateTo404NotFound(navigationExtras?: NavigationExtras) {
    return this.routerService.navigate({ id: 'root.home.not-found' }, navigationExtras);
  }
}
