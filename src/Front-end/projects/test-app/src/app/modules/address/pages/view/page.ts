import {Component} from "@angular/core";
import {Subscription} from "rxjs";
import {NhPageBaseComponent} from "nh-common";
import {Address} from "../../models/address.models";
import {AddressService} from "../../services/address.service";

@Component({
  selector: 'app-address-page-view',
  templateUrl: './page.html',
  styleUrls: ['./page.scss'],
  standalone: false
})
export class ViewAddressPage extends NhPageBaseComponent {
  addressId: string | undefined;
  routeParam$: Subscription|undefined;
  addressLoad$: Subscription|undefined;
  address: Address | undefined;

  constructor(
    private addressService: AddressService
  ) {
    super();
    this.routeParam$ = this.activatedRoute.paramMap.subscribe(async paramMap => {
      this.addressId = paramMap.get('id') ?? '';
      this.load().then();
    });
  }

  override async appOnDestroy(): Promise<void> {
    this.routeParam$?.unsubscribe();
    this.addressLoad$?.unsubscribe();
  }

  async load() {
    if(!this.addressId) {
      this.address = undefined;
      return;
    }

    const loadPromises = [
      this.loadAddress(),
    ];

    const results = await Promise.all(loadPromises);
  }

  async loadAddress() {
    if(!this.addressId) {
      return;
    }

    this.addressLoad$?.unsubscribe();
    this.addressLoad$ = this.addressService.get<Address>(this.addressId).subscribe(address => {
      this.address = address;
    });
  }
}
