import {Component, inject, Input, input, OnDestroy, OnInit} from '@angular/core'
import { TaskResult } from '../../models/misc.models';

import {Observable} from 'rxjs';
import { NhCollectionBaseComponent } from '../nh-collection-base-component/component';
import {
  NhUserNotification,
  NhUserNotificationCollectionHttpRequestOptions, NhUserNotificationState
} from '../../models/user-notification.models';
import {NhUserNotificationService} from "../../services/nh-user-notification.service";
import {NhCommonModuleConfig} from "../../models/config.models";
import {CollectionHttpResponse} from "../../models/http.models";

@Component({
  selector: 'nh-user-notifications-abstract',
  template: ``,
  standalone: false
})
export abstract class NhUserNotificationsAbstractComponent extends NhCollectionBaseComponent<NhUserNotification> implements OnInit, OnDestroy {
  @Input() allowStart: boolean = true;
  @Input() allowStop: boolean = true;
  @Input() itemsPerPage: number = 999;
  @Input() autoLoadNotificationChanges: boolean = true;
  protected readonly userNotificationService: NhUserNotificationService = inject(NhUserNotificationService);
  protected readonly moduleConfig: NhCommonModuleConfig = inject(NhCommonModuleConfig);
  readonly partialLocalStorageKey = input<string>('nh-user-notification-collection');
  override requestOptions = new NhUserNotificationCollectionHttpRequestOptions();
  userNotificationState = this.userNotificationService.state;
  private lastNotificationDate = this.userNotificationState.overview.lastNotificationDate;
  private lastNotificationCount = this.userNotificationState.overview.totalCount;
  private lastUnreadNotificationCount = this.userNotificationState.overview.unreadCount;
  protected userNotificationSubscription = this.userNotificationService.userNotificationState$.subscribe(async (state) => {
    this.userNotificationState = state;

    const hasNewNotifications = this.lastNotificationDate && this.lastNotificationDate != this.userNotificationState.overview.lastNotificationDate;
    const countChanged = this.lastNotificationCount && this.lastNotificationCount != this.userNotificationState.overview.totalCount;
    const unreadCountChanged = this.lastUnreadNotificationCount && this.lastUnreadNotificationCount != this.userNotificationState.overview.unreadCount;

    if(hasNewNotifications || countChanged || unreadCountChanged) {
      if(this.autoLoadNotificationChanges) {
        try {
          await this.reload();
        }catch (ex) {
          console.error('Error while loading new notifications:', ex);
        }
      }

      this.onNewNotificationsAvailable(state).catch((error) => {
        console.error('Error handling user notification state change:', error);
      });
    }
  });

  constructor() {
    super();
  }

  protected onNewNotificationsAvailable(state: NhUserNotificationState): Promise<void> {
    return Promise.resolve();
  }

  override ngOnInit() {
    super.ngOnInit();
    if(this.allowStart) {
      this.userNotificationService.start();
    }
  }

  override ngOnDestroy() {
    super.ngOnDestroy();
    if(this.allowStop) {
      this.userNotificationService.stop();
    }

    this.userNotificationSubscription?.unsubscribe();
  }

  getInitialRequestOptions() {
    return new NhUserNotificationCollectionHttpRequestOptions({
      itemsPerPage: this.moduleConfig.defaultItemsPerPage
    });
  }

  override getLocalStoragePartialKey(): string | null {
    return this.partialLocalStorageKey();
  }

  async onLoad(requestOptions: NhUserNotificationCollectionHttpRequestOptions) {
    return <Observable<CollectionHttpResponse<NhUserNotification>>>this.userNotificationService.getNotifications(requestOptions);
  }

  override async afterLoad() {
    this.lastNotificationDate = this.userNotificationState.overview.lastNotificationDate;
    this.lastNotificationCount = this.userNotificationState.overview.totalCount;
    this.lastUnreadNotificationCount = this.userNotificationState.overview.unreadCount;
  }

  async reload() {
    const currentPage = this.requestOptions.page;
    const itemsPerPage = this.requestOptions.itemsPerPage;

    const loadPromises: any[] = [];

    for(let i = 1; i <= currentPage; i++) {
      const requestOptions = <NhUserNotificationCollectionHttpRequestOptions>JSON.parse(JSON.stringify(this.requestOptions));
      requestOptions.page = i;
      const loadObservable = await this.onLoad(requestOptions);
      loadPromises.push(loadObservable.lastValueFrom());
    }

    const results = await Promise.all(loadPromises);
    this.userNotificationService.loadState().then();

    this.collectionResponse.items = [];

    for(const result of results) {
      if(result && result.items) {
        this.collectionResponse.items.push(...result.items);
      }
    }
  }

  async markAsRead(notification: NhUserNotification): Promise<TaskResult<unknown>> {
    const taskResult = new TaskResult();
    try {
      await this.userNotificationService.markAsRead(notification.id).lastValueFrom();
      this.userNotificationService.loadState().then();
      notification.isLastRead = true;
    }catch (ex) {
      taskResult.addError('', 'Something went wrong while marking the notification as read.');
      return taskResult;
    }

    return taskResult;
  }

  async markAllAsRead(): Promise<TaskResult<unknown>> {
    const taskResult = new TaskResult();
    try {
      await this.userNotificationService.markAllAsRead().lastValueFrom();
      this.reload().then();
    } catch (ex) {
      taskResult.addError('', 'Something went wrong while marking all notifications as read.');
      return taskResult;
    }

    return taskResult;
  }

  async archive(notification: NhUserNotification): Promise<TaskResult<unknown>> {
    const taskResult = new TaskResult();
    try {
      await this.userNotificationService.archive(notification.id).lastValueFrom();
      this.collectionResponse.items = this.collectionResponse.items.filter(item => item.id !== notification.id);
    }catch (ex) {
      taskResult.addError('', 'Something went wrong while marking the notification as archived.');
      return taskResult;
    }

    this.collectionResponse.items = this.collectionResponse.items.filter(item => item.id !== notification.id);

    return taskResult;
  }

  async archiveAll(): Promise<TaskResult<unknown>> {
    const taskResult = new TaskResult();
    try {
      await this.userNotificationService.archiveAll().lastValueFrom();
      this.collectionResponse.items = [];
      this.reload().then();
    } catch (ex) {
      taskResult.addError('', 'Something went wrong while marking all notifications archived.');
      return taskResult;
    }

    await this.firstPage();

    return taskResult;
  }
}
