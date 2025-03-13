export class NhCommonModuleConfig {
  appDisplayName: string = '';
  baseUrl: string = '';
  apiBaseUrl: string = '';
  authApiBaseUrl: string = '';
  defaultLanguage: string = '';
  supportedLanguages: string[] = [];
  language: string = '';
  defaultCulture: string = '';
  culture: string = '';
  authenticationRealm: string = '';
  environment: string = '';
  cookieDomain: string = '';
  authentication = {
    endpoints: {
      login: '/authentication/login',
      logout: '/authentication/logout',
      refresh: '/authentication/refresh'
    }
  }

  public constructor(init?: Partial<NhCommonModuleConfig>) {
    Object.assign(this, init);
  }
}

export class NhCommonConfig {
  languageCode: string = '';
  culture: string = '';

  public constructor(init?: Partial<NhCommonConfig>) {
    Object.assign(this, init);
  }
}

export class NhCommonConfigChanged {
  config: NhCommonConfig = new NhCommonConfig();
  public constructor(init?: Partial<NhCommonConfigChanged>) {
    Object.assign(this, init);
  }
}
