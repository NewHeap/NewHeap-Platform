export class User {
  username?: string;
  customerId?: number;
  customerGuid?: string;
  isGuest: boolean = false;

  public constructor(init?: Partial<User>) {
    Object.assign(this, init);
  }
}

export class Authorization {
  realm: string = '';
  provider: string = '';
  token: string = '';
  expiration: string | undefined;
  refreshToken: string | undefined;
  refreshTokenExpires: string | undefined;
  user: User = new User();
  claims: Claim[] = [];
  divisions: Division[] = [];
  activeDivision?: Division;

  public constructor(init?: Partial<Authorization>) {
    Object.assign(this, init);
  }
}

export class Division {
  id: string = '';
  creationDateTime: any;
  lastModifiedDateTime: any;
  name: string = '';
  description: string = '';
  userSelectAllowed: boolean = false;
  timeZoneId: string = '';

  public constructor(init?: Partial<Division>) {
    Object.assign(this, init);
  }
}

export class DivisionRole {
  id?: string;
  name?: string;

  public constructor(init?: Partial<DivisionRole>) {
    Object.assign(this, init);
  }
}

export class DivisionUser {
  id?: string;
  lockOutStartDateTime?: string;
  lockOutEndDateTime?: string;
  userId?: string;
  user?: User;
  divisionId?: string
  division?: Division;
  roles: Array<DivisionRole> = [];
  roleIds: Array<string> = [];

  public constructor(init?: Partial<DivisionUser>) {
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

export class ClaimAuthenticateSessionAccountViewModel {
  completed: boolean = false;
  success: boolean = false;
  errorMessages: string[] = [];
  token: Authorization = new Authorization();

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
  Permission = 'platform.permission',
  DivisionRole = 'platform.division.role',
  DivisionPermission = 'platform.division.permission',
}

export class AuthenticateModel {
  realm: string = '';
  username!: string;
  password: string | undefined;

  public constructor(init?: Partial<AuthenticateModel>) {
    Object.assign(this, init);
  }
}

export class AuthForgotPasswordModel {
  username!: string;

  public constructor(init?: Partial<AuthForgotPasswordModel>) {
    Object.assign(this, init);
  }
}
