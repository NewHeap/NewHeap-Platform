import {inject, Injectable, NgZone, OnDestroy, PLATFORM_ID, REQUEST_CONTEXT} from '@angular/core';
import {BehaviorSubject, lastValueFrom, Observable, timeout} from 'rxjs';
import {
  AuthenticateModel, AuthenticationFlow,
  AuthenticationSessionCreateResponse,
  AuthSessionExpirationInformation,
  Claim,
  ClaimTypes,
  ImpersonateAuthenticateModel,
  INhAuthorization,
  NhAccountInformationResponse,
  NhAuthenticationStep,
  NhAuthenticationStepStatuses,
  NhAuthenticatorSetup,
  NhAuthorization,
  NhBrowserAccount,
  NhBrowserAccountFailureCodes,
  NhBrowserAccountIdentity,
  NhDivision,
  NhLoginResponse,
  NhPasskey,
  NhPasskeyOptions,
  NhSessionRenewalOptions,
  NhTwoFactorChallenge,
  NhTwoFactorChange,
  NhTwoFactorEmailCodeSent,
  NhTwoFactorEnrollmentResult,
  NhTwoFactorFailureCodes,
  NhTwoFactorReauthentication,
  NhTwoFactorStatus,
  NhTwoFactorVerifyModel,
  RefreshTokenLoginAccountMutateModel,
  RevertImpersonateAuthenticateModel
} from "../models/auth.models";
import {DateTime} from "luxon";
import {TaskResult} from "../models/misc.models";
import {HttpClient, HttpContext, HttpContextToken, HttpErrorResponse, HttpHeaders, HttpParams} from '@angular/common/http';
import {
  EndpointsAuthenticationNhCommonModuleConfig,
  MultipleAccountsAuthenticationNhCommonModuleConfig,
  NhCommonModuleConfig
} from "../models/config.models";
import {NhApiUtil} from "../util/nh-api-util";
import {isPlatformServer} from "@angular/common";
import {Base64} from "js-base64";
import {NhPasskeyClient} from "./nh-passkey-client.service";
import {NhBackgroundOperation} from "../models/background-operation.models";
import {
  nhAccessTokenUserId,
  nhBrowserAccountDirectoryStorageKey,
  nhHasExpired,
  nhReadBrowserAccountDirectory,
  nhRefreshTokenExpiry,
  nhRunWithStorageLease,
  NhStoredBrowserAccount,
  nhWriteBrowserAccountDirectory
} from "./nh-browser-account-directory";

/** Marks a request whose 401 must not start a server session check, such as the check itself. */
export const NH_SKIP_SERVER_SESSION_CHECK = new HttpContextToken<boolean>(() => false);

interface NhTwoFactorChangeResponse {
  recoveryCodes?: string[] | null;
  session?: NhLoginResponse | null;
}

/** A session that can be renewed: its authorization, the account it belongs to and its sign-in name. */
interface NhRenewableSession<TAuthorization extends INhAuthorization> {
  authorization: TAuthorization;
  userId: string | undefined;
  userName: string | undefined;
}

@Injectable()
export abstract class BaseNhAuthService<TAuthorization extends INhAuthorization> implements OnDestroy {
  protected static readonly pendingTwoFactorChallengeStorageKey = 'nh-two-factor-challenge';
  protected static readonly externalTwoFactorChallengePrefix = 'nh-two-factor=';
  protected static readonly rememberDeviceStorageKey = 'nh-two-factor-device';
  protected static readonly serverSessionConfirmationMilliseconds = 30_000;
  /** `renewSession()` keeps an access token that stays valid for longer than this. */
  protected static readonly sessionRenewalMarginMilliseconds = 60_000;
  /**
   * One lock for every request that writes the browser's authentication cookies (renewal, account
   * switch and sign-out), because the server writes those cookies for whichever account it serves.
   */
  protected static readonly sessionLockName = 'nh-authentication-session';
  /** A request made while holding the session lock fails after this, so no tab holds it for long. */
  protected static readonly sessionLockRequestTimeoutMilliseconds = 30_000;
  /**
   * The logout endpoint's confirmation that it left the browser's cookies alone. A server on another
   * origin must expose it through CORS, as NewHeap.Platform.AspNet.Common does; without it the
   * active session is renewed instead, which writes its cookies again.
   */
  protected static readonly authenticationCookiesHeaderName = 'X-NH-Authentication-Cookies';

  protected authorization: TAuthorization | undefined = undefined;
  protected pendingTwoFactorChallenge: NhTwoFactorChallenge | undefined = undefined;
  protected authSession: AuthenticationSessionCreateResponse | undefined = undefined;
  public readonly authSubject = new BehaviorSubject<TAuthorization | undefined>(this.getAuthorization());
  protected onReady: ((value: (PromiseLike<unknown> | unknown)) => void) | undefined;
  public readonly authReady: Promise<unknown>;

  protected _sessionExpirationInformation = new BehaviorSubject<AuthSessionExpirationInformation>(this.getSessionExpirationInformation());
  public sessionExpirationInformationChanged = this._sessionExpirationInformation.asObservable();
  protected intervalHandle: any;
  private serverSessionCheck: Promise<boolean> | undefined;
  private serverSessionConfirmedAt = 0;
  private sessionRejectedByServer = false;

  private readonly browserAccountsSubject = new BehaviorSubject<NhBrowserAccount[]>([]);
  /**
   * Emits the accounts of this browser whenever they change, also when another tab changed them.
   * Always empty while `authentication.multipleAccounts` is off.
   */
  public readonly accountsChanged: Observable<NhBrowserAccount[]> = this.browserAccountsSubject.asObservable();
  private readonly sessionRenewalsInFlight = new Map<string, Promise<TaskResult<TAuthorization>>>();
  /** The name typed at the sign-in that is still pending, such as a second-factor challenge. */
  private pendingSignInUserName: string | undefined;
  /** The sign-in name of the session with this refresh token, until the token rotates. */
  private sessionSignInUserName: {refreshToken: string, userName: string} | undefined;
  private browserAccountStorageListener: ((event: StorageEvent) => void) | undefined;

  protected zone: NgZone = inject(NgZone);
  protected moduleConfig: NhCommonModuleConfig = inject(NhCommonModuleConfig);
  protected httpClient: HttpClient = inject(HttpClient);
  protected platformId: Object = inject(PLATFORM_ID);
  protected requestContext: any = inject(REQUEST_CONTEXT, {optional: true});
  protected passkeyClient: NhPasskeyClient = inject(NhPasskeyClient);

  constructor() {
    this.authReady = new Promise(resolve => {
      this.onReady = resolve;
    });

    if (!isPlatformServer(this.platformId)) {
      // Before the timer starts, so an invalid account configuration leaves nothing running.
      this.startBrowserAccountDirectory();

      this.intervalHandle = setInterval(() => {
        this.zone.run(() => {
          this.dispatchSessionExpirationInformationChanged();
        });
      }, 1000);
    }
  }

  dispatchSessionExpirationInformationChanged() {
    const sessionExpirationInfo = this.getSessionExpirationInformation();
    this._sessionExpirationInformation.next(sessionExpirationInfo);
  }

  ngOnDestroy() {
    if (this.intervalHandle) {
      clearInterval(this.intervalHandle);
    }

    if (this.browserAccountStorageListener) {
      window.removeEventListener('storage', this.browserAccountStorageListener);
      this.browserAccountStorageListener = undefined;
    }
  }

  private getAllPermissionClaimTypes() {
    let types = new Array<string>();
    types.push(ClaimTypes.Permission);

    for (const type of this.moduleConfig?.authentication?.additionalClaimPermissionTypes ?? []) {
      types.push(type);
    }

    return types;
  }

  private getAllDivisionPermissionClaimTypes() {
    let types = new Array<string>();
    types.push(ClaimTypes.DivisionPermission);

    for (const type of this.moduleConfig?.authentication?.additionalDivisionClaimPermissionTypes ?? []) {
      types.push(type);
    }

    return types;
  }

  public getSessionExpirationInformation() {
    const result = new AuthSessionExpirationInformation({
      isAuthenticated: this.isAuthenticated()
    });

    if (result.isAuthenticated) {
      const auth = this.getAuthorization()!;
      const nowDate = DateTime.utc();
      const authDate = DateTime.fromISO(auth.validTo ?? '').toUTC();
      const duration = authDate.diff(nowDate);

      result.expiresWithin15MinutesOrExpired = duration.minutes <= 15;
      if (duration.seconds >= 0) {
        result.displayMinutesSecondsUntilExpiration = `${(duration.minutes < 10 && duration.minutes >= 0) ? '0' : ''}${duration.minutes}:${(duration.seconds < 10 && duration.seconds >= 0) ? '0' : ''}${duration.seconds}`;
      } else {
        result.displayMinutesSecondsUntilExpiration = `00:00`;
      }
    } else {
      result.expiresWithin15MinutesOrExpired = true;
      result.displayMinutesSecondsUntilExpiration = `00:00`;
      result.rejectedByServer = this.sessionRejectedByServer;
    }

    return result;
  }

  /**
   * Ends the local session: removes the stored authorization record and the cached authorization,
   * also when its access token has already expired, so neither a later request nor a reload can use
   * the record or the refresh token it holds. When there was an authorization to remove, `authSubject`
   * emits `undefined` (unless `doDispatchEvent` is `false`) and the session expiration information
   * is dispatched again.
   *
   * With `authentication.multipleAccounts` enabled it ends the active account's session only: that
   * account and its refresh token leave the browser's account directory, the other accounts stay.
   */
  public clearAuthorization(doDispatchEvent: boolean = true): void {
    let hadAuthorization = this.authorization !== undefined;
    // The stored record names the account every tab shares, also when this tab's copy is older.
    const clearedAuthorization = this.readStoredAuthorization() ?? this.authorization;

    try {
      hadAuthorization = localStorage.getItem('at') !== null || hadAuthorization;
      localStorage.removeItem('at');
    } catch {
      // Without storage, as during server rendering, there is no stored record to remove.
    }

    this.authorization = undefined;
    this.forgetBrowserAccountOf(clearedAuthorization);
    if (!hadAuthorization) {
      return;
    }

    if (doDispatchEvent) {
      this.authSubject.next(this.authorization);
    }
    this.dispatchSessionExpirationInformationChanged();
  }

