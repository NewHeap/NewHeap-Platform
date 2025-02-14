import {NgModule} from '@angular/core';
import {CommonModule} from '@angular/common';
import {TranslateModule} from "@ngx-translate/core";
import {NhCommonModule} from "nh-common";
import {FormsModule} from "@angular/forms";
import {NhSharedErrorComponent} from "../../../../nh-common/src/lib/components/nh-error/component";

@NgModule({
  imports: [
    CommonModule,
    TranslateModule,
    FormsModule,
    NhCommonModule,
    NhSharedErrorComponent
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
