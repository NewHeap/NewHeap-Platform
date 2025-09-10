import {NgModule} from '@angular/core';
import {CommonModule, NgOptimizedImage} from '@angular/common';
import {HomeRouting} from './home-routing';
import {IndexHomePage} from './pages/index/page';
import {HomeSharedModule} from './home-shared.module';
import {TranslateModule} from "@ngx-translate/core";
import {NhCommonModule} from "nh-common";
import {AppSharedModule} from "../../shared/app-shared.module";
import {FormsModule} from "@angular/forms";
import {SitemapXmlHomePage} from "../sitemap-xml/page";
import {AddressSharedModule} from "../address/shared.address.module";

@NgModule({
  imports: [
    CommonModule,
    HomeSharedModule,
    HomeRouting,
    TranslateModule,
    NhCommonModule,
    AppSharedModule,
    NgOptimizedImage,
    FormsModule,
    AddressSharedModule
  ],
  declarations: [
    IndexHomePage,
    SitemapXmlHomePage
  ],
  providers: []
})
export class HomeModule {
}