  /**
   * Stores `auth` as the active authorization. Every sign-in, second factor, enrollment, passkey,
   * renewal and profile reload stores its session here. With `authentication.multipleAccounts`
   * enabled, the account it belongs to is added to or refreshed in the browser's account directory;
   * a session without a refresh token, such as an impersonation, is never added.
   */
  public setAuthorization(auth: TAuthorization, noEvent = false): void {
    localStorage.setItem('at', Base64.encode(JSON.stringify(auth)));
    this.authorization = auth;
    this.sessionRejectedByServer = false;
    this.serverSessionConfirmedAt = 0;
    this.rememberBrowserAccount(auth);
    if (!noEvent) {
      this.authSubject.next(this.authorization);
    }
    this.dispatchSessionExpirationInformationChanged();
  }

  protected abstract initEmptyAuthorization(): TAuthorization;

  public getAuthorization(fromCache: boolean = true): TAuthorization | undefined {
    let auth: TAuthorization = this.initEmptyAuthorization();

    if (fromCache && this.authorization) {
      return this.authorization;
    }

    try {
      auth = JSON.parse(Base64.decode(localStorage.getItem('at') ?? ''));
      this.authorization = auth;
    } catch (ex) {
    }

    return auth;
  }

  public isAuthenticated(): boolean {
    const auth = this.getAuthorization();
    let authenticated = true;
    authenticated = ((auth?.claims?.length ?? 0) > 0);

    if (authenticated && auth) {
      authenticated = (DateTime.fromISO(auth.validTo ?? '') >= DateTime.utc());
    }

    return authenticated;
  }

  public isImpersonating(): boolean {
    return this.isAuthenticated() && !!this.getAuthorization()?.claims?.find(x => x.type === ClaimTypes.ImpersonateOriginUserId);
  }

  public getActiveDivision(): NhDivision | null {
    let division: NhDivision | null = null;

    const auth = this.getAuthorization();

    if (auth && auth.user && auth.activeDivision) {
      division = auth.activeDivision;
    }

    return division;
  }

  public getActiveDivisionId(): string | undefined {
    let divisionId: string | undefined = undefined;
    const auth = this.getAuthorization();

    if ((auth?.user?.activeDivisionId?.length ?? 0) > 0) {
      divisionId = auth!.user!.activeDivisionId;
    }

    return divisionId;
  }

  public isGrantedRole(roles: string | string[]): boolean {

    if (typeof roles === 'string') {
      roles = [roles];
    }

    for (const role of roles) {
      const claim = <Claim>{
        value: role,
        type: ClaimTypes.Role
      };

      if (this.isClaimGranted(claim)) {
        return true;
      }
    }

    return false;
  }

  public isClaimGranted(claim: Claim) {
    const auth = this.getAuthorization();
    return (null != auth && auth.claims != null && null != auth.claims.find((x: Claim) => x.type === claim.type && x.value === claim.value));
  }

  public isOneClaimGranted(claims: Array<Claim>) {
    if (!claims || claims.length < 1) {
      return true;
    }

    for (const claim of claims) {
      if (this.isClaimGranted(claim)) {
        return true;
      }
    }

    return false;
  }

  public isOnePermissionGranted(permissions: Array<string>) {
    if (!permissions || permissions.length < 1) {
      return true;
    }

    for (const permission of permissions) {
      for (const claimType of this.getAllPermissionClaimTypes()) {
        if (this.isClaimGranted(<Claim>{type: claimType, value: permission})) {
          return true;
        }
      }
    }

    return false;
  }

  /**
   * Returns true only when every requested permission is granted through one
   * of the configured permission claim types. An empty requirement is allowed.
   */
  public isAllPermissionsGranted(permissions: Array<string>) {
    if (!permissions || permissions.length < 1) {
      return true;
    }

    return permissions.every(permission =>
      this.getAllPermissionClaimTypes().some(claimType =>
        this.isClaimGranted(<Claim>{type: claimType, value: permission}))
    );
  }

  public isOneRoleGranted(roles: Array<string>) {
    if (!roles || roles.length < 1) {
      return true;
    }

    for (const role of roles) {
      if (this.isClaimGranted(<Claim>{type: ClaimTypes.Role, value: role})) {
        return true;
      }
    }

    return false;
  }

  public isOneDivisionPermissionGranted(divisionId: string | undefined, permissions: Array<string>) {
    if (!permissions || permissions.length < 1) {
      return true;
    }

    for (const permission of permissions) {
      for (const claimType of this.getAllDivisionPermissionClaimTypes()) {
        if (this.isClaimGranted(<Claim>{type: claimType, value: (divisionId ?? '') + '_' + permission})) {
          return true;
        }
      }
    }

    return false;
  }

  public isOneDivisionRoleGranted(divisionId: string | undefined, roles: Array<string>) {
    if (!roles || roles.length < 1) {
      return true;
    }

    for (const role of roles) {
      if (this.isClaimGranted(<Claim>{type: ClaimTypes.DivisionRole, value: (divisionId ?? '') + '_' + role})) {
        return true;
      }
    }

    return false;
  }

  public isOneActiveDivisionPermissionGranted(permissions: Array<string>) {
    return this.isOneDivisionPermissionGranted(this.getActiveDivisionId(), permissions);
  }

  public isOneActiveDivisionRoleGranted(roles: Array<string>) {
    return this.isOneDivisionRoleGranted(this.getActiveDivisionId(), roles);
  }

  public getAuthenticationFlow(username: string): Promise<TaskResult<AuthenticationFlow>> {
    const model = {username: username};
    return this.httpClient.post<AuthenticationFlow>(this.moduleConfig.authApiBaseUrl + this.moduleConfig.authentication.endpoints.authorizationFlow, model, {
      withCredentials: true
    }).taskResultLastValueFrom();
  }

  public getMicrosoftRedirectUrl(callbackUrl: string, username: string): Promise<TaskResult<string>> {
    return this.httpClient.post<string>(this.moduleConfig.authApiBaseUrl + this.moduleConfig.authentication.endpoints.msRedirectUrl, {
      callbackUrl: callbackUrl,
      userName: username
    }, {
      withCredentials: true
    }).taskResultLastValueFrom();
  }

  public authorizeMicrosoft(code: string, state: string): Promise<TaskResult<TAuthorization>> {
    return this.httpClient.post<TAuthorization>(this.moduleConfig.authApiBaseUrl + this.moduleConfig.authentication.endpoints.msAuthenticate, {
      code: code,
      state: state,
    }, {
      withCredentials: true
    }).taskResultLastValueFrom();
  }

  /**
   * Signs in with username and password and stores the complete session.
   *
   * When the server requires a second factor this method fails with
   * `NhTwoFactorFailureCodes.Required` and stores nothing. Use `authenticateInteractive`
   * to receive the challenge, or read it with `getPendingTwoFactorChallenge()`.
   */
  async authenticate(model: AuthenticateModel, loginAsUser: boolean = false): Promise<TaskResult<TAuthorization>> {
    const result = new TaskResult<TAuthorization>();
    const stepResult = await this.authenticateInteractive(model);

    if (!stepResult.isSuccess) {
      stepResult.copyTo(result);
      return result;
    }

    if (stepResult.data?.status === 'two-factor-required') {
      return result.withError(NhTwoFactorFailureCodes.Required, 'nh-two-factor.required');
    }

    if (stepResult.data?.status === 'enrollment-required') {
      return result.withError(NhTwoFactorFailureCodes.EnrollmentRequired, 'nh-two-factor.enrollment-required');
    }

    result.data = stepResult.data?.authorization;
    return result;
  }

  /**
   * Signs in with username and password. Returns the stored authorization, the
   * second-factor challenge or the required enrollment. A pending step is never stored as
   * an authorization.
   *
   * A failed sign-in ends the current session, unless `authentication.multipleAccounts` is
   * enabled: then signing in to another account that fails leaves the active account signed in.
   */
  async authenticateInteractive(model: AuthenticateModel): Promise<TaskResult<NhAuthenticationStep<TAuthorization>>> {
    const result = new TaskResult<NhAuthenticationStep<TAuthorization>>();
    model.realm = this.moduleConfig.authenticationRealm;
    model.rememberDeviceToken = model.rememberDeviceToken ?? this.getRememberDeviceToken();
    // The refresh endpoint finds the account by the same name the sign-in used.
    this.pendingSignInUserName = model.username || undefined;

    const request$ = this.httpClient.post<NhLoginResponse>(this.moduleConfig.authApiBaseUrl + this.moduleConfig.authentication.endpoints.login, model, {
      params: this.languageParams(),
      withCredentials: true
    });

    try {
      result.data = this.completeAuthenticationStep(await lastValueFrom(request$));
    } catch (ex) {
      this.pendingSignInUserName = undefined;

      if (this.isAuthenticated() && !this.multipleAccountsEnabled()) {
        this.clearAuthorization();
      }

      const errResult = NhApiUtil.taskResultFromResponse(ex);
      errResult.copyTo(result);
    }

    return result;
  }

  /**
   * Completes a second-factor challenge and stores the session.
   */
  async verifyTwoFactor(model: NhTwoFactorVerifyModel): Promise<TaskResult<TAuthorization>> {
    const result = new TaskResult<TAuthorization>();

    const request$ = this.httpClient.post<NhLoginResponse>(this.moduleConfig.authApiBaseUrl + this.twoFactorEndpoints().twoFactorVerify, model, {
      params: this.languageParams(),
      withCredentials: true
    });

    try {
      result.data = this.completeAuthenticationStep(await lastValueFrom(request$)).authorization;
    } catch (ex) {
      this.pendingStepFailed(ex).copyTo(result);
    }

    return result;
  }

  /**
   * E-mails a sign-in code for the pending challenge when the user enrolled the e-mail factor.
   */
  sendTwoFactorEmailCode(challengeToken: string): Promise<TaskResult<NhTwoFactorEmailCodeSent>> {
    return this.sendTwoFactorRequest(
      this.httpClient.post<NhTwoFactorEmailCodeSent>(
        this.twoFactorUrl(this.twoFactorEndpoints().twoFactorEmail),
        {challengeToken: challengeToken},
        this.twoFactorStepRequestOptions()),
      response => new NhTwoFactorEmailCodeSent(response));
  }

  /**
   * Whether this browser can use passkeys.
   */
  isPasskeySupported(): boolean {
    return this.passkeyClient.isSupported();
  }

