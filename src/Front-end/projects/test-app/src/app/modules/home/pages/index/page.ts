import {Component} from "@angular/core";
import {ActivatedRoute} from "@angular/router";
import {
  CollectionHttpRequestOptions,
  FilterRequestOptions,
  ImpersonateAuthenticateModel,
  NhApiService, NhContextEventWithCoordinates, NhContextMenu, NhContextMenuItem, NhContextMenuService,
  NhPageBaseComponent, NhUserNotificationOverview, NhUserNotificationState, RevertImpersonateAuthenticateModel
} from "nh-common";
import {ToastrService} from "ngx-toastr";
import {NhUserNotificationService} from "../../../../../../../nh-common/src/lib/services/nh-user-notification.service";
import {Subscription} from "rxjs";
import {AccountService, ChangePasswordUserMutateModel} from "../../../../core/services/account.service";

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
    private toastrService: ToastrService,
    private accountService: AccountService,
    private contextMenuService: NhContextMenuService
  ) {
    super();
  }

  override async appOnDestroy(): Promise<void> {
    await super.appOnDestroy();
  }

  override async appOnInit() {
    const urlTest = this.nhRouterService.serializeUrl({
      id: 'home'
    }, {
      queryParams: {
        test: 'test'
      }
    });
    console.log(urlTest);

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

  throwTestError(): void {
    throw new Error("Sentry Test Error");
  }

  async testChangePassword() {
    const changePwModel = await this.accountService.passwordChange(new ChangePasswordUserMutateModel({
      currentPassword: 'NewHeap123!',
      password: 'NewHeap123!',
      confirmPassword: 'NewHeap123!'
    })).taskResultLastValueFrom();

    if(!changePwModel.isSuccess) {
      this.toastrService.error('Change password failed', 'Error');
    } else {
      this.toastrService.success('Change password success', 'Success');
    }
  }

  async onContextMenu(event?: any) {
    event?.preventDefault();

    const contextMenu = NhContextMenu.fromEvent(event).withItems([
      new NhContextMenuItem({
        title: this.translateService.instant('Go to NewHeap'),
        onClick: async (event: any) => {
          window.open('https://newheap.com', "_blank");
        }
      })
    ]);

    this.contextMenuService.open(contextMenu);
  }
}
