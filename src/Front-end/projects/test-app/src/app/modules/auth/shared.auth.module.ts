import {NgModule} from '@angular/core';
import {CommonModule} from '@angular/common';
import {TranslateModule} from '@ngx-translate/core';
import {AccountService} from './services/account.service';
import {AccountChangePasswordComponent} from './components/account-change-password/component';
import {SharedModule} from '../../shared/shared.module';
import {NgbModule} from '@ng-bootstrap/ng-bootstrap';
import {FormsModule} from '@angular/forms';

@NgModule({
  imports: [
    CommonModule,
    TranslateModule,
    NgbModule,
    FormsModule,
    SharedModule
  ],
  declarations: [
    AccountChangePasswordComponent
  ],
  providers: [
    AccountService
  ],
  exports: [
    AccountChangePasswordComponent
  ]
})
export class SharedAuthModule {
}