  /**
   * Completes the pending challenge with a passkey and stores the session.
   */
  async verifyTwoFactorWithPasskey(challengeToken: string, rememberDevice: boolean = false): Promise<TaskResult<TAuthorization>> {
    const result = new TaskResult<TAuthorization>();
    const endpoints = this.twoFactorEndpoints();

    const options = await this.sendTwoFactorRequest(
      this.httpClient.post<NhPasskeyOptions>(
        this.twoFactorUrl(endpoints.twoFactorPasskeyOptions),
        {challengeToken: challengeToken},
        this.twoFactorStepRequestOptions()),
      response => new NhPasskeyOptions(response));
    if (!options.isSuccess) {
      options.copyTo(result);
      return result;
    }

    const credential = await this.runPasskeyCeremony(() => this.passkeyClient.get(options.data!.options));
    if (!credential.isSuccess) {
      credential.copyTo(result);
      return result;
    }

    const request$ = this.httpClient.post<NhLoginResponse>(
      this.twoFactorUrl(endpoints.twoFactorPasskey),
      {
        challengeToken: challengeToken,
        ceremonyToken: options.data!.ceremonyToken,
        credential: credential.data,
        rememberDevice: rememberDevice
      },
      this.twoFactorStepRequestOptions());

    try {
      result.data = this.completeAuthenticationStep(await lastValueFrom(request$)).authorization;
    } catch (ex) {
      this.pendingStepFailed(ex).copyTo(result);
    }

    return result;
  }

  /**
   * Signs in with a discoverable passkey, without a username or password. Returns the
   * stored authorization, or a pending step when the server's policy asks for more.
   */
  async signInWithPasskey(): Promise<TaskResult<NhAuthenticationStep<TAuthorization>>> {
    const result = new TaskResult<NhAuthenticationStep<TAuthorization>>();
    const endpoints = this.twoFactorEndpoints();
    // A discoverable passkey names no account; a name typed earlier belongs to another sign-in.
    this.pendingSignInUserName = undefined;

    const options = await this.sendTwoFactorRequest(
      this.httpClient.post<NhPasskeyOptions>(
        this.twoFactorUrl(endpoints.passkeySignInOptions),
        {},
        this.twoFactorStepRequestOptions()),
      response => new NhPasskeyOptions(response));
    if (!options.isSuccess) {
      options.copyTo(result);
      return result;
    }

    const credential = await this.runPasskeyCeremony(() => this.passkeyClient.get(options.data!.options));
    if (!credential.isSuccess) {
      credential.copyTo(result);
      return result;
    }

    const request$ = this.httpClient.post<NhLoginResponse>(
      this.twoFactorUrl(endpoints.passkeySignIn),
      {
        ceremonyToken: options.data!.ceremonyToken,
        credential: credential.data,
        rememberDeviceToken: this.getRememberDeviceToken()
      },
      this.twoFactorStepRequestOptions());

    try {
      result.data = this.completeAuthenticationStep(await lastValueFrom(request$));
    } catch (ex) {
      NhApiUtil.taskResultFromResponse(ex).copyTo(result);
    }

    return result;
  }

  /**
   * Starts authenticator enrollment for a user whom the policy requires to enroll during
   * sign-in. `enrollmentToken` is the `challengeToken` of the enrollment step.
   */
  beginEnrollmentAuthenticatorSetup(enrollmentToken: string): Promise<TaskResult<NhAuthenticatorSetup>> {
    return this.sendTwoFactorRequest(
      this.httpClient.post<NhAuthenticatorSetup>(
        this.twoFactorUrl(this.twoFactorEndpoints().twoFactorEnrollmentAuthenticator),
        {enrollmentToken: enrollmentToken},
        this.twoFactorStepRequestOptions()),
      response => new NhAuthenticatorSetup(response));
  }

  /**
   * Confirms the authenticator during a required enrollment and stores the session.
   */
  confirmEnrollmentAuthenticator(enrollmentToken: string, code: string): Promise<TaskResult<NhTwoFactorEnrollmentResult<TAuthorization>>> {
    return this.sendEnrollmentCompletion(
      this.twoFactorEndpoints().twoFactorEnrollmentAuthenticatorConfirm,
      {enrollmentToken: enrollmentToken, code: code});
  }

  /**
   * E-mails a confirmation code during a required enrollment.
   */
  sendEnrollmentEmailCode(enrollmentToken: string): Promise<TaskResult<NhTwoFactorEmailCodeSent>> {
    return this.sendTwoFactorRequest(
      this.httpClient.post<NhTwoFactorEmailCodeSent>(
        this.twoFactorUrl(this.twoFactorEndpoints().twoFactorEnrollmentEmail),
        {enrollmentToken: enrollmentToken},
        this.twoFactorStepRequestOptions()),
      response => new NhTwoFactorEmailCodeSent(response));
  }

  /**
   * Confirms the e-mail factor during a required enrollment and stores the session.
   */
  confirmEnrollmentEmail(enrollmentToken: string, code: string): Promise<TaskResult<NhTwoFactorEnrollmentResult<TAuthorization>>> {
    return this.sendEnrollmentCompletion(
      this.twoFactorEndpoints().twoFactorEnrollmentEmailConfirm,
      {enrollmentToken: enrollmentToken, code: code});
  }

  /**
   * Registers a passkey during a required enrollment and stores the session.
   */
  async enrollPasskey(enrollmentToken: string, name?: string): Promise<TaskResult<NhTwoFactorEnrollmentResult<TAuthorization>>> {
    const result = new TaskResult<NhTwoFactorEnrollmentResult<TAuthorization>>();
    const endpoints = this.twoFactorEndpoints();

    const options = await this.sendTwoFactorRequest(
      this.httpClient.post<NhPasskeyOptions>(
        this.twoFactorUrl(endpoints.twoFactorEnrollmentPasskeyOptions),
        {enrollmentToken: enrollmentToken},
        this.twoFactorStepRequestOptions()),
      response => new NhPasskeyOptions(response));
    if (!options.isSuccess) {
      options.copyTo(result);
      return result;
    }

    const credential = await this.runPasskeyCeremony(() => this.passkeyClient.create(options.data!.options));
    if (!credential.isSuccess) {
      credential.copyTo(result);
      return result;
    }

    return this.sendEnrollmentCompletion(endpoints.twoFactorEnrollmentPasskey, {
      enrollmentToken: enrollmentToken,
      ceremonyToken: options.data!.ceremonyToken,
      credential: credential.data,
      name: name
    });
  }

  /**
   * Returns the pending second-factor challenge of this browser tab, if it has not expired.
   */
  getPendingTwoFactorChallenge(): NhTwoFactorChallenge | undefined {
    const challenge = this.pendingTwoFactorChallenge ?? this.readStoredTwoFactorChallenge();
    if (!challenge) {
      return undefined;
    }

    if (challenge.expiresAt && DateTime.fromISO(challenge.expiresAt) <= DateTime.now()) {
      this.clearPendingTwoFactorChallenge();
      return undefined;
    }

    this.pendingTwoFactorChallenge = challenge;
    return challenge;
  }

  clearPendingTwoFactorChallenge(): void {
    this.pendingTwoFactorChallenge = undefined;
    this.pendingSignInUserName = undefined;

    if (isPlatformServer(this.platformId)) {
      return;
    }

    try {
      sessionStorage.removeItem(BaseNhAuthService.pendingTwoFactorChallengeStorageKey);
    } catch {
      // Storage can be unavailable in private browsing; the in-memory challenge is cleared.
    }
  }

  /**
   * Reads a challenge that an external sign-in (Microsoft OAuth) passed back in the URL
   * fragment, stores it as the pending challenge and removes it from the address bar.
   */
  consumeExternalTwoFactorChallenge(fragment?: string): NhTwoFactorChallenge | undefined {
    const browser = !isPlatformServer(this.platformId);
    const source = fragment ?? (browser ? window.location.hash : '');
    const value = source.startsWith('#') ? source.substring(1) : source;

    if (!value.startsWith(BaseNhAuthService.externalTwoFactorChallengePrefix)) {
      return undefined;
    }

    let challenge: NhTwoFactorChallenge;
    try {
      const payload = value.substring(BaseNhAuthService.externalTwoFactorChallengePrefix.length);
      challenge = new NhTwoFactorChallenge(JSON.parse(Base64.decode(payload)));
    } catch {
      return undefined;
    }

    if (!challenge.challengeToken) {
      return undefined;
    }

    this.setPendingTwoFactorChallenge(challenge);

    if (browser && fragment === undefined) {
      window.history.replaceState(window.history.state, '', window.location.pathname + window.location.search);
    }

    return challenge;
  }

  async getTwoFactorStatus(): Promise<TaskResult<NhTwoFactorStatus>> {
    const result = new TaskResult<NhTwoFactorStatus>();
    const request$ = this.httpClient.get<NhTwoFactorStatus>(
      this.moduleConfig.authApiBaseUrl + this.twoFactorEndpoints().twoFactorStatus,
      this.twoFactorAccountRequestOptions()
    );

    try {
      result.data = new NhTwoFactorStatus(await lastValueFrom(request$));
    } catch (ex) {
      const errResult = NhApiUtil.taskResultFromResponse(ex);
      errResult.copyTo(result);
    }

    return result;
  }

  /**
   * Starts authenticator enrollment. Restarting enrollment while two-factor authentication
   * is enabled requires reauthentication.
   */
  async beginAuthenticatorSetup(reauthentication?: NhTwoFactorReauthentication): Promise<TaskResult<NhAuthenticatorSetup>> {
    const result = new TaskResult<NhAuthenticatorSetup>();
    const request$ = this.httpClient.post<NhAuthenticatorSetup>(
      this.moduleConfig.authApiBaseUrl + this.twoFactorEndpoints().twoFactorAuthenticator,
      {reauthentication: reauthentication},
      this.twoFactorAccountRequestOptions()
    );

    try {
      result.data = new NhAuthenticatorSetup(await lastValueFrom(request$));
    } catch (ex) {
      const errResult = NhApiUtil.taskResultFromResponse(ex);
      errResult.copyTo(result);
    }

    return result;
  }

  /**
   * Confirms authenticator enrollment. The server ends every other session; the renewed
   * session for this device is stored before the promise resolves.
   */
  confirmAuthenticator(code: string): Promise<TaskResult<NhTwoFactorChange>> {
    return this.sendTwoFactorChange(this.twoFactorEndpoints().twoFactorAuthenticatorConfirm, {code: code});
  }

  regenerateRecoveryCodes(reauthentication: NhTwoFactorReauthentication): Promise<TaskResult<NhTwoFactorChange>> {
    return this.sendTwoFactorChange(this.twoFactorEndpoints().twoFactorRecoveryCodes, {reauthentication: reauthentication});
  }

  disableTwoFactor(reauthentication: NhTwoFactorReauthentication): Promise<TaskResult<NhTwoFactorChange>> {
    return this.sendTwoFactorChange(this.twoFactorEndpoints().twoFactorDisable, {reauthentication: reauthentication});
  }

