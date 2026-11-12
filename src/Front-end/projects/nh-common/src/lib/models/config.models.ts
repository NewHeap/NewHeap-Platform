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

/**
 * Opt-in support for a browser that is signed in to more than one account, each with a
 * refresh-token session of its own. Off by default: until an application sets `enabled`, the
 * browser keeps one authorization in `localStorage['at']` exactly as before, and nothing else is
 * stored or written. Turning it off again removes the other accounts from the browser at the next
 * start; their sessions are not revoked and expire on the server. Every application on one origin
 * shares `at` and the accounts, so configure the option the same way in each of them.
 */
export class MultipleAccountsAuthenticationNhCommonModuleConfig {
  /** Keeps a directory of signed-in accounts next to the active authorization. */
  enabled: boolean = false;
  /**
   * The most accounts one browser keeps. Signing in to one more forgets the least recently used
   * other account in this browser; its refresh token is not revoked and expires on the server.
   */
  maximum: number = 8;
  /**
   * Name of a cookie that holds only the number of accounts, so a server-side authorize endpoint
   * can decide whether to show an account picker. It never identifies an account and never
   * authorizes anything. The cookie is not written while this is unset.
   */
  hintCookieName?: string;

  public constructor(init?: Partial<MultipleAccountsAuthenticationNhCommonModuleConfig>) {
    Object.assign(this, init);
  }
}

export class AuthenticationNhCommonModuleConfig {
  addAuthTokensToRequests: boolean = true;
  additionalClaimPermissionTypes: string[] = [];
  additionalDivisionClaimPermissionTypes: string[] = [];
  endpoints: EndpointsAuthenticationNhCommonModuleConfig = new EndpointsAuthenticationNhCommonModuleConfig();
  loginPath: string = '/';
  /**
   * Ends the local session when the API answers 401 and the account endpoint confirms that the
   * server no longer accepts the session, for example after a password change on another device.
   */
  endSessionRejectedByServer: boolean = true;
  /** Opt-in: lets the browser stay signed in to more than one account. Off by default. */
  multipleAccounts: MultipleAccountsAuthenticationNhCommonModuleConfig = new MultipleAccountsAuthenticationNhCommonModuleConfig();

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
  /**
   * Refreshes the authentication of the open live-update connection after the session renewed its
   * access token, instead of letting the connection close when the token it opened with expires.
   * Turn it on only together with the server's `NhBackgroundOperationsOptions.HubAuthenticationRefresh`
   * on ASP.NET Core 11. It needs `@microsoft/signalr` 11; with 10 the connection keeps closing on
   * expiry and reconnecting. A refresh that fails restarts the connection, which resynchronizes, and
   * a change of user or active division still restarts it. Default `false`.
   */
  authenticationRefresh: boolean = false;

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
