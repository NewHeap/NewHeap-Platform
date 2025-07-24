import {inject, Injectable, NgZone, OnDestroy, PLATFORM_ID, REQUEST_CONTEXT} from '@angular/core';
import {BehaviorSubject, lastValueFrom} from 'rxjs';
import {
  AuthenticateModel, AuthenticationFlow,
  AuthenticationSessionCreateResponse,
  AuthSessionExpirationInformation,
  Claim,
  ClaimTypes,
  ImpersonateAuthenticateModel,
  INhAuthorization,
  NhAccountInformationResponse,
  NhAuthorization,
  NhDivision,
  RefreshTokenLoginAccountMutateModel,
  RevertImpersonateAuthenticateModel
} from "../models/auth.models";
import {DateTime} from "luxon";
import {TaskResult} from "../models/misc.models";
import {HttpClient, HttpHeaders, HttpParams} from '@angular/common/http';
import {NhCommonModuleConfig} from "../models/config.models";
import {NhApiUtil} from "../util/nh-api-util";
import {isPlatformServer} from "@angular/common";
import {Token} from "@angular/compiler";

@Injectable()
export abstract class BaseNhAuthService<TAuthorization extends INhAuthorization> implements OnDestroy {
  protected authorization: TAuthorization | undefined = undefined;
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
    localStorage.setItem('at', btoa(JSON.stringify(auth)));
    this.authorization = auth;
    if (!noEvent) {
      this.authSubject.next(this.authorization);
    }
    this.dispatchSessionExpirationInformationChanged();
  }

  protected abstract initEmptyAuthorization(): TAuthorization;

  public getAuthorization(fromCache: boolean = true): TAuthorization | undefined {
    let auth: TAuthorization = this.initEmptyAuthorization();

    try {
      auth = JSON.parse(atob(localStorage.getItem('at') ?? ''));
    } catch (ex) {
    }

    return auth;
  }

  public isAuthenticated(): boolean {
    const auth = this.getAuthorization();
    let authenticated = true;
    if (this.moduleConfig.authType === 'cookie') {
      authenticated = ((auth?.token?.length ?? 0) > 0);
    }

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

  async authenticate(model: AuthenticateModel, loginAsUser: boolean = false): Promise<TaskResult<TAuthorization>> {
    const result = new TaskResult<TAuthorization>();
    let httpParams = new HttpParams();
    if (httpParams.get('language') === null) {
      httpParams = httpParams.set('language', this.moduleConfig.language);
    }

    model.realm = this.moduleConfig.authenticationRealm;

    const request$ = this.httpClient.post<TAuthorization>(this.moduleConfig.authApiBaseUrl + this.moduleConfig.authentication.endpoints.login, model, {
      params: httpParams,
      withCredentials: true
    });

    try {
      result.data = await lastValueFrom(request$);

      if (result.isSuccess) {
        result.data.realm = this.moduleConfig.authenticationRealm;
      }

      this.setAuthorization(result.data);
    } catch (ex) {
      if (this.isAuthenticated()) {
        this.clearAuthorization();
      }

      const errResult = NhApiUtil.taskResultFromResponse(ex);
      errResult.copyTo(result);
    }

    return result;
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
      if (this.isAuthenticated()) {
        this.clearAuthorization();
      }

      const errResult = NhApiUtil.taskResultFromResponse(ex);
      errResult.copyTo(result);
    }

    return result;
  }

  async logout(): Promise<TaskResult<void>> {
    const result = new TaskResult<void>();
    let httpParams = new HttpParams();
    if (httpParams.get('language') === null) {
      httpParams = httpParams.set('language', this.moduleConfig.language);
    }
    const request$ = this.httpClient.post<void>(this.moduleConfig.authApiBaseUrl + this.moduleConfig.authentication.endpoints.logout, {}, {
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
