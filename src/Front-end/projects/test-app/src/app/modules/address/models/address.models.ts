import {CollectionHttpRequestOptions} from "nh-common";

export class AddressCollectionHttpRequestOptions extends CollectionHttpRequestOptions {
  countryCodes: string[] = [];
  public constructor(init?: Partial<AddressCollectionHttpRequestOptions>) {
    super(init);
    Object.assign(this, init);
  }
}

export class Address {
  id?: string;
  creationDateTime: any;
  lastModifiedDateTime: any;
  country?: string;
  countryCode?: string;
  province?: string;
  municipality?: string;
  addressCode?: string;
  place?: string;
  postalCode?: string;
  street?: string;
  streetObjectNumber?: string;
  streetObjectNumberSuffix?: string;
  streetObjectRoomNumber?: string;
  locationDescription?: string;
  locationLongitude?: string;
  locationLatitude?: string;
  identifiableKey?: string;
  computedCompleteAddress?: string;

  public constructor(init?: Partial<Address>) {
    Object.assign(this, init);
  }
}