  /**
   * E-mails a code that confirms the user's e-mail address as a second factor. Requires
   * reauthentication while two-factor authentication is enabled.
   */
  beginEmailSetup(reauthentication?: NhTwoFactorReauthentication): Promise<TaskResult<NhTwoFactorEmailCodeSent>> {
    return this.sendTwoFactorRequest(
      this.httpClient.post<NhTwoFactorEmailCodeSent>(
        this.twoFactorUrl(this.twoFactorEndpoints().twoFactorEmailSetup),
        {reauthentication: reauthentication},
        this.twoFactorAccountRequestOptions()),
      response => new NhTwoFactorEmailCodeSent(response));
  }

  confirmEmailSetup(code: string): Promise<TaskResult<NhTwoFactorChange>> {
    return this.sendTwoFactorChange(this.twoFactorEndpoints().twoFactorEmailConfirm, {code: code});
  }

  /**
   * Forgets every remembered device, including this one, and ends every other session.
   */
  async forgetTwoFactorDevices(reauthentication: NhTwoFactorReauthentication): Promise<TaskResult<NhTwoFactorChange>> {
    const result = await this.sendTwoFactorChange(this.twoFactorEndpoints().twoFactorForgetDevices, {reauthentication: reauthentication});
    if (result.isSuccess) {
      this.storeRememberDeviceToken(undefined);
    }

    return result;
  }

  getPasskeys(): Promise<TaskResult<NhPasskey[]>> {
    return this.sendTwoFactorRequest(
      this.httpClient.get<NhPasskey[]>(
        this.twoFactorUrl(this.twoFactorEndpoints().passkeys),
        this.twoFactorAccountRequestOptions()),
      response => (response ?? []).map(passkey => new NhPasskey(passkey)));
  }

  /**
   * Registers a passkey on this device. Adding a passkey while two-factor authentication is
   * enabled requires reauthentication. The server ends every other session; the renewed
   * session for this device is stored before the promise resolves.
   */
  async registerPasskey(name?: string, reauthentication?: NhTwoFactorReauthentication): Promise<TaskResult<NhTwoFactorChange>> {
    const result = new TaskResult<NhTwoFactorChange>();
    const endpoints = this.twoFactorEndpoints();

    const options = await this.sendTwoFactorRequest(
      this.httpClient.post<NhPasskeyOptions>(
        this.twoFactorUrl(endpoints.passkeyRegistrationOptions),
        {reauthentication: reauthentication},
        this.twoFactorAccountRequestOptions()),
      response => new NhPasskeyOptions(response));
    if (!options.isSuccess) {
      options.copyTo(result);
      return result;
    }

    const credential = await this.runPasskeyCeremony(() => this.passkeyClient.create(options.data!.options));
    if (!credential.isSuccess) {
      credential.copyTo(result);
      return result;
    }

    return this.sendTwoFactorChange(endpoints.passkeys, {
      ceremonyToken: options.data!.ceremonyToken,
      credential: credential.data,
      name: name
    });
  }

  renamePasskey(passkeyId: string, name: string): Promise<TaskResult<void>> {
    return this.sendTwoFactorRequest(
      this.httpClient.put<void>(
        this.twoFactorUrl(this.twoFactorEndpoints().passkeys) + '/' + encodeURIComponent(passkeyId),
        {name: name},
        this.twoFactorAccountRequestOptions()),
      () => undefined);
  }

  /**
   * Removes a passkey. Removing the last second factor disables two-factor authentication,
   * which fails when the policy requires it.
   */
  removePasskey(passkeyId: string, reauthentication: NhTwoFactorReauthentication): Promise<TaskResult<NhTwoFactorChange>> {
    return this.sendTwoFactorChange(
      this.twoFactorEndpoints().passkeys + '/' + encodeURIComponent(passkeyId) + '/remove',
      {reauthentication: reauthentication});
  }

  /**
   * Removes every second factor of another user who lost access to them. Requires the
   * server's two-factor administration policy.
   */
  resetUserTwoFactor(userId: string): Promise<TaskResult<void>> {
    const endpoint = this.twoFactorEndpoints().twoFactorUserReset.replace('{userId}', encodeURIComponent(userId));
    return this.sendTwoFactorRequest(
      this.httpClient.post<void>(this.twoFactorUrl(endpoint), {}, this.twoFactorAccountRequestOptions()),
      () => undefined);
  }

  /**
   * Starts a background operation that reminds every user whom the policy requires to
   * enroll. A repeated idempotency key returns the operation that was already started.
   */
  startTwoFactorEnrollmentReminders(idempotencyKey: string): Promise<TaskResult<NhBackgroundOperation>> {
    return this.sendTwoFactorRequest(
      this.httpClient.post<NhBackgroundOperation>(
        this.twoFactorUrl(this.twoFactorEndpoints().twoFactorEnrollmentReminders),
        {idempotencyKey: idempotencyKey},
        this.twoFactorAccountRequestOptions()),
      response => response);
  }

  /**
   * Starts a background operation that ends the sessions of every user whom the policy
   * requires to enroll, so their next sign-in enrolls them.
   */
  startTwoFactorSessionRevocation(idempotencyKey: string): Promise<TaskResult<NhBackgroundOperation>> {
    return this.sendTwoFactorRequest(
      this.httpClient.post<NhBackgroundOperation>(
        this.twoFactorUrl(this.twoFactorEndpoints().twoFactorSessionRevocation),
        {idempotencyKey: idempotencyKey},
        this.twoFactorAccountRequestOptions()),
      response => response);
  }

  protected async sendTwoFactorChange(endpoint: string, body: object): Promise<TaskResult<NhTwoFactorChange>> {
    const result = new TaskResult<NhTwoFactorChange>();
    const request$ = this.httpClient.post<NhTwoFactorChangeResponse>(
      this.moduleConfig.authApiBaseUrl + endpoint,
      body,
      this.twoFactorAccountRequestOptions()
    );

    try {
      const response = await lastValueFrom(request$);
      const sessionRenewed = this.applyRenewedSession(response?.session);

      result.data = new NhTwoFactorChange({
        recoveryCodes: response?.recoveryCodes ?? undefined,
        sessionRenewed: sessionRenewed
      });
    } catch (ex) {
      const errResult = NhApiUtil.taskResultFromResponse(ex);
      errResult.copyTo(result);
    }

    return result;
  }

  /**
   * Stores the session that the server issued for this device after a change that ended
   * every session. The user, claims and divisions of the current authorization are kept.
   */
  protected applyRenewedSession(session: NhLoginResponse | null | undefined): boolean {
    const authorization = this.getAuthorization();
    if (!session?.token || !authorization) {
      return false;
    }

    authorization.token = session.token;
    authorization.validTo = session.validTo ?? authorization.validTo;
    authorization.refreshToken = session.refreshToken ?? undefined;
    authorization.refreshTokenExpires = session.refreshValidTo ?? undefined;

    this.setAuthorization(authorization);
    return true;
  }

  protected toAuthorization(response: NhLoginResponse): TAuthorization {
    const authorization = response as unknown as TAuthorization;
    delete (authorization as NhLoginResponse).twoFactor;
    delete (authorization as NhLoginResponse).rememberDeviceToken;
    authorization.realm = this.moduleConfig.authenticationRealm;
    return authorization;
  }

  /**
   * Stores a complete session, or keeps a second-factor challenge or enrollment as the
   * pending step of this browser tab.
   */
  protected completeAuthenticationStep(response: NhLoginResponse): NhAuthenticationStep<TAuthorization> {
    if (response?.twoFactor) {
      const challenge = new NhTwoFactorChallenge(response.twoFactor);
      this.setPendingTwoFactorChallenge(challenge);

      return new NhAuthenticationStep<TAuthorization>({
        status: challenge.status === NhAuthenticationStepStatuses.enrollmentRequired ? 'enrollment-required' : 'two-factor-required',
        challenge: challenge
      });
    }

    if (response?.rememberDeviceToken) {
      this.storeRememberDeviceToken(response.rememberDeviceToken);
    }

    const authorization = this.toAuthorization(response);
    this.rememberSessionSignInUserName(authorization.refreshToken, this.pendingSignInUserName);
    this.pendingSignInUserName = undefined;
    this.setAuthorization(authorization);
    this.clearPendingTwoFactorChallenge();
    return new NhAuthenticationStep<TAuthorization>({status: 'authenticated', authorization: authorization});
  }

  /**
   * Converts a failed challenge or enrollment request and forgets the pending step when the
   * server ended it.
   */
  protected pendingStepFailed(ex: unknown): TaskResult<unknown> {
    const errResult = NhApiUtil.taskResultFromResponse(ex);
    const stepEnded = errResult.items.some(item =>
      item.name === NhTwoFactorFailureCodes.ChallengeExpired || item.name === NhTwoFactorFailureCodes.LockedOut);

    if (stepEnded) {
      this.clearPendingTwoFactorChallenge();
    }

    return errResult;
  }

  protected async sendEnrollmentCompletion(endpoint: string, body: object): Promise<TaskResult<NhTwoFactorEnrollmentResult<TAuthorization>>> {
    const result = new TaskResult<NhTwoFactorEnrollmentResult<TAuthorization>>();
    const request$ = this.httpClient.post<NhTwoFactorChangeResponse>(
      this.twoFactorUrl(endpoint),
      body,
      this.twoFactorStepRequestOptions());

    try {
      const response = await lastValueFrom(request$);
      const authorization = response?.session ? this.completeAuthenticationStep(response.session).authorization : undefined;

      result.data = new NhTwoFactorEnrollmentResult<TAuthorization>({
        authorization: authorization,
        recoveryCodes: response?.recoveryCodes ?? undefined
      });
    } catch (ex) {
      this.pendingStepFailed(ex).copyTo(result);
    }

    return result;
  }

  protected async sendTwoFactorRequest<TResponse, TResult>(
    request$: Observable<TResponse>,
    map: (response: TResponse) => TResult): Promise<TaskResult<TResult>> {
    const result = new TaskResult<TResult>();

    try {
      result.data = map(await lastValueFrom(request$, {defaultValue: undefined as TResponse}));
    } catch (ex) {
      NhApiUtil.taskResultFromResponse(ex).copyTo(result);
    }

    return result;
  }

