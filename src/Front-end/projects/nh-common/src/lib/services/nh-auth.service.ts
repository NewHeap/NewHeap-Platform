import {Inject, Injectable, NgZone, OnDestroy, Optional, PLATFORM_ID, REQUEST_CONTEXT} from '@angular/core';
import {BehaviorSubject, lastValueFrom} from 'rxjs';
import {
  AccountInformationResponse,
  AuthenticateModel,
  AuthenticationSessionCreateResponse,
  Authorization,
  AuthSessionExpirationInformation, CheckAuthenticateSessionModel,
  Claim, ClaimAuthenticateSessionAccountMutateModel, ClaimAuthenticateSessionAccountViewModel,
  ClaimTypes,
  Division, RefreshTokenLoginAccountMutateModel
} from "../models/auth.models";
import {DateTime} from "luxon";
import {TaskResult} from "../models/misc.models";
import {HttpClient, HttpHeaders, HttpParams} from '@angular/common/http';
import {Base64} from "js-base64";
import {NhCommonModuleConfig} from "../models/config.models";
import {NhApiUtil} from "../util/nh-api-util";
import {isPlatformServer} from "@angular/common";

@Injectable({
  providedIn: 'root'
})
export class NhAuthService implements OnDestroy {
  private authorization: Authorization|undefined = undefined;
  private authSession: AuthenticationSessionCreateResponse|undefined = undefined;
  public readonly authSubject = new BehaviorSubject<Authorization|undefined>(this.getAuthorization());
  private onReady: ((value: (PromiseLike<unknown> | unknown)) => void) | undefined;
  public readonly authReady: Promise<unknown>;

  private _sessionExpirationInformation = new BehaviorSubject<AuthSessionExpirationInformation>(this.getSessionExpirationInformation());
  public sessionExpirationInformationChanged = this._sessionExpirationInformation.asObservable();
  private intervalHandle: any;

