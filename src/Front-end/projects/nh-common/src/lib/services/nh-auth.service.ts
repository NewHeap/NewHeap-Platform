import {inject, Injectable, NgZone, OnDestroy, PLATFORM_ID, REQUEST_CONTEXT} from '@angular/core';
import {BehaviorSubject, lastValueFrom, Observable} from 'rxjs';
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
  NhDivision,
  NhLoginResponse,
  NhPasskey,
  NhPasskeyOptions,
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
import {HttpClient, HttpErrorResponse, HttpHeaders, HttpParams} from '@angular/common/http';
import {EndpointsAuthenticationNhCommonModuleConfig, NhCommonModuleConfig} from "../models/config.models";
import {NhApiUtil} from "../util/nh-api-util";
import {isPlatformServer} from "@angular/common";
import {Base64} from "js-base64";
import {NhPasskeyClient} from "./nh-passkey-client.service";
import {NhBackgroundOperation} from "../models/background-operation.models";

interface NhTwoFactorChangeResponse {
  recoveryCodes?: string[] | null;
  session?: NhLoginResponse | null;
}

@Injectable()
export abstract class BaseNhAuthService<TAuthorization extends INhAuthorization> implements OnDestroy {
  protected static readonly pendingTwoFactorChallengeStorageKey = 'nh-two-factor-challenge';
  protected static readonly externalTwoFactorChallengePrefix = 'nh-two-factor=';
  protected static readonly rememberDeviceStorageKey = 'nh-two-factor-device';

  protected authorization: TAuthorization | undefined = undefined;
  protected pendingTwoFactorChallenge: NhTwoFactorChallenge | undefined = undefined;
  protected authSession: AuthenticationSessionCreateResponse | undefined = undefined;
  public readonly authSubject = new BehaviorSubject<TAuthorization | undefined>(this.getAuthorization());
  protected onReady: ((value: (PromiseLike<unknown> | unknown)) => void) | undefined;
  public readonly authReady: Promise<unknown>;

  protected _sessionExpirationInformation = new BehaviorSubject<AuthSessionExpirationInformation>(this.getSessionExpirationInformation());
  public sessionExpirationInformationChanged = this._sessionExpirationInformation.asObservable();
  protected intervalHandle: any;

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
    }

    return result;
  }

  public clearAuthorization(doDispatchEvent: boolean = true): void {
    if (this.isAuthenticated()) {
      try {
        localStorage.removeItem('at');
      } finally {
      }

      this.authorization = undefined;
      if (doDispatchEvent) {
        this.authSubject.next(this.authorization);
      }
      this.dispatchSessionExpirationInformationChanged();
    }
  }

  public setAuthorization(auth: TAuthorization, noEvent = false): void {
    localStorage.setItem('at', Base64.encode(JSON.stringify(auth)));
    this.authorization = auth;
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
   */
  async authenticateInteractive(model: AuthenticateModel): Promise<TaskResult<NhAuthenticationStep<TAuthorization>>> {
    const result = new TaskResult<NhAuthenticationStep<TAuthorization>>();
    model.realm = this.moduleConfig.authenticationRealm;
    model.rememberDeviceToken = model.rememberDeviceToken ?? this.getRememberDeviceToken();

    const request$ = this.httpClient.post<NhLoginResponse>(this.moduleConfig.authApiBaseUrl + this.moduleConfig.authentication.endpoints.login, model, {
      params: this.languageParams(),
      withCredentials: true
    });

    try {
      result.data = this.completeAuthenticationStep(await lastValueFrom(request$));
    } catch (ex) {
      if (this.isAuthenticated()) {
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

      // Loop the informationResponse as object to set the properties on the auth object
      for (const key in informationResponse) {
        if (Object.prototype.hasOwnProperty.call(informationResponse, key)) {
          // @ts-ignore
          auth[key] = informationResponse[key];
        }
      }
      if(this.moduleConfig.authType !== 'header') {
        auth.token = '';
      }

      this.setAuthorization(auth);

      result.data = this.getAuthorization();
    } catch (ex) {
      // Check status code, if it's in the 400 range, clear the authorization
      const isHttpResponseError = ex instanceof HttpErrorResponse;
      if (isHttpResponseError) {
        const response = ex as HttpErrorResponse;
        if (response.status >= 401 && response.status < 500) {
          this.clearAuthorization();
        }
      }

      const errResult = NhApiUtil.taskResultFromResponse(ex);
      errResult.copyTo(result);
    }

    return result;
  }

  async logout(): Promise<TaskResult<void>> {
    const result = new TaskResult<void>();
    const refreshToken = this.getAuthorization()?.refreshToken ?? null;
    let httpParams = new HttpParams();
    if (httpParams.get('language') === null) {
      httpParams = httpParams.set('language', this.moduleConfig.language);
    }
    const request$ = this.httpClient.post<void>(this.moduleConfig.authApiBaseUrl + this.moduleConfig.authentication.endpoints.logout, { refreshToken }, {
      params: httpParams,
      withCredentials: true
    });

    try {
      await lastValueFrom(request$);

      if (result.isSuccess) {
        this.clearAuthorization();
      }
    } catch (ex) {
      const errResult = NhApiUtil.taskResultFromResponse(ex);
      errResult.copyTo(result);
    }

    return result;
  }

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