  /**
   * Runs a WebAuthn prompt. A cancelled prompt or an unsupported browser becomes a failed
   * result with `NhTwoFactorFailureCodes.PasskeyUnavailable`.
   */
  protected async runPasskeyCeremony(ceremony: () => Promise<object>): Promise<TaskResult<object>> {
    const result = new TaskResult<object>();

    try {
      result.data = await ceremony();
    } catch {
      return result.withError(NhTwoFactorFailureCodes.PasskeyUnavailable, 'nh-two-factor.passkey-unavailable');
    }

    return result;
  }

  /**
   * Returns the remember-device token of header clients. Cookie clients receive it as an
   * HttpOnly cookie, so nothing is stored in the browser.
   */
  protected getRememberDeviceToken(): string | undefined {
    if (this.moduleConfig.authType !== 'header' || isPlatformServer(this.platformId)) {
      return undefined;
    }

    try {
      return localStorage.getItem(this.rememberDeviceStorageKey()) ?? undefined;
    } catch {
      return undefined;
    }
  }

  protected storeRememberDeviceToken(token: string | undefined): void {
    if (this.moduleConfig.authType !== 'header' || isPlatformServer(this.platformId)) {
      return;
    }

    try {
      if (token) {
        localStorage.setItem(this.rememberDeviceStorageKey(), token);
      } else {
        localStorage.removeItem(this.rememberDeviceStorageKey());
      }
    } catch {
      // Storage can be unavailable in private browsing; the device is then not remembered.
    }
  }

  protected rememberDeviceStorageKey(): string {
    return BaseNhAuthService.rememberDeviceStorageKey + ':' + this.moduleConfig.authenticationRealm;
  }

  protected setPendingTwoFactorChallenge(challenge: NhTwoFactorChallenge): void {
    this.pendingTwoFactorChallenge = challenge;

    if (isPlatformServer(this.platformId)) {
      return;
    }

    try {
      sessionStorage.setItem(BaseNhAuthService.pendingTwoFactorChallengeStorageKey, JSON.stringify(challenge));
    } catch {
      // Storage can be unavailable in private browsing; the in-memory challenge still works.
    }
  }

  protected readStoredTwoFactorChallenge(): NhTwoFactorChallenge | undefined {
    if (isPlatformServer(this.platformId)) {
      return undefined;
    }

    try {
      const stored = sessionStorage.getItem(BaseNhAuthService.pendingTwoFactorChallengeStorageKey);
      if (!stored) {
        return undefined;
      }

      return new NhTwoFactorChallenge(JSON.parse(stored));
    } catch {
      return undefined;
    }
  }

  protected twoFactorEndpoints(): EndpointsAuthenticationNhCommonModuleConfig {
    // Consumers that passed a plain endpoints object keep working with the default routes.
    return new EndpointsAuthenticationNhCommonModuleConfig({
      ...new EndpointsAuthenticationNhCommonModuleConfig(),
      ...this.moduleConfig.authentication.endpoints
    });
  }

  protected twoFactorUrl(endpoint: string): string {
    return this.moduleConfig.authApiBaseUrl + endpoint;
  }

  protected twoFactorStepRequestOptions() {
    return {
      params: this.languageParams(),
      withCredentials: true
    };
  }

  protected twoFactorAccountRequestOptions() {
    let headers = new HttpHeaders();
    const authorization = this.getAuthorization();
    if (this.moduleConfig.authType === 'header' && authorization?.token) {
      headers = headers.set('Authorization', `Bearer ${authorization.token}`);
    }

    return {
      params: this.languageParams(),
      headers: headers,
      withCredentials: true
    };
  }

  protected languageParams(): HttpParams {
    return new HttpParams().set('language', this.moduleConfig.language);
  }

  public async reloadAuthorizationProfile(): Promise<TaskResult<TAuthorization>> {
    const result = new TaskResult<TAuthorization>();

    const auth = this.getAuthorization();
    if (!auth) {
      return result.withError('', 'Not authenticated.');
    }

    // With multiple accounts another tab may renew or switch while the profile loads.
    const requestedRefreshToken = auth.refreshToken;
    const requestedUserId = this.multipleAccountsEnabled()
      ? this.normalizeBrowserAccountUserId(this.nonEmptyText(auth.user?.id))
      : undefined;

    let httpParams = new HttpParams();
    if (httpParams.get('language') === null) {
      httpParams = httpParams.set('language', this.moduleConfig.language);
    }

    let httpHeaders = new HttpHeaders();
    if (this.moduleConfig.authType === 'header') {
      httpHeaders = httpHeaders.set('Authorization', `Bearer ${auth.token}`);
    }

    const request$ = this.httpClient.get<NhAccountInformationResponse>(this.moduleConfig.authApiBaseUrl + this.moduleConfig.authentication.endpoints.accountInformation, {
      params: httpParams,
      headers: httpHeaders,
      withCredentials: true
    });

    try {
      const informationResponse = await lastValueFrom(request$);
      const target = this.profileTarget(auth, requestedRefreshToken, requestedUserId, informationResponse);
      if (!target) {
        // The profile belongs to a session that is no longer the active one; storing it would
        // show one account while the browser's session belongs to another.
        return this.browserAccountFailure<TAuthorization>(NhBrowserAccountFailureCodes.NotActive);
      }

      // Loop the informationResponse as object to set the properties on the auth object
      for (const key in informationResponse) {
        if (Object.prototype.hasOwnProperty.call(informationResponse, key)) {
          // @ts-ignore
          target[key] = informationResponse[key];
        }
      }
      if(this.moduleConfig.authType !== 'header') {
        target.token = '';
      }

      this.setAuthorization(target);

      result.data = this.getAuthorization();
    } catch (ex) {
      // Check status code, if it's in the 400 range, clear the authorization
      const isHttpResponseError = ex instanceof HttpErrorResponse;
      if (isHttpResponseError) {
        const response = ex as HttpErrorResponse;
        if (response.status >= 401 && response.status < 500 && this.isStoredSession(requestedRefreshToken)) {
          this.clearAuthorization();
        }
      }

      const errResult = NhApiUtil.taskResultFromResponse(ex);
      errResult.copyTo(result);
    }

    return result;
  }

  /**
   * Asks the account information endpoint whether the server still accepts the current session.
   * When the server answers 401, for example after a password change on another device, the local
   * session ends and the session expiration information reports `rejectedByServer`. Concurrent
   * checks share one request, and a confirmed session is not checked again for 30 seconds.
   * @returns `false` only when there is no session or the server rejected it.
   */
  public verifyServerSession(): Promise<boolean> {
    if (!this.isAuthenticated()) {
      return Promise.resolve(false);
    }

    if (Date.now() - this.serverSessionConfirmedAt < BaseNhAuthService.serverSessionConfirmationMilliseconds) {
      return Promise.resolve(true);
    }

    this.serverSessionCheck ??= this.checkServerSession().finally(() => {
      this.serverSessionCheck = undefined;
    });

    return this.serverSessionCheck;
  }

  protected async checkServerSession(): Promise<boolean> {
    const checkedAuthorization = this.getAuthorization();

    let httpHeaders = new HttpHeaders();
    if (this.moduleConfig.authType === 'header' && (checkedAuthorization?.token?.length ?? 0) > 0) {
      httpHeaders = httpHeaders.set('Authorization', `Bearer ${checkedAuthorization!.token}`);
    }

    const request$ = this.httpClient.get<unknown>(this.moduleConfig.authApiBaseUrl + this.moduleConfig.authentication.endpoints.accountInformation, {
      params: new HttpParams().set('language', this.moduleConfig.language),
      headers: httpHeaders,
      withCredentials: true,
      context: new HttpContext().set(NH_SKIP_SERVER_SESSION_CHECK, true)
    });

    try {
      await lastValueFrom(request$);
      this.serverSessionConfirmedAt = Date.now();

      return true;
    } catch (ex) {
      const rejected = ex instanceof HttpErrorResponse && ex.status === 401;

      // Other failures say nothing about the session, and a sign-in during the check replaced it.
      if (!rejected || this.getAuthorization() !== checkedAuthorization) {
        return true;
      }

      if (!this.multipleAccountsEnabled()) {
        this.endRejectedSession();
        return false;
      }

      // Signing out another account expires the browser's cookies until the active session is
      // renewed inside the session lock. Decide inside the lock, so a 401 from that moment does not
      // end a session that was renewed meanwhile.
      return this.runExclusive(async () => {
        if (this.readStoredAuthorization()?.refreshToken !== checkedAuthorization?.refreshToken) {
          return true;
        }

        this.endRejectedSession();
        // A rejected session, for example after a password change, also ends every other account
        // of this browser: their refresh tokens are not kept once the server refused one session.
        this.forgetAllBrowserAccounts();

        return false;
      });
    }
  }

  private endRejectedSession(): void {
    this.sessionRejectedByServer = true;
    this.clearAuthorization();
  }

  /**
   * Asks the server to revoke the current login's refresh token and end its session, then ends the
   * local session. The local session always ends, also when the request fails (offline, a timeout,
   * CORS or an error response), so the next person on the device does not keep it. A failed request
   * still returns a failed result: a cookie session can only be ended by the server's response, so
   * report that the sign-out did not reach the server.
   *
   * With `authentication.multipleAccounts` enabled it signs out the active account only, and
   * revokes the refresh token the browser holds at that moment, also when another tab just rotated it.
   */
  async logout(): Promise<TaskResult<void>> {
    if (!this.multipleAccountsEnabled()) {
      return this.endActiveSession(this.getAuthorization()?.refreshToken ?? null);
    }

    return this.runExclusive(() => this.endActiveSession(
      this.readStoredAuthorization()?.refreshToken ?? this.getAuthorization()?.refreshToken ?? null,
      true));
  }

  /**
   * The accounts this browser is signed in to, most recently used first. An account is listed while
   * its refresh token is valid; expired accounts are removed whenever the list is read. Always empty
   * while `authentication.multipleAccounts` is off.
   */
  public get accounts(): NhBrowserAccount[] {
    if (!this.multipleAccountsEnabled() || isPlatformServer(this.platformId)) {
      return [];
    }

    const activeUserId = this.activeBrowserAccountUserId();

    return this.readBrowserAccounts()
      .sort((first, second) => second.lastUsedAt.localeCompare(first.lastUsedAt))
      .map(account => new NhBrowserAccount({
        userId: account.userId,
        userName: account.userName,
        displayName: account.displayName,
        validTo: account.authorization.validTo ?? undefined,
        refreshTokenExpires: nhRefreshTokenExpiry(account.authorization),
        signedInAt: account.signedInAt,
        lastUsedAt: account.lastUsedAt,
        isActive: account.userId === activeUserId
      }));
  }

