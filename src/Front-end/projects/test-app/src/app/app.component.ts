import {
  Component,
  Inject,
  OnDestroy,
  OnInit,
  PLATFORM_ID,
  ViewContainerRef
} from '@angular/core';
import {
  NhAuthorization, NhAuthService,
  NhHeadService,
  NhJsonLdService,
  NhModalService, NhServerService,
  PreConnectUrlItem,
  PreLoadUrlItem
} from "nh-common";
import {environment} from "../environments/environment";
import {Subscription} from "rxjs";
import {CookieService} from "ngx-cookie-service";
import {isPlatformBrowser} from "@angular/common";
import {AuthService} from "./core/services/auth.service";

@Component({
    selector: 'app-root',
    templateUrl: './app.component.html',
    styleUrl: './app.component.scss',
    standalone: false
})
export class AppComponent implements OnInit, OnDestroy {
  $jsonLdData: Subscription;
  jsonLdStructuredData: any|undefined;
  preConnectUrlItems: PreConnectUrlItem[] = [];
  preLoadUrlItems: PreLoadUrlItem[] = [];
  showCookie = false;
  protected authorization: NhAuthorization | undefined;
  private $auth: Subscription;

  constructor(
    private serverService: NhServerService,
    private modalService: NhModalService,
    private viewContainerRef: ViewContainerRef,
    private jsonLdService: NhJsonLdService,
    private headService: NhHeadService,
    private cookieService: CookieService,
    private authService: AuthService,
    @Inject(PLATFORM_ID) private platformId: Object,
  )
  {
    this.modalService.setViewContainerRef(this.viewContainerRef);
    this.$jsonLdData = this.jsonLdService.dataSubject.subscribe(async (changesModel) => {
      this.jsonLdStructuredData = this.jsonLdService.build();
    })

    //Check if browser (angular service) has cookie consent otherwise showCookie = true
    if(isPlatformBrowser(this.platformId)) {
      if(cookieService.get('cookieconsent_status') === "") {
        this.showCookie = true;
      }
    }

    this.$auth = this.authService.authSubject.subscribe(async (authorization) => {
      await this.authChanged(authorization);
    });
  }

  private async authChanged(authorization: NhAuthorization | undefined) {
    this.authorization = authorization;
  }

  acceptCookie() {
    this.cookieService.set('cookieconsent_status', 'allow');
    this.showCookie = false;
  }

  denyCookie() {
    this.cookieService.set('cookieconsent_status', 'deny');
    this.showCookie = false;
  }

  async ngOnInit() {
    this.initPreConnectUrls();
    this.initPreLoadUrls();
  }

  ngOnDestroy() {
    this.$jsonLdData?.unsubscribe();
  }

  initPreConnectUrls() {
    if(!this.serverService.didInitByServer()) {
      // this.preConnectUrlItems.push(new PreConnectUrlItem({
      //   preConnect: true,
      //   dnsPrefetch: true,
      //   url: environment.shopApi.baseUrl,
      //   withCrossOrigin: false,
      //   crossOrigin: undefined
      // }));

      this.preConnectUrlItems.push(new PreConnectUrlItem({
        preConnect: true,
        dnsPrefetch: true,
        url: 'https://www.google-analytics.com',
        withCrossOrigin: true,
        crossOrigin: undefined
      }));

      this.preConnectUrlItems.push(new PreConnectUrlItem({
        preConnect: true,
        dnsPrefetch: true,
        url: 'https://connect.facebook.net',
        withCrossOrigin: true,
        crossOrigin: undefined
      }));

      for(const preConnectUrlItem of this.preConnectUrlItems) {
        this.headService.addPreConnectUrl(preConnectUrlItem);
      }
    }
  }

  initPreLoadUrls() {
    if(!this.serverService.didInitByServer()) {
      for(const preLoadUrlItem of this.preLoadUrlItems) {
        this.headService.addPreLoadUrl(preLoadUrlItem);
      }
    }
  }
}
