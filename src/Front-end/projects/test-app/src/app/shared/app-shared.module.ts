import {NgModule} from '@angular/core';
import {CommonModule} from '@angular/common';
import {TranslateModule} from "@ngx-translate/core";
import {NhCommonModule} from "nh-common";
import {FormsModule} from "@angular/forms";
import {NhErrorComponent} from "../../../../nh-common/src/lib/components/nh-error/component";

@NgModule({
  imports: [
    CommonModule,
    TranslateModule,
    FormsModule,
    NhCommonModule,
    NhErrorComponent
  ],
  declarations: [
  ],
  exports: [
  ],
  providers: [
  ]
})
export class AppSharedModule {
}