  /**
   * Renews the active session with its refresh token, once for every tab of the browser: concurrent
   * calls, in this tab or another, share one request to the refresh endpoint. Inside the lock it
   * reads the stored session again, keeps a session that another tab renewed meanwhile or whose
   * access token stays valid for more than a minute, and otherwise rotates the refresh token and
   * stores the new session before any other tab can read it. When the refresh endpoint refuses a
   * token that another tab rotated outside the lock, the stored session is returned instead of a
   * failure. The user, claims and divisions of the session are kept; reload the profile when they
   * may have changed.
   *
   * Works with and without `authentication.multipleAccounts`.
   *
   * @param userId the account that is expected to be active; the renewal fails with
   * `NhBrowserAccountFailureCodes.NotActive` when another account became active, for example in
   * another tab. Use `switchAccount` to renew and activate another account.
   * @param options `force` rotates the refresh token also while the access token is still valid,
   * for example because the server must write the access cookie again.
   */
  public renewSession(userId?: string, options?: NhSessionRenewalOptions): Promise<TaskResult<TAuthorization>> {
    const expectedUserId = this.normalizeBrowserAccountUserId(userId);
    const force = options?.force === true;
    const key = `${expectedUserId ?? ''}|${force}`;

    const pending = this.sessionRenewalsInFlight.get(key);
    if (pending) {
      return pending;
    }

    const renewal = this.renewActiveSession(expectedUserId, force).finally(() => {
      this.sessionRenewalsInFlight.delete(key);
    });
    this.sessionRenewalsInFlight.set(key, renewal);

    return renewal;
  }

  /**
   * Makes another account of this browser the active one. It always rotates that account's refresh
   * token, so the server checks again whether the account may sign in and writes its access cookie,
   * and then stores the account's session as the active authorization of every tab. The session of
   * the account that was active stays in the directory and is not revoked.
   *
   * Requires `authentication.multipleAccounts`.
   */
  public switchAccount(userId: string): Promise<TaskResult<TAuthorization>> {
    this.requireMultipleAccounts('switchAccount');
    const accountId = this.normalizeBrowserAccountUserId(userId);

    return this.runExclusive(async () => {
      const account = accountId ? this.readStoredAccountSession(accountId) : undefined;
      if (!account) {
        return this.browserAccountFailure<TAuthorization>(NhBrowserAccountFailureCodes.UnknownAccount);
      }

      return this.rotateSession(account, () => this.readStoredAccountSession(account.userId!));
    });
  }

  /**
   * Removes another account from this browser without revoking its session on the server, as
   * "remove from this device" does. Returns `false` and changes nothing for the active account,
   * whose session also lives in the server's authentication cookies: end it with `logout()` or
   * `logoutAccount()`. Returns `false` as well for an account this browser does not hold. Runs
   * inside the session lock, so a switch or renewal in another tab cannot bring the account back.
   *
   * Requires `authentication.multipleAccounts`.
   */
  public forgetAccount(userId: string): Promise<boolean> {
    this.requireMultipleAccounts('forgetAccount');
    const accountId = this.normalizeBrowserAccountUserId(userId);

    return this.runExclusive(async () => {
      if (!accountId || accountId === this.activeBrowserAccountUserId()) {
        return false;
      }

      const accounts = this.readBrowserAccounts();
      const remaining = accounts.filter(account => account.userId !== accountId);
      if (remaining.length === accounts.length) {
        return false;
      }

      this.writeBrowserAccounts(remaining);
      return true;
    });
  }

  /**
   * Signs out one account: the server revokes exactly that account's refresh token and the account
   * leaves this browser, also when the request fails, as `logout()` does. Signing out the active
   * account ends the active session. Signing out another account leaves the active account signed
   * in: the request asks the server to keep the browser's authentication cookies
   * (`keepAuthenticationCookies`). A server that does not confirm it, such as an older one, expired
   * them; the active session is then renewed right after the revocation, inside the same lock, so its
   * cookies are written again, and a failure of that renewal is part of the result.
   *
   * Requires `authentication.multipleAccounts`.
   */
  public logoutAccount(userId: string): Promise<TaskResult<void>> {
    this.requireMultipleAccounts('logoutAccount');
    const accountId = this.normalizeBrowserAccountUserId(userId);

    return this.runExclusive(async () => {
      const result = new TaskResult<void>();
      const active = this.readActiveSession();

      if (accountId && active?.userId === accountId) {
        return this.endActiveSession(active.authorization.refreshToken ?? null, true);
      }

      const account = accountId ? this.readStoredAccountSession(accountId) : undefined;
      if (!account) {
        // Nothing of this account is left in the browser; signing out again changes nothing.
        return result;
      }

      let cookiesKept = false;
      try {
        const revoked = await this.requestAccountLogout(account.authorization.refreshToken ?? null);
        revoked.copyTo(result);
        cookiesKept = revoked.isSuccess && revoked.data === true;
      } finally {
        this.removeBrowserAccount(account.userId!);
      }

      // A server that does not confirm it kept the cookies, such as an older one, expired them.
      if (!cookiesKept && active?.authorization.refreshToken) {
        const restored = await this.rotateSession(active, () => this.readActiveSession());
        restored.copyTo(result);
      }

      return result;
    });
  }

  /**
   * Signs out every account of this browser: the server revokes each account's refresh token, and
   * the active session and the whole account directory end, also when a request fails. The result
   * fails when any revocation did not reach the server.
   *
   * Requires `authentication.multipleAccounts`.
   */
  public logoutAllAccounts(): Promise<TaskResult<void>> {
    this.requireMultipleAccounts('logoutAllAccounts');

    return this.runExclusive(async () => {
      const result = new TaskResult<void>();
      const active = this.readActiveSession();

      try {
        for (const account of this.readBrowserAccounts()) {
          if (account.userId === active?.userId) {
            continue;
          }

          (await this.requestLogout(account.authorization.refreshToken ?? null, true)).copyTo(result);
        }

        if (active) {
          (await this.endActiveSession(active.authorization.refreshToken ?? null, true)).copyTo(result);
        }
      } finally {
        this.forgetAllBrowserAccounts();
      }

      return result;
    });
  }

  /**
   * Who an authorization belongs to, for the browser's account directory. By default the user id
   * is the account information's `user.id`, or the user id claim of the access token while the
   * profile has not been loaded yet; the sign-in name is the user's `userName`, or else `email`;
   * the display name is the user's `displayName`, or else `email`. The name typed at a password
   * sign-in takes precedence over this sign-in name. Override it when the application signs in by
   * another name. The result only finds the account's entry in this browser: the server decides who
   * is signed in.
   */
  protected describeBrowserAccount(authorization: TAuthorization): NhBrowserAccountIdentity | undefined {
    const user = authorization?.user as (INhAuthorization['user'] & {userName?: unknown, displayName?: unknown}) | undefined;
    const userId = this.nonEmptyText(user?.id) ?? nhAccessTokenUserId(authorization?.token);

    if (!userId) {
      return undefined;
    }

    const email = this.nonEmptyText(user?.email);

    return {
      userId: userId,
      userName: this.nonEmptyText(user?.userName) ?? email,
      displayName: this.nonEmptyText(user?.displayName) ?? email
    };
  }

  /**
   * The browser's Web Locks, which serialize session renewals across tabs. When the browser has
   * none, a short `localStorage` lease serializes them instead.
   */
  protected sessionLockManager(): LockManager | undefined {
    if (typeof navigator === 'undefined') {
      return undefined;
    }

    return navigator.locks ?? undefined;
  }

  /**
   * Revokes `refreshToken` and ends the active session, also when the request fails. `insideLock`
   * bounds the request, so a tab never holds the session lock for long.
   */
  private async endActiveSession(refreshToken: string | null, insideLock: boolean = false): Promise<TaskResult<void>> {
    const result = new TaskResult<void>();

    try {
      (await this.requestLogout(refreshToken, insideLock)).copyTo(result);
    } finally {
      this.clearAuthorization();
    }

    return result;
  }

  private async requestLogout(refreshToken: string | null, insideLock: boolean = false): Promise<TaskResult<void>> {
    const result = new TaskResult<void>();
    let httpParams = new HttpParams();
    if (httpParams.get('language') === null) {
      httpParams = httpParams.set('language', this.moduleConfig.language);
    }
    let request$ = this.httpClient.post<void>(this.moduleConfig.authApiBaseUrl + this.moduleConfig.authentication.endpoints.logout, { refreshToken }, {
      params: httpParams,
      withCredentials: true
    });
    if (insideLock) {
      request$ = request$.pipe(timeout(BaseNhAuthService.sessionLockRequestTimeoutMilliseconds));
    }

    try {
      await lastValueFrom(request$);
    } catch (ex) {
      const errResult = NhApiUtil.taskResultFromResponse(ex);
      errResult.copyTo(result);
    }

    return result;
  }

  /**
   * Revokes the refresh token of an account that is not active and asks the server to leave the
   * browser's authentication cookies, which belong to the active account, alone. `data` is `true`
   * when the server confirmed that it kept them.
   */
  private async requestAccountLogout(refreshToken: string | null): Promise<TaskResult<boolean>> {
    const result = new TaskResult<boolean>();
    const request$ = this.httpClient.post<void>(
      this.moduleConfig.authApiBaseUrl + this.moduleConfig.authentication.endpoints.logout,
      {refreshToken: refreshToken, keepAuthenticationCookies: true},
      {
        params: this.languageParams(),
        withCredentials: true,
        observe: 'response'
      }).pipe(timeout(BaseNhAuthService.sessionLockRequestTimeoutMilliseconds));

    try {
      const response = await lastValueFrom(request$);
      result.data = response.headers.get(BaseNhAuthService.authenticationCookiesHeaderName) === 'kept';
    } catch (ex) {
      NhApiUtil.taskResultFromResponse(ex).copyTo(result);
    }

    return result;
  }

  private async renewActiveSession(expectedUserId: string | undefined, force: boolean): Promise<TaskResult<TAuthorization>> {
    if (isPlatformServer(this.platformId)) {
      return this.browserAccountFailure<TAuthorization>(NhBrowserAccountFailureCodes.NotRenewable);
    }

    const before = this.readActiveSession();
    if (!before) {
      return this.browserAccountFailure<TAuthorization>(NhBrowserAccountFailureCodes.NotRenewable);
    }

    if (expectedUserId && before.userId !== expectedUserId) {
      return this.browserAccountFailure<TAuthorization>(NhBrowserAccountFailureCodes.NotActive);
    }

    return this.runExclusive(async () => {
      const current = this.readActiveSession();
      if (!current) {
        return this.browserAccountFailure<TAuthorization>(NhBrowserAccountFailureCodes.NotRenewable);
      }

      if (expectedUserId && current.userId !== expectedUserId) {
        return this.browserAccountFailure<TAuthorization>(NhBrowserAccountFailureCodes.NotActive);
      }

      const renewedByAnotherTab = current.authorization.refreshToken !== before.authorization.refreshToken;
      if (!force && (renewedByAnotherTab || this.isSessionFresh(current.authorization))) {
        return this.useStoredSession(current.authorization);
      }

      return this.rotateSession(current, () => this.readActiveSession());
    });
  }

