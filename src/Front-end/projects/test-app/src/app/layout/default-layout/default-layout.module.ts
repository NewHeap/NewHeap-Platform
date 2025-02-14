import {NgModule} from '@angular/core';
import {CommonModule} from '@angular/common';
import {RouterModule} from '@angular/router';
import {AppDefaultLayoutComponent} from './default-layout';
import {FormsModule} from '@angular/forms';
import {NhCommonModule} from "nh-common";
import {TranslateModule} from "@ngx-translate/core";

@NgModule({
  imports: [
    CommonModule,
    RouterModule,
    FormsModule,
    NhCommonModule,
    TranslateModule
  ],
  declarations: [
    AppDefaultLayoutComponent,
  ],
  exports: [
    AppDefaultLayoutComponent
  ],
  providers: []
})
export class DefaultLayoutModule {
}
