import {Component} from '@angular/core'

import {NhUserNotificationsAbstractComponent} from "./abstract.component";

@Component({
  selector: 'nh-user-notifications',
  templateUrl: 'component.html',
  styleUrls: ['component.scss'],
  standalone: false
})
export class NhUserNotificationsComponent extends NhUserNotificationsAbstractComponent {

  constructor() {
    super();
  }

  override ngOnInit() {
    super.ngOnInit();
  }

  override ngOnDestroy() {
    super.ngOnDestroy();
  }
}
