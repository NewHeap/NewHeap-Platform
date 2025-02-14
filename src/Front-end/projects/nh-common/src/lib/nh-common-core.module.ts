import {CommonModule} from '@angular/common';
import { NgModule, Optional, SkipSelf, inject, provideAppInitializer } from '@angular/core';
import {Observable} from "rxjs";
import {NhConfigCommonService} from "nh-common";
import './prototype-extensions/array.extensions';
import './prototype-extensions/observable.extensions';

@NgModule({
  imports: [
    CommonModule,
  ],
  declarations: [
  ],
  providers: [
    provideAppInitializer(() => {
        const configService = inject(NhConfigCommonService);
        return new Observable<unknown>((observer) => {
          //
          // We use APP_INITIALIZER to load the configuration before the application starts. (Cuz DEPS calls for AppConfigService it is loaded soon in the lifecycle of the app.)
          //
          configService.initialize().then(() => {
            observer.next();
            observer.complete();
          }, (err) => {
            observer.error(err);
            observer.complete();
          });
        });
      })
  ],
  exports: [
  ],
})
// @ts-ignore
export class NhCommonCoreModule {

  constructor(@Optional() @SkipSelf() parentModule: NhCommonCoreModule) {
    // Import guard
    if (parentModule) {
      throw new Error(`${parentModule} has already been loaded. Import Core module in the AppModule only.`);
    }
  }
}
