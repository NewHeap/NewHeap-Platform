import { CollectionHttpRequestOptions } from "./http.models";

export class UserCollectionHttpRequestOptions extends CollectionHttpRequestOptions {
  includeArchived?: boolean;
  excludeNonDivisionAccess?: boolean;
  roles?: Array<string>;
  divisionIds?: Array<string>;

  public constructor(init?: Partial<UserCollectionHttpRequestOptions>) {
    super(init);
    Object.assign(this, init);
  }
}

export class UserMutateModel {
  email: string = '';
  registerUrl: string = '';

  public constructor(init?: Partial<UserMutateModel>) {
    Object.assign(this, init);
  }
}
