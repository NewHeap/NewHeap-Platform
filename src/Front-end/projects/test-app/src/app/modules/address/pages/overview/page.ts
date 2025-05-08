import {Component} from '@angular/core';
import {ClaimTypes, NhPageBaseComponent} from "nh-common";

@Component({
  selector: 'app-address-page-overview',
  templateUrl: './page.html',
  styleUrls: ['./page.scss'],
  standalone: false
})
export class OverviewAddressPage extends NhPageBaseComponent {
  claimTypes = ClaimTypes;
  constructor() {
    super();
  }

  async load() {

  }


}
