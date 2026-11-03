import {Observable, catchError, throwError} from 'rxjs';
import {TranslateLoader, TranslationObject} from '@ngx-translate/core';
import {HttpClient, HttpErrorResponse, HttpHeaders} from '@angular/common/http';
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

  /**
   * The application's texts of `lang`. A regional language such as `nl-NL`, `nl_BE` or `en-GB` whose
   * file does not exist (404) gets the texts of its base language, `nl.json` or `en.json`, as the
   * libraries' own texts do; a regional file that exists is used as it is. Other errors propagate.
   */
  public getTranslation(lang: string): Observable<any> {
    const baseLanguage = NhTranslateBrowserLoader.baseLanguageOf(lang);
    if (!baseLanguage) {
      return this.load(lang);
    }

    return this.load(lang).pipe(
      catchError((error: unknown) => NhTranslateBrowserLoader.isMissing(error)
        ? this.load(baseLanguage)
        : throwError(() => error))
    );
  }

  /** The base language of a regional tag (`nl` for `nl-NL` or `nl_BE`); `undefined` without a region. */
  private static baseLanguageOf(lang: string): string | undefined {
    const [base, ...region] = lang.split(/[-_]/);
    return base && region.length > 0 ? base.toLowerCase() : undefined;
  }

  /** Whether the answer says that the translation file does not exist. */
  private static isMissing(error: unknown): boolean {
    return error instanceof HttpErrorResponse && error.status === 404;
  }

  /** The texts of exactly `lang`. */
  private load(lang: string): Observable<any> {
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