  /**
   * Rotates the session's refresh token and stores the renewed session as the active authorization.
   * Runs inside the session lock. When the refresh endpoint refuses the token and `reread` finds
   * that another tab rotated it, the stored session is used instead.
   */
  private async rotateSession(
    session: NhRenewableSession<TAuthorization>,
    reread: () => NhRenewableSession<TAuthorization> | undefined): Promise<TaskResult<TAuthorization>> {
    const result = new TaskResult<TAuthorization>();
    const refreshToken = session.authorization.refreshToken;

    if (!refreshToken || !session.userName || nhHasExpired(nhRefreshTokenExpiry(session.authorization), Date.now())) {
      return this.browserAccountFailure<TAuthorization>(NhBrowserAccountFailureCodes.NotRenewable);
    }

    const response = await this.requestSessionRefresh(session.userName, refreshToken);
    if (!response.isSuccess || !response.data?.token) {
      const stored = reread();
      const rotatedElsewhere = !!stored
        && stored.userId === session.userId
        && !!stored.authorization.refreshToken
        && stored.authorization.refreshToken !== refreshToken;

      if (rotatedElsewhere) {
        const active = this.readStoredAuthorization();
        if (active && active.refreshToken === stored.authorization.refreshToken) {
          // Another tab renewed this session and stored it as the active one.
          return this.useStoredSession(active);
        }

        // Another tab rotated an account that is not active; rotate the token it stored, once.
        return this.rotateSession(stored, () => undefined);
      }

      response.copyTo(result);
      if (result.isSuccess) {
        result.addError(NhBrowserAccountFailureCodes.NotRenewable, 'nh-accounts.not-renewable');
      }

      return result;
    }

    const renewed = this.mergeRenewedSession(session.authorization, response.data);
    this.rememberSessionSignInUserName(renewed.refreshToken, session.userName);
    this.setAuthorization(renewed);

    if (!renewed.refreshToken && session.userId) {
      // The presented token is consumed; without a new one the account cannot be renewed again.
      this.removeBrowserAccount(session.userId);
    }

    result.data = renewed;
    return result;
  }

  private async requestSessionRefresh(userName: string, refreshToken: string): Promise<TaskResult<NhLoginResponse>> {
    const result = new TaskResult<NhLoginResponse>();
    const request$ = this.httpClient.post<NhLoginResponse>(
      this.moduleConfig.authApiBaseUrl + this.moduleConfig.authentication.endpoints.refresh,
      {userName: userName, refreshToken: refreshToken},
      {
        params: this.languageParams(),
        withCredentials: true,
        // A refused refresh token says nothing about the active session.
        context: new HttpContext().set(NH_SKIP_SERVER_SESSION_CHECK, true)
      }).pipe(timeout(BaseNhAuthService.sessionLockRequestTimeoutMilliseconds));

    try {
      result.data = await lastValueFrom(request$);
    } catch (ex) {
      NhApiUtil.taskResultFromResponse(ex).copyTo(result);
    }

    return result;
  }

  /** Returns the stored active session as the outcome of a renewal and lets this tab use it. */
  private useStoredSession(authorization: TAuthorization): TaskResult<TAuthorization> {
    const result = new TaskResult<TAuthorization>();
    const stored = this.readStoredAuthorization();

    if (stored && this.authorization?.refreshToken !== stored.refreshToken) {
      this.authorization = stored;
      this.authSubject.next(this.authorization);
      this.dispatchSessionExpirationInformationChanged();
    }

    result.data = this.authorization ?? authorization;
    return result;
  }

  /** Keeps the user, claims and divisions of `previous` and takes the tokens of the renewal. */
  private mergeRenewedSession(previous: TAuthorization, response: NhLoginResponse): TAuthorization {
    const renewed = {...previous} as TAuthorization & {refreshValidTo?: string | null};

    renewed.token = response.token ?? '';
    renewed.validTo = response.validTo ?? undefined;
    renewed.refreshToken = response.refreshToken ?? undefined;
    renewed.refreshTokenExpires = response.refreshValidTo ?? undefined;
    if ('refreshValidTo' in renewed) {
      renewed.refreshValidTo = response.refreshValidTo ?? undefined;
    }

    return renewed;
  }

  private isSessionFresh(authorization: TAuthorization): boolean {
    const validTo = Date.parse(authorization.validTo ?? '');

    return !Number.isNaN(validTo)
      && validTo - Date.now() > BaseNhAuthService.sessionRenewalMarginMilliseconds;
  }

  /** The active session as stored, which every tab shares, with the account it belongs to. */
  private readActiveSession(): NhRenewableSession<TAuthorization> | undefined {
    const authorization = this.readStoredAuthorization();
    if (!authorization) {
      return undefined;
    }

    const identity = this.describeBrowserAccount(authorization);
    const userId = this.normalizeBrowserAccountUserId(identity?.userId);
    const account = userId && this.multipleAccountsEnabled()
      ? this.readBrowserAccounts().find(entry => entry.userId === userId)
      : undefined;

    return {
      authorization: authorization,
      userId: userId,
      userName: this.signInUserNameFor(authorization.refreshToken) ?? account?.userName ?? identity?.userName
    };
  }

  private readStoredAccountSession(userId: string): NhRenewableSession<TAuthorization> | undefined {
    const account = this.readBrowserAccounts().find(entry => entry.userId === userId);
    if (!account) {
      return undefined;
    }

    return {authorization: account.authorization, userId: account.userId, userName: account.userName};
  }

  private readStoredAuthorization(): TAuthorization | undefined {
    try {
      const stored = localStorage.getItem('at');
      if (!stored) {
        return undefined;
      }

      return JSON.parse(Base64.decode(stored));
    } catch {
      return undefined;
    }
  }

  private runExclusive<T>(task: () => Promise<T>): Promise<T> {
    const locks = this.sessionLockManager();
    if (locks) {
      return locks.request(BaseNhAuthService.sessionLockName, () => task()) as Promise<T>;
    }

    return nhRunWithStorageLease(BaseNhAuthService.sessionLockName, task);
  }

  private multipleAccountsConfig(): MultipleAccountsAuthenticationNhCommonModuleConfig {
    return new MultipleAccountsAuthenticationNhCommonModuleConfig({
      ...new MultipleAccountsAuthenticationNhCommonModuleConfig(),
      ...this.moduleConfig?.authentication?.multipleAccounts
    });
  }

  private multipleAccountsEnabled(): boolean {
    return this.moduleConfig?.authentication?.multipleAccounts?.enabled === true;
  }

  private requireMultipleAccounts(method: string): void {
    if (!this.multipleAccountsEnabled()) {
      throw new Error(`BaseNhAuthService.${method} needs authentication.multipleAccounts.enabled.`);
    }
  }

