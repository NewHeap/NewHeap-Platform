import type {BrowserOptions} from "@sentry/browser/build/npm/types/client";
import * as Sentry from "@sentry/angular";
import {ErrorHandlerOptions} from "@sentry/angular";

export class EndpointsAuthenticationNhCommonModuleConfig {
  msAuthenticate: string = '/authentication/oath/microsoft/authorize';
  msRedirectUrl: string = '/authentication/oath/microsoft';
  authorizationFlow: string = '/authentication/method';
  login: string = '/authentication/login';
  logout: string = '/authentication/logout';
  refresh: string = '/authentication/refresh';
  impersonate: string = '/authentication/impersonate';
  revertImpersonate: string = '/authentication/ImpersonateRevert';
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

export class UserNotificationNhCommonModuleConfig {
  urlSuffix: string = '/UserNotification';
  pollingInterval: number = 5000; // in milliseconds

  public constructor(init?: Partial<UserNotificationNhCommonModuleConfig>) {
    Object.assign(this, init);
  }
}

export class NhSentryErrorLoggingNhCommonModuleConfig {
  errorLoggingEnabled: boolean = false;
  tracingEnabled: boolean = false;
  options: BrowserOptions = {};
  errorHandlerOptions: ErrorHandlerOptions = {};

  public constructor(init?: Partial<NhSentryErrorLoggingNhCommonModuleConfig>) {
    Object.assign(this, init);
  }
}

export class NhErrorLoggingNhCommonModuleConfig {
  sentry: NhSentryErrorLoggingNhCommonModuleConfig = new NhSentryErrorLoggingNhCommonModuleConfig();

  public constructor(init?: Partial<NhErrorLoggingNhCommonModuleConfig>) {
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
  authType: 'cookie'|'header' = 'header';
  language: string = '';
  defaultCulture: string = '';
  culture: string = '';
  authenticationRealm: string = '';
  environment: string = '';
  cookieDomain: string = '';
  defaultItemsPerPage: number = 20;
  authentication: AuthenticationNhCommonModuleConfig = new AuthenticationNhCommonModuleConfig();
  userNotification: UserNotificationNhCommonModuleConfig = new UserNotificationNhCommonModuleConfig();
  errorLogging: NhErrorLoggingNhCommonModuleConfig = new NhErrorLoggingNhCommonModuleConfig();

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
