import {Component} from '@angular/core';
import {faBuilding, faCalendar} from "@fortawesome/free-solid-svg-icons";
import {ClaimTypes, NhPageBaseComponent} from "nh-common";

@Component({
  selector: 'app-address-page-overview',
  templateUrl: './page.html',
  styleUrls: ['./page.scss'],
  standalone: false
})
export class OverviewAddressPage extends NhPageBaseComponent {
  protected readonly faBuilding = faBuilding;
  protected readonly faCalendar = faCalendar;
  claimTypes = ClaimTypes;
  constructor() {
    super();
  }

  async load() {

  }


}
