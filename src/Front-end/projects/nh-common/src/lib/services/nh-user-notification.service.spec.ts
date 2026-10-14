import {discardPeriodicTasks, fakeAsync, TestBed, tick} from '@angular/core/testing';
import {of} from 'rxjs';

import '../prototype-extensions/observable.extensions';
import {NhCommonModuleConfig, UserNotificationNhCommonModuleConfig} from '../models/config.models';
import {NhUserNotificationOverview} from '../models/user-notification.models';
import {NhApiService} from './nh-api.service';
import {NhAppService} from './nh-app.service';
import {NhAuthService} from './nh-auth.service';
import {NhUserNotificationService} from './nh-user-notification.service';

describe('NhUserNotificationService', () => {
  it('polls the overview only while there is a session', fakeAsync(() => {
    let authenticated = false;
    const requestedUrls: string[] = [];
    TestBed.configureTestingModule({
      providers: [
        {
          provide: NhCommonModuleConfig,
          useValue: new NhCommonModuleConfig({
            apiBaseUrl: '/api',
            userNotification: new UserNotificationNhCommonModuleConfig({
              urlSuffix: '/user-notifications',
              pollingInterval: 1000
            })
          })
        },
        {provide: NhAppService, useValue: {isPlatformServer: () => false}},
        {provide: NhAuthService, useValue: {isAuthenticated: () => authenticated}},
        {
          provide: NhApiService,
          useValue: {
            get: (url: string) => {
              requestedUrls.push(url);
              return of(new NhUserNotificationOverview());
            }
          }
        }
      ]
    });
    const service = TestBed.inject(NhUserNotificationService);

    service.start();
    tick(0);
    tick(1000);
    expect(requestedUrls).toEqual([]);

    authenticated = true;
    tick(1000);
    expect(requestedUrls).toEqual(['/api/user-notifications/overview']);

    service.stop();
    discardPeriodicTasks();
  }));
});