  private startBrowserAccountDirectory(): void {
    const config = this.multipleAccountsConfig();
    if (config.hintCookieName && !/^[A-Za-z0-9!#$%&'*+.^_`|~-]+$/.test(config.hintCookieName)) {
      throw new Error('authentication.multipleAccounts.hintCookieName must be a cookie name without spaces, separators or "=".');
    }

    if (!this.multipleAccountsEnabled()) {
      this.removeBrowserAccountsLeftBehind();
      return;
    }

    if (!Number.isInteger(config.maximum) || config.maximum < 1) {
      throw new Error('authentication.multipleAccounts.maximum must be a whole number of at least 1.');
    }

    try {
      this.publishBrowserAccounts();
    } catch {
      // A directory that cannot be read, for example after it was changed outside this library,
      // is dropped instead of stopping the application from starting.
      nhWriteBrowserAccountDirectory([]);
      this.publishBrowserAccounts();
    }

    this.browserAccountStorageListener = event => this.onBrowserAccountStorageChanged(event);
    window.addEventListener('storage', this.browserAccountStorageListener);
  }

  /**
   * With the option off, removes the accounts and the hint cookie that an earlier opt-in left in
   * this browser, so their refresh tokens do not stay behind. Their sessions are not revoked and
   * expire on the server. Nothing exists to remove in a browser that never had the option on.
   */
  private removeBrowserAccountsLeftBehind(): void {
    try {
      if (localStorage.getItem(nhBrowserAccountDirectoryStorageKey) !== null) {
        localStorage.removeItem(nhBrowserAccountDirectoryStorageKey);
      }
    } catch {
      // Without storage nothing was left behind.
    }

    const hintCookieName = this.multipleAccountsConfig().hintCookieName;
    if (hintCookieName && typeof document !== 'undefined' && document.cookie.includes(`${hintCookieName}=`)) {
      this.writeBrowserAccountHintCookie([]);
    }
  }

  /**
   * Whether the stored active session still holds `refreshToken`. Always true while multiple
   * accounts are off, which keeps the single-account behavior unchanged.
   */
  private isStoredSession(refreshToken: string | undefined): boolean {
    if (!this.multipleAccountsEnabled()) {
      return true;
    }

    return this.readStoredAuthorization()?.refreshToken === refreshToken;
  }

  /**
   * The authorization a loaded profile belongs to, or `undefined` when it no longer matches the
   * active session: the profile is of another account, or another tab switched or signed out while
   * it loaded. Another tab's renewal of the same account takes the profile onto the stored session.
   */
  private profileTarget(
    requested: TAuthorization,
    requestedRefreshToken: string | undefined,
    requestedUserId: string | undefined,
    profile: NhAccountInformationResponse): TAuthorization | undefined {
    if (!this.multipleAccountsEnabled()) {
      return requested;
    }

    const profileUserId = this.normalizeBrowserAccountUserId(this.nonEmptyText(profile?.user?.id));
    if (requestedUserId && profileUserId && profileUserId !== requestedUserId) {
      return undefined;
    }

    const stored = this.readStoredAuthorization();
    if (!stored) {
      return undefined;
    }

    if (stored.refreshToken === requestedRefreshToken) {
      return requested;
    }

    return profileUserId && this.browserAccountUserId(stored) === profileUserId ? stored : undefined;
  }

  /** Keeps this tab in step when another tab signs in, switches, renews or signs out. */
  private onBrowserAccountStorageChanged(event: StorageEvent): void {
    if (event.storageArea !== localStorage) {
      return;
    }

    const activeChanged = event.key === null || event.key === 'at';
    const directoryChanged = event.key === null || event.key === nhBrowserAccountDirectoryStorageKey;
    if (!activeChanged && !directoryChanged) {
      return;
    }

    this.zone.run(() => {
      if (activeChanged) {
        this.authorization = this.readStoredAuthorization();
        this.serverSessionConfirmedAt = 0;
        this.authSubject.next(this.authorization);
        this.dispatchSessionExpirationInformationChanged();
      }

      this.publishBrowserAccounts();
    });
  }

  private rememberBrowserAccount(authorization: TAuthorization): void {
    if (!this.multipleAccountsEnabled() || isPlatformServer(this.platformId)) {
      return;
    }

    const identity = this.describeBrowserAccount(authorization);
    const userId = this.normalizeBrowserAccountUserId(identity?.userId);
    if (!userId || !authorization?.refreshToken) {
      // A session without a refresh token, such as an impersonation, cannot be resumed later.
      return;
    }

    const accounts = this.readBrowserAccounts();
    if (accounts.some(account => account.userId !== userId && account.authorization.refreshToken === authorization.refreshToken)) {
      // The refresh token belongs to another account, so this authorization mixes two accounts,
      // for example a profile that loaded while another tab switched. Neither entry is changed.
      return;
    }

    const existing = accounts.find(account => account.userId === userId);
    const userName = this.signInUserNameFor(authorization.refreshToken) ?? existing?.userName ?? identity?.userName;
    if (!userName) {
      // Without a sign-in name the refresh endpoint cannot renew it; a profile reload adds it.
      return;
    }

    const now = new Date().toISOString();
    const account: NhStoredBrowserAccount<TAuthorization> = {
      userId: userId,
      userName: userName,
      displayName: identity?.displayName ?? existing?.displayName,
      signedInAt: existing?.signedInAt ?? now,
      lastUsedAt: now,
      // The access token stays out of the directory: an account becomes active only through a
      // renewal, which issues a new one, and `at` holds the active session's own.
      authorization: {...authorization, token: ''}
    };

    const others = accounts
      .filter(entry => entry.userId !== userId)
      .sort((first, second) => second.lastUsedAt.localeCompare(first.lastUsedAt))
      .slice(0, this.multipleAccountsConfig().maximum - 1);

    this.writeBrowserAccounts([account, ...others]);
  }

  private forgetBrowserAccountOf(authorization: TAuthorization | undefined): void {
    if (!authorization || !this.multipleAccountsEnabled() || isPlatformServer(this.platformId)) {
      return;
    }

    const userId = this.browserAccountUserId(authorization);
    if (userId) {
      this.removeBrowserAccount(userId);
    }
  }

  private removeBrowserAccount(userId: string): void {
    const accounts = this.readBrowserAccounts();
    const remaining = accounts.filter(account => account.userId !== userId);

    if (remaining.length !== accounts.length) {
      this.writeBrowserAccounts(remaining);
    }
  }

  private forgetAllBrowserAccounts(): void {
    if (!this.multipleAccountsEnabled() || isPlatformServer(this.platformId)) {
      return;
    }

    this.writeBrowserAccounts([]);
  }

  private readBrowserAccounts(): NhStoredBrowserAccount<TAuthorization>[] {
    const directory = nhReadBrowserAccountDirectory<TAuthorization>(Date.now());

    if (directory.pruned) {
      nhWriteBrowserAccountDirectory(directory.accounts);
    }

    return directory.accounts;
  }

  private writeBrowserAccounts(accounts: NhStoredBrowserAccount<TAuthorization>[]): void {
    nhWriteBrowserAccountDirectory(accounts);
    this.publishBrowserAccounts();
  }

  private publishBrowserAccounts(): void {
    const accounts = this.accounts;

    this.writeBrowserAccountHintCookie(accounts);
    this.browserAccountsSubject.next(accounts);
  }

  /**
   * Writes the hint cookie: the number of accounts and nothing else, so it never identifies an
   * account. It lives as long as the longest refresh token of the browser and is removed when no
   * account is left.
   */
  private writeBrowserAccountHintCookie(accounts: NhBrowserAccount[]): void {
    const name = this.multipleAccountsConfig().hintCookieName;
    if (!name || isPlatformServer(this.platformId) || typeof document === 'undefined') {
      return;
    }

    const attributes = [`${name}=${accounts.length}`, 'Path=/', 'SameSite=Lax'];
    attributes.push(`Max-Age=${this.browserAccountHintCookieMaxAge(accounts)}`);

    const domain = this.moduleConfig.cookieDomain;
    if (domain && /^[A-Za-z0-9.-]+$/.test(domain)) {
      attributes.push(`Domain=${domain}`);
    }

    if (document.location?.protocol === 'https:') {
      attributes.push('Secure');
    }

    try {
      document.cookie = attributes.join('; ');
    } catch {
      // A sandboxed document may refuse cookies; the hint is only a convenience for the server.
    }
  }

  private browserAccountHintCookieMaxAge(accounts: NhBrowserAccount[]): number {
    if (accounts.length === 0) {
      return 0;
    }

    const maximumSeconds = 400 * 24 * 60 * 60;
    const expiries = accounts
      .map(account => Date.parse(account.refreshTokenExpires ?? ''))
      .filter(expiresAt => !Number.isNaN(expiresAt));

    if (expiries.length === 0) {
      return 30 * 24 * 60 * 60;
    }

    const seconds = Math.ceil((Math.max(...expiries) - Date.now()) / 1000);
    return Math.min(Math.max(seconds, 1), maximumSeconds);
  }

  private activeBrowserAccountUserId(): string | undefined {
    const active = this.readStoredAuthorization();

    return active ? this.browserAccountUserId(active) : undefined;
  }

  private browserAccountUserId(authorization: TAuthorization): string | undefined {
    return this.normalizeBrowserAccountUserId(this.describeBrowserAccount(authorization)?.userId);
  }

  private normalizeBrowserAccountUserId(userId: string | undefined): string | undefined {
    const normalized = userId?.trim().toLowerCase();

    return normalized ? normalized : undefined;
  }

  private rememberSessionSignInUserName(refreshToken: string | undefined, userName: string | undefined): void {
    if (refreshToken && userName) {
      this.sessionSignInUserName = {refreshToken: refreshToken, userName: userName};
    }
  }

  private signInUserNameFor(refreshToken: string | undefined): string | undefined {
    if (!refreshToken || this.sessionSignInUserName?.refreshToken !== refreshToken) {
      return undefined;
    }

    return this.sessionSignInUserName.userName;
  }

  private browserAccountFailure<T>(code: NhBrowserAccountFailureCodes): TaskResult<T> {
    const keys: Record<NhBrowserAccountFailureCodes, string> = {
      [NhBrowserAccountFailureCodes.UnknownAccount]: 'nh-accounts.unknown-account',
      [NhBrowserAccountFailureCodes.NotRenewable]: 'nh-accounts.not-renewable',
      [NhBrowserAccountFailureCodes.NotActive]: 'nh-accounts.not-active'
    };

    return new TaskResult<T>().withError(code, keys[code]);
  }

  private nonEmptyText(value: unknown): string | undefined {
    return typeof value === 'string' && value.trim().length > 0 ? value : undefined;
  }

  /**
   * Rotates a refresh token and stores the response as the authorization. Set `userName` in the
   * model: the refresh endpoint finds the account by it. Prefer `renewSession()`, which rotates once
   * for every tab and keeps the user, claims and divisions; this lower-level call does neither.
   */
  async authenticateRefreshToken(model: RefreshTokenLoginAccountMutateModel): Promise<TaskResult<TAuthorization>> {
    const result = new TaskResult<TAuthorization>();

    let httpParams = new HttpParams();
    if (httpParams.get('language') === null) {
      httpParams = httpParams.set('language', this.moduleConfig.language);
    }

    const request$ = this.httpClient.post<TAuthorization>(this.moduleConfig.authApiBaseUrl + this.moduleConfig.authentication.endpoints.refresh, model, {
      params: httpParams,
      withCredentials: true
    });

    try {
      result.data = await lastValueFrom(request$);
      if (result.isSuccess) {
        this.setAuthorization(result.data);
      }
    } catch (ex) {
      const errResult = NhApiUtil.taskResultFromResponse(ex);
      errResult.copyTo(result);
    }

    return result;
  }

  async impersonate(model: ImpersonateAuthenticateModel): Promise<TaskResult<TAuthorization>> {
    const result = new TaskResult<TAuthorization>();

    let httpParams = new HttpParams();
    if (httpParams.get('language') === null) {
      httpParams = httpParams.set('language', this.moduleConfig.language);
    }

    const request$ = this.httpClient.post<TAuthorization>(this.moduleConfig.authApiBaseUrl + this.moduleConfig.authentication.endpoints.impersonate, model, {
      params: httpParams,
      withCredentials: true
    });

    try {
      result.data = await lastValueFrom(request$);
      if (result.isSuccess) {
        this.setAuthorization(result.data);
        await this.reloadAuthorizationProfile();
      }
    } catch (ex) {
      const errResult = NhApiUtil.taskResultFromResponse(ex);
      errResult.copyTo(result);
    }

    return result;
  }

  async impersonateRevert(model: RevertImpersonateAuthenticateModel): Promise<TaskResult<TAuthorization>> {
    const result = new TaskResult<TAuthorization>();

    let httpParams = new HttpParams();
    if (httpParams.get('language') === null) {
      httpParams = httpParams.set('language', this.moduleConfig.language);
    }

    const request$ = this.httpClient.post<TAuthorization>(this.moduleConfig.authApiBaseUrl + this.moduleConfig.authentication.endpoints.revertImpersonate, model, {
      params: httpParams,
      withCredentials: true
    });

    try {
      result.data = await lastValueFrom(request$);
      if (result.isSuccess) {
        this.setAuthorization(result.data);
        await this.reloadAuthorizationProfile();
      }
    } catch (ex) {
      const errResult = NhApiUtil.taskResultFromResponse(ex);
      errResult.copyTo(result);
    }

    return result;
  }
}

@Injectable()
export class NhAuthService extends BaseNhAuthService<NhAuthorization> {
  protected initEmptyAuthorization(): NhAuthorization {
    return new NhAuthorization();
  }

  constructor() {
    super();
  }
}
