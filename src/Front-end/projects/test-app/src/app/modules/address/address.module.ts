import {NgModule} from '@angular/core';
import {CommonModule} from '@angular/common';
import {TranslateModule} from '@ngx-translate/core';
import {OverviewAddressPage} from "./pages/overview/page";
import {ViewAddressPage} from "./pages/view/page";
import {AddressSharedModule} from "./shared.address.module";
import {FormsModule} from "@angular/forms";
import {AppSharedModule} from "../../shared/app-shared.module";
import {AddressRoutingModule} from "./address-routing.module";
import {NhCommonModule} from "nh-common";
import {NgxDatatableModule} from "@swimlane/ngx-datatable";

@NgModule({
  imports: [
    CommonModule,
    AppSharedModule,
    AddressSharedModule,
    AddressRoutingModule,
    TranslateModule,
    FormsModule,
    NhCommonModule,
    NgxDatatableModule,
  ],
  declarations: [
    OverviewAddressPage,
    ViewAddressPage
  ],
  providers: []
})
export class AddressModule {
}

