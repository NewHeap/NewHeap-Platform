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
  twoFactorVerify: string = '/authentication/two-factor/verify';
  twoFactorStatus: string = '/account/two-factor';
  twoFactorAuthenticator: string = '/account/two-factor/authenticator';
  twoFactorAuthenticatorConfirm: string = '/account/two-factor/authenticator/confirm';
  twoFactorRecoveryCodes: string = '/account/two-factor/recovery-codes';
  twoFactorDisable: string = '/account/two-factor/disable';
  twoFactorForgetDevices: string = '/account/two-factor/forget-devices';
  twoFactorEmailSetup: string = '/account/two-factor/email';
  twoFactorEmailConfirm: string = '/account/two-factor/email/confirm';
  twoFactorEmail: string = '/authentication/two-factor/email';
  twoFactorPasskeyOptions: string = '/authentication/two-factor/passkey/options';
  twoFactorPasskey: string = '/authentication/two-factor/passkey';
  twoFactorEnrollmentAuthenticator: string = '/authentication/two-factor/enrollment/authenticator';
  twoFactorEnrollmentAuthenticatorConfirm: string = '/authentication/two-factor/enrollment/authenticator/confirm';
  twoFactorEnrollmentEmail: string = '/authentication/two-factor/enrollment/email';
  twoFactorEnrollmentEmailConfirm: string = '/authentication/two-factor/enrollment/email/confirm';
  twoFactorEnrollmentPasskeyOptions: string = '/authentication/two-factor/enrollment/passkey/options';
  twoFactorEnrollmentPasskey: string = '/authentication/two-factor/enrollment/passkey';
  /** Administration route; `{userId}` is replaced with the user ID. */
  twoFactorUserReset: string = '/authentication/two-factor/users/{userId}/reset';
  twoFactorEnrollmentReminders: string = '/authentication/two-factor/operations/enrollment-reminders';
  twoFactorSessionRevocation: string = '/authentication/two-factor/operations/session-revocation';
  passkeySignInOptions: string = '/authentication/passkey/options';
  passkeySignIn: string = '/authentication/passkey/login';
  passkeys: string = '/account/passkeys';
  passkeyRegistrationOptions: string = '/account/passkeys/options';

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

export class BackgroundOperationsNhCommonModuleConfig {
  urlSuffix: string = '/background-operations';
  administrationUrlSuffix: string = '/background-operations/administration';
  hubBaseUrl?: string;
  hubUrlSuffix: string = '/hub/background-operations';
  pollingInterval: number = 5000;
  liveUpdatesEnabled: boolean = true;
  listPageSize: number = 100;

  public constructor(init?: Partial<BackgroundOperationsNhCommonModuleConfig>) {
    Object.assign(this, init);
  }
}
export class NhTranslationNhCommonModuleConfig {
  browserLoaderPrefix: string = './assets/i18n/';
  serverLoaderPath: string = 'assets/i18n/';
  public constructor(init?: Partial<NhTranslationNhCommonModuleConfig>) {
    Object.assign(this, init);
  }
}

export class NhFormDropDownNhCommonModuleConfig {
  deferLazyLoadUntilOpened: boolean = false;

  public constructor(init?: Partial<NhFormDropDownNhCommonModuleConfig>) {
    Object.assign(this, init);
  }
}

export class NhHttpNhCommonModuleConfig {
  deduplicateGetRequests: boolean = false;
  deduplicateGetRequestHeaderNames: string[] = [
    'accept',
    'accept-language',
    'authorization',
    'cookie',
    'x-nh-activedivisionid'
  ];

  public constructor(init?: Partial<NhHttpNhCommonModuleConfig>) {
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
  defaultDateFormat: string = 'dd-MM-yyyy';
  defaultDateTimeFormat: string = 'dd-MM-yyyy HH:mm:ss';
  authentication: AuthenticationNhCommonModuleConfig = new AuthenticationNhCommonModuleConfig();
  userNotification: UserNotificationNhCommonModuleConfig = new UserNotificationNhCommonModuleConfig();
  backgroundOperations: BackgroundOperationsNhCommonModuleConfig = new BackgroundOperationsNhCommonModuleConfig();
  translation: NhTranslationNhCommonModuleConfig = new NhTranslationNhCommonModuleConfig();
  formDropdown: NhFormDropDownNhCommonModuleConfig = new NhFormDropDownNhCommonModuleConfig();
  http: NhHttpNhCommonModuleConfig = new NhHttpNhCommonModuleConfig();

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
