import {Component, ViewChild} from "@angular/core";
import {AuthenticateModel, NhPageBaseComponent, NhRouterService} from "nh-common";
import {UntypedFormControl} from "@angular/forms";

@Component({
    selector: 'app-auth-page-login',
    templateUrl: './page.html',
    styleUrls: ['./page.scss'],
    standalone: false
})
export class LoginAuthPage extends NhPageBaseComponent {
  @ViewChild('appForm') form: any;
  isLoading: boolean = false;
  isSubmitting: boolean = false;
  formData: AuthenticateModel = new AuthenticateModel();

  constructor(
    private routerService: NhRouterService
  ) {
    super();
  }

  override async appOnInit() {

  }
  override async appOnInitAndLoadWithSkipBrowserInitial() {
    this.load().then();
    this.pageSettings.title = this.translateService.instant('Login');
    this.pageSettings.description = this.translateService.instant('Login');
  }

  override async appAfterViewInit() {
  }

  async load() {
    this.formData = new AuthenticateModel();
  }

  async onSubmit(event: any) {
    if(this.isSubmitting) {
      return;
    }

    try {
      this.isSubmitting = true;
      const loginResult = await this.authService.authenticate(this.formData);
      if(loginResult.isSuccess) {
        const authResult = await this.authService.reloadAuthorizationProfile();
        if(authResult.isSuccess) {
          await this.routerService.navigate({ 'id': 'home/index' });
        }
      }
      await this.load();

    } catch (err: any) {

    } finally {
      this.isSubmitting = false;
    }
  }
}
