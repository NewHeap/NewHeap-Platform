import {NhAuthorization, NhDivision, NhUser} from "nh-common";

export class User extends NhUser {
  override activeDivision: Division | undefined;
  override division: Division | undefined;
  constructor(init?: Partial<User>) {
    super(init);
  }
}

export class Division extends NhDivision {
  constructor(init?: Partial<Division>) {
    super(init);
  }
}

export class Authorization extends NhAuthorization {
  override user: User | undefined;
  override activeDivision: Division | undefined;
  override divisions: Division[] = [];
  constructor(init?: Partial<Authorization>) {
    super(init);
  }
}
