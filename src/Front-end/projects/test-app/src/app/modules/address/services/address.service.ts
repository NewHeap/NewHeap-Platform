import {Injectable} from "@angular/core";
import {NhBaseApiService} from "nh-common";

@Injectable({
  providedIn: 'root'
})
export class AddressService extends NhBaseApiService {
  constructor() {
    super('address');
  }
}
