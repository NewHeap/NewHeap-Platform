// import {join} from 'path';
// import {Observable} from 'rxjs';
// import {TranslateLoader} from '@ngx-translate/core';
// import {StateKey, makeStateKey, TransferState, inject} from '@angular/core';
// import * as fs from 'fs';
// import { NhCommonModuleConfig } from "../../models/config.models";
//
// export class NhTranslateServerLoader implements TranslateLoader {
//   protected moduleConfig: NhCommonModuleConfig = inject(NhCommonModuleConfig);
//
//   constructor(
//     private transferState: TransferState,
//     private suffix: string = '.json'
//   ) {
//   }
//
//   public getTranslation(lang: string): Observable<any> {
//     return new Observable((observer) => {
//       let assets_folder = '';
//
//       assets_folder = join(
//         process.cwd(),
//         this.moduleConfig.translation.serverLoaderPath
//       );
//
//       if(!fs.existsSync(`${assets_folder}/${lang}${this.suffix}`)) {
//         observer.next({});
//         observer.complete();
//         return;
//       }
//
//       const jsonData = JSON.parse(
//         fs.readFileSync(`${assets_folder}/${lang}${this.suffix}`, 'utf8')
//       );
//
//       // Here we save the translations in the transfer-state
//       const key: StateKey<number> = makeStateKey<number>(
//         'transfer-translate-' + lang
//       );
//       this.transferState.set(key, jsonData);
//
//       observer.next(jsonData);
//       observer.complete();
//     });
//   }
// }
//
// export function nhTranslateServerLoaderFactory(transferState: TransferState) {
//   return new NhTranslateServerLoader(transferState);
// }
