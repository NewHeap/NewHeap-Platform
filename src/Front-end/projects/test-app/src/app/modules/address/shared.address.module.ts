import {NgModule} from '@angular/core';
import {CommonModule} from '@angular/common';
import {TranslateModule} from "@ngx-translate/core";
import {FormsModule} from "@angular/forms";
import {SharedAuthModule} from "../auth/shared.auth.module";
import {NgxDatatableModule} from "@swimlane/ngx-datatable";
import {MutateAddressComponent} from "./components/mutate/component";
import {TableAddressComponent} from "./components/table/component";
import {RouterLink} from "@angular/router";
import {AppSharedModule} from "../../shared/app-shared.module";
import {NhCommonModule} from "nh-common";
import {AddressService} from "./services/address.service";

@NgModule({
  imports: [
    CommonModule,
    TranslateModule,
    FormsModule,
    AppSharedModule,
    SharedAuthModule,
    NgxDatatableModule,
    RouterLink,
    NhCommonModule
  ],
  declarations: [
    MutateAddressComponent,
    TableAddressComponent,
  ],
  exports: [
    MutateAddressComponent,
    TableAddressComponent,
  ],
  providers: []
})
export class AddressSharedModule {
}
