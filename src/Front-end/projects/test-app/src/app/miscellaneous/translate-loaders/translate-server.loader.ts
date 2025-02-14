import {join} from 'path';
import {Observable} from 'rxjs';
import {TranslateLoader} from '@ngx-translate/core';
import {StateKey, makeStateKey, TransferState, Inject, PLATFORM_ID, inject} from '@angular/core';
import * as fs from 'fs';
import {environment} from "../../../environments/environment";

export class TranslateServerLoader implements TranslateLoader {
  private platformId: Object;

  constructor(
    private transferState: TransferState,
    private prefix: string = 'i18n',
    private suffix: string = '.json'
  ) {
    this.platformId = inject(PLATFORM_ID);
    this.platformId = inject(PLATFORM_ID);
  }

  public getTranslation(lang: string): Observable<any> {
    return new Observable((observer) => {
      let assets_folder = '';

      if(environment.name === 'development') {
        assets_folder = join(
          process.cwd(),
          'projects',
          'webshop',
          'src',
          'assets',
          this.prefix
        );
      } else {
        assets_folder = join(
          process.cwd(),
          'dist',
          'test-app', // Project name
          'browser',
          'assets',
          this.prefix
        );
      }

      if(!fs.existsSync(`${assets_folder}/${lang}${this.suffix}`)) {
        observer.next({});
        observer.complete();
        return;
      }

      const jsonData = JSON.parse(
        fs.readFileSync(`${assets_folder}/${lang}${this.suffix}`, 'utf8')
      );

      // Here we save the translations in the transfer-state
      const key: StateKey<number> = makeStateKey<number>(
        'transfer-translate-' + lang
      );
      this.transferState.set(key, jsonData);

      observer.next(jsonData);
      observer.complete();
    });
  }
}

export function translateServerLoaderFactory(transferState: TransferState) {
  return new TranslateServerLoader(transferState);
}
