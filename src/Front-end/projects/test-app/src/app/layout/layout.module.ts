import {ModuleWithProviders, NgModule} from '@angular/core';
import {DefaultLayoutModule} from './default-layout/default-layout.module';

@NgModule({
  imports: [
    DefaultLayoutModule
  ],
  declarations: [],
  exports: [],
  providers: []
})
export class LayoutModule {
  static forRoot(): ModuleWithProviders<LayoutModule> {
    return {
      ngModule: LayoutModule,
      providers: [
      ]
    };
  }
}
