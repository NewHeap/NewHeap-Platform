import {BaseNhAuthService, NhAuthService} from "../services/nh-auth.service";

export class EndpointsAuthenticationNhCommonModuleConfig {
  login: string = '/authentication/login';
  logout: string = '/authentication/logout';
  refresh: string = '/authentication/refresh';
  accountInformation: string ='/account';

  public constructor(init?: Partial<EndpointsAuthenticationNhCommonModuleConfig>) {
    Object.assign(this, init);
  }
}

export class AuthenticationNhCommonModuleConfig {
  addAuthTokensToRequests: boolean = true;
  additionalClaimPermissionTypes: string[] = [];
  additionalDivisionClaimPermissionTypes: string[] = [];
  endpoints: EndpointsAuthenticationNhCommonModuleConfig = new EndpointsAuthenticationNhCommonModuleConfig();
  loginPath: string = '/';

  public constructor(init?: Partial<AuthenticationNhCommonModuleConfig>) {
    Object.assign(this, init);
  }
}

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
  authentication: AuthenticationNhCommonModuleConfig = new AuthenticationNhCommonModuleConfig();

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
