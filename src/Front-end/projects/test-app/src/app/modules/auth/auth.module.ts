import {NgModule} from '@angular/core';
import {CommonModule} from '@angular/common';
import {TranslateModule} from '@ngx-translate/core';
import {SharedAuthModule} from './shared.auth.module';
import {AuthRoutingModule} from './auth-routing.module';
import {FormsModule} from '@angular/forms';
import {AppSharedModule} from "../../shared/app-shared.module";
import {LoginAuthPage} from "./pages/login/page";


@NgModule({
  imports: [
    CommonModule,
    TranslateModule,
    FormsModule,
    AppSharedModule,
    SharedAuthModule,
    AuthRoutingModule
  ],
  declarations: [
    LoginAuthPage,
  ],
  providers: []
})
export class AuthModule {
}
