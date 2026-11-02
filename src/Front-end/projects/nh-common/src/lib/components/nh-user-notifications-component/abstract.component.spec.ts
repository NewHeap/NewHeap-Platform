import {TestBed} from '@angular/core/testing';
import {ActivatedRoute, Router} from '@angular/router';
import {provideNhToastr} from '@newheap/nh-toastr';
import {provideTranslateService} from '@ngx-translate/core';
import {BehaviorSubject, of} from 'rxjs';

import '../../prototype-extensions/observable.extensions';
import {NhCommonModuleConfig} from '../../models/config.models';
import {CollectionHttpResponse} from '../../models/http.models';
import {NhUserNotification, NhUserNotificationOverview, NhUserNotificationState} from '../../models/user-notification.models';
import {NhAppService} from '../../services/nh-app.service';
import {NhAuthService} from '../../services/nh-auth.service';
import {NhModalService} from '../../services/nh-modal.service';
import {NhRouterService} from '../../services/nh-router.service';
import {NhUserNotificationService} from '../../services/nh-user-notification.service';
import {NhUserNotificationsAbstractComponent} from './abstract.component';

class Bell extends NhUserNotificationsAbstractComponent {
  shownIds(): string[] { return this.collectionResponse.items.map(item => item.id); }
  close(): void { this.userNotificationSubscription.unsubscribe(); }
}

describe('NhUserNotificationsAbstractComponent', () => {
  it('reads the list again when the polled overview changes, without reading the overview a second time', async () => {
    const state = new NhUserNotificationState();
    const states = new BehaviorSubject<NhUserNotificationState>(state);
    let listReads = 0;
    let overviewReads = 0;
    TestBed.configureTestingModule({
      providers: [
        provideTranslateService(),
        provideNhToastr({disableTimeOut: true, positionClass: 'toast-top-right'}),
        {provide: NhCommonModuleConfig, useValue: new NhCommonModuleConfig()},
        {
          provide: NhUserNotificationService,
          useValue: {
            state,
            userNotificationState$: states.asObservable(),
            getNotifications: () => {
              listReads++;
              return of(new CollectionHttpResponse<NhUserNotification>({items: [new NhUserNotification({id: 'n1'})]}));
            },
            loadState: async () => {
              overviewReads++;
            }
          }
        },
        ...[NhAppService, NhAuthService, NhModalService, NhRouterService, ActivatedRoute, Router]
          .map(provide => ({provide, useValue: {}}))
      ]
    });
    const bell = TestBed.runInInjectionContext(() => new Bell());

    // A poll brought a new notification: the shown page is read again, the overview is not.
    state.overview = new NhUserNotificationOverview({totalCount: 1, unreadCount: 1, lastNotificationDate: '2026-11-02T08:00:00Z'});
    states.next(state);
    await new Promise(resolve => setTimeout(resolve));
    expect([listReads, overviewReads]).toEqual([1, 0]);
    expect(bell.shownIds()).toEqual(['n1']);

    // The same overview again changes nothing.
    states.next(state);
    await new Promise(resolve => setTimeout(resolve));
    expect([listReads, overviewReads]).toEqual([1, 0]);

    // After the person's own action the list and the overview behind the count are read again.
    await bell.reload();
    expect([listReads, overviewReads]).toEqual([2, 1]);

    bell.close();
  });
});
