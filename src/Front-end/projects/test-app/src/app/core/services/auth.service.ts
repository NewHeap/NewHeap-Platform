import {BaseNhAuthService} from "nh-common";
import {Authorization} from "../models/auth.models";
import {Inject, Injectable} from "@angular/core";

@Injectable({
  providedIn: 'root'
})
export class AuthService extends BaseNhAuthService<Authorization> {
  protected override initEmptyAuthorization(): Authorization {
      return new Authorization();
  }

  constructor() {
    super();
  }
}
