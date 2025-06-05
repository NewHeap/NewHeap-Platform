import {Inject, Injectable, OnDestroy, Optional, PLATFORM_ID, REQUEST_CONTEXT, TransferState} from "@angular/core";
import {BehaviorSubject, lastValueFrom} from "rxjs";
import {Base64} from "js-base64";
import {DOCUMENT, isPlatformBrowser, isPlatformServer} from "@angular/common";
import {TranslateService} from "@ngx-translate/core";
import {NhCommonConfig, NhCommonConfigChanged, NhCommonModuleConfig} from "../models/config.models";
import {NhAuthService} from "./nh-auth.service";
import {NhCookieService} from "./nh-cookie.service";
import {languageToCultureMap} from "../util/nh-common-util";

@Injectable({
  providedIn: 'root',
})
export class NhConfigCommonService implements OnDestroy {
  private static STORAGE_KEY = 'test_app_ac';
  private static COOKIE_KEY_LANGUAGE_CODE = 'shop.userLanguage';
  private didInitialize: boolean = false;
  private readonly localStorage?: Storage;
  private config: NhCommonConfig;
  public readonly configSubject: BehaviorSubject<NhCommonConfigChanged>;

  constructor(
    @Inject(DOCUMENT) private document: Document,
    @Inject(PLATFORM_ID) private platformId: Object,
    @Optional() @Inject(REQUEST_CONTEXT) private requestContext: any,
    private authService: NhAuthService,
    private translateService: TranslateService,
    private cookieService: NhCookieService,
    private commonModuleConfig: NhCommonModuleConfig
  ) {
    this.localStorage = this.document.defaultView?.localStorage as Storage;
    this.config = this.getConfig(false);
    this.configSubject = new BehaviorSubject<NhCommonConfigChanged>(new NhCommonConfigChanged({ config: this.config }));
  }

  async ngOnDestroy() {
  }



  async initialize() {
    if(this.didInitialize) {
      return;
    }

    const config = this.getConfig();

    config.languageCode = this.resolveInitialLanguageCode();

    this.translateService.onLangChange.subscribe(async () => {
      if(isPlatformBrowser(this.platformId)) {
        this.cookieService.set(NhConfigCommonService.COOKIE_KEY_LANGUAGE_CODE, this.config.languageCode, { sameSite: 'Strict', domain: this.commonModuleConfig.cookieDomain });
      }

      this.commonModuleConfig.language = this.config.languageCode;
      this.commonModuleConfig.culture = (<any>languageToCultureMap)[this.config.languageCode];
    });

    this.translateService.setDefaultLang(this.commonModuleConfig.defaultLanguage);
    await lastValueFrom(this.translateService.use(this.config.languageCode));
    this.commonModuleConfig.culture = (<any>languageToCultureMap)[this.config.languageCode];

    if(isPlatformBrowser(this.platformId)) {
      this.cookieService.set(NhConfigCommonService.COOKIE_KEY_LANGUAGE_CODE, this.config.languageCode, { sameSite: 'Strict', domain: this.commonModuleConfig.cookieDomain });
    }

    this.didInitialize = true;
  }

  public getInitialConfig(): NhCommonConfig {
    return new NhCommonConfig({
      languageCode: this.commonModuleConfig.defaultLanguage,
      culture: this.commonModuleConfig.defaultCulture,
    });
  }

  private resolveInitialLanguageCode(): string {
    const url = new URL(this.document.URL);
    let languageCode = this.commonModuleConfig.defaultLanguage;
    const urlPaths = url.pathname.split('/').filter(x => x?.trim() !== undefined && x?.trim() !== '');

    const getLanguageCookieValue = () => {
      return this.cookieService.get(NhConfigCommonService.COOKIE_KEY_LANGUAGE_CODE);
    };

    const resolver = () => {
      if(isPlatformServer(this.platformId)) {
        // Server side: the custom cookie we set our self with a fallback to the request accept language header, and the last fallback to the default language.
        try {
          languageCode = getLanguageCookieValue() ?? (<any>this?.requestContext?.request?.headers)['accept-language']?.toString()?.trim()?.substring(0, 2) ?? this.commonModuleConfig.defaultLanguage;
        } catch {
          languageCode = getLanguageCookieValue() ?? this?.requestContext?.request?.headers?.get('accept-language')?.toString()?.trim()?.substring(0, 2) ?? this.commonModuleConfig.defaultLanguage;
        }

      } else if(isPlatformBrowser(this.platformId)) {
        // Client side: the custom cookie we set our self with a fallback to the browser language, and the last fallback to the default language.
        languageCode = getLanguageCookieValue() ?? navigator.language ?? this.commonModuleConfig.defaultLanguage;
      } else {
        languageCode = this.commonModuleConfig.defaultLanguage;
      }
    };

    const isHomepage = urlPaths.length <= 0;
    if(isHomepage) {
      // We don't have a language code in the URL, so we need to determine the language code based on;
      resolver();
    } else {
      // If it is not the homepage, we always have a language code in the URL.
      // This means segment position 0 is the language code.
      languageCode = urlPaths[0];
    }

    languageCode = (languageCode ?? this.commonModuleConfig.defaultLanguage).trim().toLowerCase();
    if(languageCode.length > 2) {
      languageCode = this.commonModuleConfig.defaultLanguage;
    }

    if(languageCode.length != 2) {
      // Looks like we failed to get a nice language code, so we fall back to the default language.
      languageCode = this.commonModuleConfig.defaultLanguage;
    }

    let isSupportedLanguageCode = this.commonModuleConfig.supportedLanguages.some(x => x === languageCode);
    if(!isSupportedLanguageCode) {
      resolver();
    }

    // Check again, navigator or accept lang etc may be non-supported.
    isSupportedLanguageCode = this.commonModuleConfig.supportedLanguages.some(x => x === languageCode);
    if(!isSupportedLanguageCode) {
      languageCode = this.commonModuleConfig.defaultLanguage;
    }

    return languageCode;
  }

  public resetConfig(): void {
    if(!isPlatformServer(this.platformId)) {
      this.localStorage!.removeItem(NhConfigCommonService.STORAGE_KEY);
    }
    this.config = this.getInitialConfig();
    this.setConfig(this.config);
  }

  public setConfig(config: NhCommonConfig): void {
    const changed = new NhCommonConfigChanged();

    if(!isPlatformServer(this.platformId)) {
      this.localStorage!.setItem(NhConfigCommonService.STORAGE_KEY, Base64.encode((JSON.stringify(config))));
    }

    this.config = config;
    changed.config = this.config;

    this.configSubject?.next(changed);
  }

  public getConfig(fromCache: boolean = true): NhCommonConfig {
    let config = this.config;
    if (!fromCache) {
      const configString = isPlatformServer(this.platformId) ? undefined : this.localStorage!.getItem(NhConfigCommonService.STORAGE_KEY);
      if (configString) {
        this.config = JSON.parse(Base64.decode(configString));
      } else {
        this.resetConfig();
      }

      config = this.config;
    }

    return config;
  }

  public async changeLanguage(newLanguageCode: string): Promise<void> {
    const currentLang = this.translateService.currentLang;
    if(newLanguageCode === currentLang) {
      return;
    }

    this.config.languageCode = newLanguageCode;
    this.setConfig(this.config);
    await lastValueFrom(this.translateService.use(newLanguageCode));
  }
}
