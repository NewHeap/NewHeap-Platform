import {CSP_NONCE, inject, NgModule, Optional, REQUEST_CONTEXT, TransferState} from '@angular/core';
import {provideServerRendering, ServerModule} from '@angular/platform-server';

import { AppModule } from './app.module';
import { AppComponent } from './app.component';
import {TranslateLoader, TranslateModule} from "@ngx-translate/core";
import {provideServerRoutesConfig, RenderMode} from "@angular/ssr";
import {nhWithNodeFetchHttpBackend} from "./miscellaneous/nh-node-fetch.http-backend";
import {nhTranslateServerLoaderFactory} from "./miscellaneous/translate-loaders/translate-server.loader";




@NgModule({
  imports: [
    AppModule,
    //ServerModule,
    TranslateModule.forRoot({
      loader: {
        provide: TranslateLoader,
        useFactory: nhTranslateServerLoaderFactory,
        deps: [TransferState]
      }
    }),
  ],
  bootstrap: [AppComponent],
  providers: [
    provideServerRendering(),
    //Angular 19 (RC 06-11-2024) SSR issue, need to provide some mock routes... (these 2)
    provideServerRoutesConfig([{ path: '', renderMode: RenderMode.Server }, { path: '**', renderMode: RenderMode.Server }]),
    nhWithNodeFetchHttpBackend(),
    {
      provide: CSP_NONCE,
      useFactory: () => {
        const reqContext: any = inject(REQUEST_CONTEXT, {optional: true});
        return (reqContext && reqContext.appNonce) ? reqContext.appNonce : null;
      }
    }
  ]
})
export class AppServerModule {}
