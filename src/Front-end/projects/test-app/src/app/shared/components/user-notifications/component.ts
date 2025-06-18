import {Component} from '@angular/core'
import {
  NhUserNotificationsAbstractComponent
} from "nh-common";

@Component({
  selector: 'user-notifications',
  templateUrl: 'component.html',
  styleUrls: ['component.scss'],
  standalone: false
})
export class UserNotificationsComponent extends NhUserNotificationsAbstractComponent {

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
