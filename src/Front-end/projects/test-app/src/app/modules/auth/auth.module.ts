import {NgModule} from '@angular/core';
import {CommonModule} from '@angular/common';
import {TranslateModule} from '@ngx-translate/core';
import {SharedAuthModule} from './shared.auth.module';
import {AuthRoutingModule} from './auth-routing.module';
import {AuthLoginPageComponent} from './pages/login/page';
import {SharedModule} from '../../shared/shared.module';
import {FormsModule} from '@angular/forms';
import {AuthPasswordForgetPageComponent} from './pages/password-forget/page';
import {AuthResetPasswordPageComponent} from './pages/reset-password/page';
import {AuthRegistrationPageComponent} from './pages/registration/page';
import {AuthTokenLoginPageComponent} from './pages/tokenlogin/page';
import {NgbModule} from '@ng-bootstrap/ng-bootstrap';


@NgModule({
  imports: [
    CommonModule,
    TranslateModule,
    NgbModule,
    FormsModule,
    SharedModule,
    SharedAuthModule,
    AuthRoutingModule
  ],
  declarations: [
    AuthLoginPageComponent,
    AuthPasswordForgetPageComponent,
    AuthResetPasswordPageComponent,
    AuthTokenLoginPageComponent,
    AuthRegistrationPageComponent
  ],
  providers: []
})
export class AuthModule {
}
