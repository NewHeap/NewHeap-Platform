export interface INhUser {
  id: string | undefined;
  email: string | undefined;
  creationDateTime: any;
  emailConfirmed: boolean;
  lockoutEnd: any;
  lockoutStart: any;
  activeDivisionId: string | undefined;
  activeDivision: INhDivision | undefined;
  roles: Array<string>;
}

export class NhUser implements INhUser {
  id: string | undefined;
  email: string | undefined;
  creationDateTime: any;
  emailConfirmed: boolean = false;
  lockoutEnd: any;
  lockoutStart: any;
  activeDivisionId: string | undefined;
  activeDivision: INhDivision | undefined;
  division: INhDivision | undefined;
  roles: Array<string> = [];

  public constructor(init?: Partial<NhUser>) {
    Object.assign(this, init);
  }
}

export interface INhAuthorization {
  realm: string;
  provider: string;
  token: string;
  validTo: string | undefined;
  refreshToken: string | undefined;
  refreshTokenExpires: string | undefined;
  user?: INhUser;
  claims: Claim[];
  divisions: INhDivision[];
  activeDivision?: INhDivision;
}

export class NhAuthorization implements INhAuthorization {
  realm: string = '';
  provider: string = '';
  token: string = '';
  validTo: string | undefined;
  refreshToken: string | undefined;
  refreshTokenExpires: string | undefined; // Not implemented yet
  user?: INhUser;
  claims: Claim[] = [];
  divisions: INhDivision[] = [];
  activeDivision?: INhDivision;

  public constructor(init?: Partial<NhAuthorization>) {
    Object.assign(this, init);
  }
}

export interface INhAccountInformationResponse {
  user?: INhUser;
  claims: Claim[];
  divisions: INhDivision[];
  activeDivision?: INhDivision;
}

export class NhAccountInformationResponse implements INhAccountInformationResponse {
  user?: INhUser;
  claims: Claim[] = [];
  divisions: INhDivision[] = [];
  activeDivision?: INhDivision;
  public constructor(init?: Partial<NhAccountInformationResponse>) {
    Object.assign(this, init);
  }
}

export interface INhDivision {
  id: string;
  creationDateTime: any;
  lastModifiedDateTime: any;
  name: string;
  description: string;
  userSelectAllowed: boolean;
  timeZoneId: string;
}

export class NhDivision implements INhDivision {
  id: string = '';
  creationDateTime: any;
  lastModifiedDateTime: any;
  name: string = '';
  description: string = '';
  userSelectAllowed: boolean = false;
  timeZoneId: string = '';

  public constructor(init?: Partial<NhDivision>) {
    Object.assign(this, init);
  }
}

export interface INhDivisionRole {
  id?: string;
  name?: string;
}

export class NhDivisionRole implements INhDivisionRole {
  id?: string;
  name?: string;

  public constructor(init?: Partial<NhDivisionRole>) {
    Object.assign(this, init);
  }
}

export interface INhDivisionUser {
  id?: string;
  lockOutStartDateTime?: string;
  lockOutEndDateTime?: string;
  userId?: string;
  user?: INhUser;
  divisionId?: string;
  division?: NhDivision;
  roles: Array<NhDivisionRole>;
  roleIds: Array<string>;
}

export class NhDivisionUser implements INhDivisionUser {
  id?: string;
  lockOutStartDateTime?: string;
  lockOutEndDateTime?: string;
  userId?: string;
  user?: INhUser;
  divisionId?: string
  division?: INhDivision;
  roles: Array<INhDivisionRole> = [];
  roleIds: Array<string> = [];

  public constructor(init?: Partial<NhDivisionUser>) {
    Object.assign(this, init);
  }
}


export class AuthSessionExpirationInformation {
  isAuthenticated: boolean = false;
  expiresWithin15MinutesOrExpired: boolean = false;
  displayMinutesSecondsUntilExpiration: string = '00:00';

  public constructor(init?: Partial<AuthSessionExpirationInformation>) {
    Object.assign(this, init);
  }
}

export class RefreshTokenLoginAccountMutateModel {
  token: string = '';
  refreshToken: string = '';

  public constructor(init?: Partial<RefreshTokenLoginAccountMutateModel>) {
    Object.assign(this, init);
  }
}

export class AuthenticationSessionCreateResponse {
  sessionToken: string = '';
  expirationDateTime: string = '';

  public constructor(init?: Partial<AuthenticationSessionCreateResponse>) {
    Object.assign(this, init);
  }
}

export class ClaimAuthenticateSessionAccountMutateModel {
  sessionToken: string = '';

  public constructor(init?: Partial<ClaimAuthenticateSessionAccountMutateModel>) {
    Object.assign(this, init);
  }
}

export interface IClaimAuthenticateSessionAccountViewModel {
  completed: boolean;
  success: boolean;
  errorMessages: string[];
  token?: INhAuthorization;
}

export class ClaimAuthenticateSessionAccountViewModel implements IClaimAuthenticateSessionAccountViewModel {
  completed: boolean = false;
  success: boolean = false;
  errorMessages: string[] = [];
  token?: INhAuthorization;

  public constructor(init?: Partial<ClaimAuthenticateSessionAccountViewModel>) {
    Object.assign(this, init);
  }
}

export class CheckAuthenticateSessionModel {
  success: boolean = false;
  didTry: boolean = false;
  completed: boolean = false;
  errorMessages: string[] = [];

  public constructor(init?: Partial<CheckAuthenticateSessionModel>) {
    Object.assign(this, init);
  }
}

export class Claim {
  type: string | undefined;
  value: string | undefined;

  public constructor(init?: Partial<Claim>) {
    Object.assign(this, init);
  }
}

