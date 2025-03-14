import { User } from "./auth.models";
import { CollectionHttpRequestOptions } from "./http.models";

export class DivisionCollectionRequestModel extends CollectionHttpRequestOptions {

}

export class DivisionUserCollectionRequestModel extends CollectionHttpRequestOptions {

}

export class Division {
  id!: string;
  creationDateTime: any;
  lastModifiedDateTime: any;
  name!: string;
  description: string = '';
  userSelectAllowed: boolean = false;
  timeZoneId: string = '';

  public constructor(init?: Partial<Division>) {
    Object.assign(this, init);
  }
}

export class DivisionRole {
  id!: string;
  name!: string;

  public constructor(init?: Partial<DivisionRole>) {
    Object.assign(this, init);
  }
}

export class DivisionUser {
  id!: string;
  creationDateTime: any;
  lastModifiedDateTime: any;
  lockOutStartDateTime?: string;
  lockOutEndDateTime?: string;
  userId!: string;
  user?: User;
  divisionId!: string
  division?: Division;
  roles: Array<DivisionRole> = [];
  roleIds: Array<string> = [];

  public constructor(init?: Partial<DivisionUser>) {
    Object.assign(this, init);
  }
}
