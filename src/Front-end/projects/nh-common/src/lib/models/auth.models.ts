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

