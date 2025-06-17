import {NgModule} from '@angular/core';
import {CommonModule} from '@angular/common';
import {TranslateModule} from "@ngx-translate/core";
import {NhCommonModule} from "nh-common";
import {FormsModule} from "@angular/forms";
import {UserNotificationsComponent} from "./components/user-notifications/component";

@NgModule({
  imports: [
    CommonModule,
    TranslateModule,
    FormsModule,
    NhCommonModule
  ],
  declarations: [
    UserNotificationsComponent
  ],
  exports: [
    UserNotificationsComponent
  ],
  providers: [
  ]
})
export class AppSharedModule {
}
