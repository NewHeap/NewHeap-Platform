import {Observable} from 'rxjs';
import {TranslateLoader, TranslationObject} from '@ngx-translate/core';
import {HttpClient, HttpHeaders} from '@angular/common/http';
import {StateKey, makeStateKey, TransferState, inject} from '@angular/core';
import { NhCommonModuleConfig } from "../../models/config.models";
import { HttpRequestOptions } from "../../models/http.models";

export class NhTranslateBrowserLoader implements TranslateLoader {
  protected moduleConfig: NhCommonModuleConfig = inject(NhCommonModuleConfig);

  protected static readonly httpRequestOptions = new HttpRequestOptions({
    headers: new HttpHeaders({
      'Content-Type': 'application/json',
      'Cache-Control': 'no-cache'
    })
  });

  constructor(private http: HttpClient, private transferState: TransferState) {}

  public getTranslation(lang: string): Observable<any> {

    const key: StateKey<number> = makeStateKey<number>(
      'transfer-translate-' + lang
    );

    const data = this.transferState.get(key, null);

    // First we are looking for the translations in transfer-state,
    // if none found, http load as fallback
    if (data) {
      return new Observable((observer) => {
        observer.next(data);
        observer.complete();
      });
    } else {
      const urlPart = this.moduleConfig.translation.browserLoaderPrefix.endsWith('/')
        ? ''
        : '/';

      return this.http.get<TranslationObject>(
        `${this.moduleConfig.translation.browserLoaderPrefix}${urlPart}${lang}.json`,
        NhTranslateBrowserLoader.httpRequestOptions
      );
    }
  }
}

export function nhTranslateBrowserLoaderFactory(
  httpClient: HttpClient,
  transferState: TransferState
) {
  return new NhTranslateBrowserLoader(httpClient, transferState);
}
