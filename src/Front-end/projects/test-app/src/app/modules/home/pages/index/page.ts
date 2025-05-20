import {Component} from "@angular/core";
import {ActivatedRoute} from "@angular/router";
import {
  CollectionHttpRequestOptions,
  FilterRequestOptions,
  ImpersonateAuthenticateModel,
  NhApiService,
  NhPageBaseComponent, RevertImpersonateAuthenticateModel
} from "nh-common";
import {ToastrService} from "ngx-toastr";

@Component({
    selector: 'app-home-page-index',
    templateUrl: './page.html',
    styleUrls: ['./page.scss'],
    standalone: false
})
export class IndexHomePage extends NhPageBaseComponent {
  searchValue: string = '';

  constructor(
    private route: ActivatedRoute,
    private apiService: NhApiService,
    private toastrService: ToastrService
  ) {
    super();
  }

  override async appOnInit() {

  }
  override async appOnInitAndLoadWithSkipBrowserInitial() {
    this.load().then();
    this.pageSettings.title = this.translateService.instant('Dit is de homepage');
    this.pageSettings.description = this.translateService.instant('Dit is de description');
  }

  override async appAfterViewInit() {
    if(this.isPlatformBrowser()) {
      this.apiService.getCollection<any>('https://localhost:5301/address', new CollectionHttpRequestOptions({
        filter: [
          new FilterRequestOptions({
            key: 'place',
            value: null,
            operator: '!='
          })
        ]
      })).subscribe((x) => {
      });
    }

  }

  async load() {
    this.authService.reloadAuthorizationProfile().then();
  }

  search($event: any){

  }

  async logout() {
    this.authService.logout().then();
  }

  async impersonate() {
    const impersonateResult = await this.authService.impersonate(new ImpersonateAuthenticateModel({
      userId: '17e35556-54f2-4975-a563-417eb5fbfa7f'
    }));

    if (!impersonateResult.isSuccess) {
      this.toastrService.error('Impersonate failed', 'Error');
    } else {
      window.location.reload();
    }
  }

  async revertImpersonate() {
    const impersonateResult = await this.authService.impersonateRevert(new RevertImpersonateAuthenticateModel({
    }));

    if (!impersonateResult.isSuccess) {
      this.toastrService.error('Revery impersonate failed', 'Error');
    } else {
      window.location.reload();
    }
  }
}