  constructor(
    private zone: NgZone,
    private moduleConfig: NhCommonModuleConfig,
    private httpClient: HttpClient,
    @Inject(PLATFORM_ID) private platformId: Object,
    @Optional() @Inject(REQUEST_CONTEXT) private requestContext: any,
  ) {
    this.authReady = new Promise(resolve => {
      this.onReady = resolve;
    });

    if(!isPlatformServer(this.platformId)) {
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
    if(this.intervalHandle) {
      clearInterval(this.intervalHandle);
    }
  }

  private getAllPermissionClaimTypes() {
    let types = new Array<string>();
    types.push(ClaimTypes.Permission);

    for(const type of this.moduleConfig?.authentication?.additionalClaimPermissionTypes ?? []) {
      types.push(type);
    }

    return types;
  }

  private getAllDivisionPermissionClaimTypes() {
    let types = new Array<string>();
    types.push(ClaimTypes.DivisionPermission);

    for(const type of this.moduleConfig?.authentication?.additionalDivisionClaimPermissionTypes ?? []) {
      types.push(type);
    }

    return types;
  }

  public getSessionExpirationInformation() {
    const result = new AuthSessionExpirationInformation({
      isAuthenticated: this.isAuthenticated()
    });

    if(result.isAuthenticated) {
      const auth = this.getAuthorization()!;
      const nowDate = DateTime.utc();
      const authDate = DateTime.fromISO(auth.validTo ?? '').toUTC();
      const duration = authDate.diff(nowDate);

      result.expiresWithin15MinutesOrExpired = duration.minutes <= 15;
      if(duration.seconds >= 0) {
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

  public clearAuthorization(): void {
    if (this.isAuthenticated()) {
      try {
        localStorage.removeItem('at');
      }finally {}

      this.authorization = undefined;
      this.authSubject.next(this.authorization);
      this.dispatchSessionExpirationInformationChanged();
    }
  }

  public setAuthorization(auth: Authorization): void {
    localStorage.setItem('at', btoa(JSON.stringify(auth)));
    this.authorization = auth;
    this.authSubject.next(this.getAuthorization());
    this.dispatchSessionExpirationInformationChanged();
  }

  public getAuthorization(fromCache: boolean = true): Authorization|undefined {
    let auth: Authorization = new Authorization();
    try {
      auth = JSON.parse(atob(localStorage.getItem('at') ?? ''));
    } catch (ex) {
    }

    return auth;
  }

  public isAuthenticated(): boolean {
    const auth = this.getAuthorization();
    let authenticated = ((auth?.token?.length ?? 0) > 0);

    if (authenticated && auth) {
      authenticated = (DateTime.fromISO(auth.validTo ?? '') >= DateTime.utc());
    }

    return authenticated;
  }

  public getActiveDivision(): Division|null {
    let division: Division|null = null;

    const auth = this.getAuthorization();

    if(auth && auth.user && auth.activeDivision) {
      division = auth.activeDivision;
    }

    return division;
  }

  public getActiveDivisionId(): string|undefined {
    let divisionId: string|undefined = undefined;
    const auth = this.getAuthorization();

    if((auth?.user?.activeDivisionId?.length ?? 0) > 0) {
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
    return (null != auth && auth.claims != null && null != auth.claims.find(x => x.type === claim.type && x.value === claim.value));
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
      for(const claimType of this.getAllPermissionClaimTypes()) {
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

  public isOneDivisionPermissionGranted(divisionId: string|undefined, permissions: Array<string>) {
    if (!permissions || permissions.length < 1) {
      return true;
    }

    for (const permission of permissions) {
      for(const claimType of this.getAllDivisionPermissionClaimTypes()) {
        if (this.isClaimGranted(<Claim>{type: claimType, value: (divisionId ?? '') + '_' + permission})) {
          return true;
        }
      }
    }

    return false;
  }

  public isOneDivisionRoleGranted(divisionId: string|undefined, roles: Array<string>) {
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

  async checkAuthSession(): Promise<TaskResult<CheckAuthenticateSessionModel>> {
    const result = new TaskResult<CheckAuthenticateSessionModel>({
      data: new CheckAuthenticateSessionModel()
    });
    const authSession = this.getAuthSession();

    if(authSession && result.data) {
      result.data.didTry = true;
      try {
        const authSessionExpired = !((Date.parse(authSession?.expirationDateTime ?? '') - Date.parse(new Date().toISOString())) > 0);
        if(authSessionExpired) {
          this.clearAuthSession();
          result.data?.errorMessages.push('The authentication session has expired.');
          return result;
        }

        const claimResult = await this.claimAuthenticationSession(new ClaimAuthenticateSessionAccountMutateModel({
          sessionToken: authSession.sessionToken
        }));

        result.data.completed = claimResult.data?.completed ?? false;
        result.data.success = claimResult.isSuccess && (claimResult.data?.success ?? false);

        if(!claimResult.isSuccess) {
          result.data.errorMessages = result.data?.errorMessages.concat(claimResult.getAllErrorMessages());
        }
      } catch (ex) {
        result.data.success = false;
        result.data?.errorMessages.push('An unknown error occurred.');
        this.clearAuthSession();
      }
    }

    return result;
  }

  public clearAuthSession(): void {
    try {
      localStorage.removeItem('as');
    } catch (ex) {}

    this.authSession = undefined;
  }

  public setAuthSession(authSession: AuthenticationSessionCreateResponse): void {
    localStorage.setItem('as', Base64.encode((JSON.stringify(authSession))));
    this.authSession = authSession;
  }

  public getAuthSession(fromCache: boolean = true): AuthenticationSessionCreateResponse|undefined {
    if(fromCache && this.authSession) {
      return this.authSession;
    }

    try {
      const authSessionString = localStorage.getItem('as') || '';
      this.authSession = undefined;
      if((authSessionString?.length ?? 0 ) > 0) {
        this.authSession = JSON.parse(Base64.decode(authSessionString));
      }
    } catch (ex) {}

    return this.authSession;
  }

  async authenticate(model: AuthenticateModel, loginAsUser: boolean = false): Promise<TaskResult<Authorization>> {
    const result = new TaskResult<Authorization>();
    let httpParams = new HttpParams();
    if (httpParams.get('language') === null) {
      httpParams = httpParams.set('language', this.moduleConfig.language);
    }

    model.realm = this.moduleConfig.authenticationRealm;

    const request$ = this.httpClient.post<Authorization>(this.moduleConfig.authApiBaseUrl + this.moduleConfig.authentication.endpoints.login, model, {
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
      if(this.isAuthenticated()) {
        this.clearAuthorization();
      }

      const errResult = NhApiUtil.taskResultFromResponse(ex);
      errResult.copyTo(result);
    }

    return result;
  }

  public async reloadAuthorizationProfile(): Promise<TaskResult<Authorization>> {
    const result = new TaskResult<Authorization>();

    const auth = this.getAuthorization();
    if(!auth) {
      return result.withError('', 'Not authenticated.');
    }

    let httpParams = new HttpParams();
    if (httpParams.get('language') === null) {
      httpParams = httpParams.set('language', this.moduleConfig.language);
    }

    let httpHeaders = new HttpHeaders();
    httpHeaders = httpHeaders.set('Authorization', `Bearer ${auth.token}`);

    const request$ = this.httpClient.get<AccountInformationResponse>(this.moduleConfig.authApiBaseUrl + this.moduleConfig.authentication.endpoints.accountInformation, {
      params: httpParams,
      headers: httpHeaders,
      withCredentials: true
    });

    try {
      const informationResponse = await lastValueFrom(request$);

      auth.claims = informationResponse.claims;
      auth.user = informationResponse.user;
      auth.divisions = informationResponse.divisions;
      auth.activeDivision = informationResponse.activeDivision;

      this.setAuthorization(auth);

      result.data = this.getAuthorization();
    } catch (ex) {
      if(this.isAuthenticated()) {
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

  async authenticateRefreshToken(model: RefreshTokenLoginAccountMutateModel): Promise<TaskResult<Authorization>> {
    const result = new TaskResult<Authorization>();

    let httpParams = new HttpParams();
    if (httpParams.get('language') === null) {
      httpParams = httpParams.set('language', this.moduleConfig.language);
    }

    const request$ = this.httpClient.post<Authorization>(this.moduleConfig.authApiBaseUrl + this.moduleConfig.authentication.endpoints.refresh, model, {
      params: httpParams,
      withCredentials: true
    });

    try {
      result.data = await lastValueFrom(request$);
      if(result.isSuccess) {
        this.setAuthorization(result.data);
      }
    } catch (ex) {
      const errResult = NhApiUtil.taskResultFromResponse(ex);
      errResult.copyTo(result);
    }

    return result;
  }

  async createAuthenticationSession(): Promise<TaskResult<AuthenticationSessionCreateResponse>> {
    const result = new TaskResult<AuthenticationSessionCreateResponse>();
    let httpParams = new HttpParams();
    if (httpParams.get('language') === null) {
      httpParams = httpParams.set('language', this.moduleConfig.language);
    }

    if(true) {
      //throw new Error('Not implemented');
    }

    const request$ = this.httpClient.post<AuthenticationSessionCreateResponse>(this.moduleConfig.authApiBaseUrl + '/auth/CreateSession', {}, {
      params: httpParams,
      withCredentials: true
    });

    try {
      result.data = await lastValueFrom(request$);
      this.setAuthSession(result.data);
    } catch (ex) {
      const errResult = NhApiUtil.taskResultFromResponse(ex);
      errResult.copyTo(result);
    }

    return result;
  }

  async claimAuthenticationSession(model: ClaimAuthenticateSessionAccountMutateModel): Promise<TaskResult<ClaimAuthenticateSessionAccountViewModel>> {
    const result = new TaskResult<ClaimAuthenticateSessionAccountViewModel>();
    let httpParams = new HttpParams();
    if (httpParams.get('language') === null) {
      httpParams = httpParams.set('language', this.moduleConfig.language);
    }

    const request$ = this.httpClient.post<ClaimAuthenticateSessionAccountViewModel>(this.moduleConfig.authApiBaseUrl + '/auth/ClaimSession', model, {
      params: httpParams,
      withCredentials: true
    });

    try {
      result.data = await lastValueFrom(request$);

      if(result.data?.completed) {
        const claimResult = result.data;
        if(claimResult.success && claimResult.token) {
          this.setAuthorization(claimResult.token);
        }

        this.clearAuthSession();
      }
    } catch (ex) {
      const errResult = NhApiUtil.taskResultFromResponse(ex);
      errResult.copyTo(result);
    }

    if((result.data?.errorMessages?.length ?? 0) > 0) {
      result.addError('', result.data?.errorMessages ?? '');
    }

    return result;
  }
}