export enum ClaimTypes {
  Name = 'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name',
  Email = 'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress',
  NameIdentifier = 'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier',
  Country = 'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/country',
  Role = 'http://schemas.microsoft.com/ws/2008/06/identity/claims/role',
  Permission = 'nh.platform.permission',
  DivisionRole = 'nh.platform.division.role',
  DivisionPermission = 'nh.platform.division.permission',
  ImpersonateOriginUserId = 'nh.platform.auth.impersonate.origin-user-id'
}

export class AuthenticateModel {
  realm: string = '';
  username!: string;
  password: string | undefined;

  public constructor(init?: Partial<AuthenticateModel>) {
    Object.assign(this, init);
  }
}

export class ImpersonateAuthenticateModel {
  userId: string = '';

  public constructor(init?: Partial<ImpersonateAuthenticateModel>) {
    Object.assign(this, init);
  }
}

export class RevertImpersonateAuthenticateModel {
  public constructor(init?: Partial<RevertImpersonateAuthenticateModel>) {
    Object.assign(this, init);
  }
}

export type AuthenticationFlow = 'password' | 'microsoft-oauth' | string;


/**
 * Second-factor methods a user can present after the first factor succeeded.
 */
export const NhTwoFactorMethods = {
  authenticator: 'authenticator',
  recoveryCode: 'recovery-code'
} as const;

export type NhTwoFactorMethod = typeof NhTwoFactorMethods[keyof typeof NhTwoFactorMethods] | string;

/**
 * Safe failure codes returned by the two-factor endpoints. Each error item uses the code as
 * its name and `nh-two-factor.<suffix>` as its translation key.
 */
export enum NhTwoFactorFailureCodes {
  Required = 'two-factor-required',
  EnrollmentRequired = 'two-factor-enrollment-required',
  InvalidCode = 'two-factor-invalid-code',
  ChallengeExpired = 'two-factor-challenge-expired',
  LockedOut = 'two-factor-locked-out',
  MethodNotAllowed = 'two-factor-method-not-allowed',
  NotEnabled = 'two-factor-not-enabled',
  SetupNotStarted = 'two-factor-setup-not-started',
  ReauthenticationRequired = 'two-factor-reauthentication-required',
  ReauthenticationFailed = 'two-factor-reauthentication-failed',
  RequiredByPolicy = 'two-factor-required-by-policy',
  NotAllowedWhileImpersonating = 'two-factor-not-allowed-while-impersonating',
  ConfigurationInvalid = 'two-factor-configuration-invalid'
}

/**
 * A pending sign-in that the user completes with a second factor.
 */
export class NhTwoFactorChallenge {
  status: string = 'two-factor-required';
  challengeToken: string = '';
  expiresAt: string = '';
  methods: NhTwoFactorMethod[] = [];

  public constructor(init?: Partial<NhTwoFactorChallenge>) {
    Object.assign(this, init);
  }
}

/**
 * Login response when two-factor authentication is enabled on the server. A complete
 * session has the token fields; a pending sign-in only has `twoFactor`.
 */
export interface NhLoginResponse {
  token?: string | null;
  validTo?: string | null;
  refreshToken?: string | null;
  refreshValidTo?: string | null;
  issuer?: string | null;
  twoFactor?: NhTwoFactorChallenge | null;
}

export type NhAuthenticationStepStatus = 'authenticated' | 'two-factor-required';

/**
 * Outcome of an interactive sign-in step: either the stored authorization or a challenge.
 */
export class NhAuthenticationStep<TAuthorization extends INhAuthorization = INhAuthorization> {
  status: NhAuthenticationStepStatus = 'authenticated';
  authorization?: TAuthorization;
  challenge?: NhTwoFactorChallenge;

  public constructor(init?: Partial<NhAuthenticationStep<TAuthorization>>) {
    Object.assign(this, init);
  }
}

export class NhTwoFactorVerifyModel {
  challengeToken: string = '';
  method: NhTwoFactorMethod = NhTwoFactorMethods.authenticator;
  code: string = '';

  public constructor(init?: Partial<NhTwoFactorVerifyModel>) {
    Object.assign(this, init);
  }
}

/**
 * Proof that the signed-in user is present before a sensitive change: the current password
 * or a valid second-factor code.
 */
export class NhTwoFactorReauthentication {
  password?: string;
  method?: NhTwoFactorMethod;
  code?: string;

  public constructor(init?: Partial<NhTwoFactorReauthentication>) {
    Object.assign(this, init);
  }
}

export class NhTwoFactorStatus {
  enabled: boolean = false;
  required: boolean = false;
  methods: NhTwoFactorMethod[] = [];
  availableMethods: NhTwoFactorMethod[] = [];
  recoveryCodesLeft: number = 0;
  authenticatorSetupPending: boolean = false;

  public constructor(init?: Partial<NhTwoFactorStatus>) {
    Object.assign(this, init);
  }
}

/**
 * Pending authenticator enrollment. `qrCodeDataUri` is a PNG data URI rendered by the server.
 */
export class NhAuthenticatorSetup {
  sharedKey: string = '';
  authenticatorUri: string = '';
  qrCodeDataUri?: string;

  public constructor(init?: Partial<NhAuthenticatorSetup>) {
    Object.assign(this, init);
  }
}

/**
 * Result of a two-factor change. Recovery codes are returned once. When the change ended
 * every session, the service has already stored the renewed session for this device.
 */
export class NhTwoFactorChange {
  recoveryCodes?: string[];
  sessionRenewed: boolean = false;

  public constructor(init?: Partial<NhTwoFactorChange>) {
    Object.assign(this, init);
  }
}
