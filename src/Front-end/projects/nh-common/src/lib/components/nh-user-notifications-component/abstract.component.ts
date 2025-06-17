import {Component, inject, Input, input, OnDestroy, OnInit} from '@angular/core'
import { TaskResult } from '../../models/misc.models';
import {
  CollectionHttpRequestOptions, CollectionHttpResponse,
  NhCollectionBaseComponent, NhCommonModuleConfig,
  NhModalService,
  NhUserNotification, NhUserNotificationCollectionHttpRequestOptions,
  NhUserNotificationService
} from "nh-common";
import {Observable} from 'rxjs';

@Component({
  selector: 'nh-user-notifications',
  templateUrl: 'component.html',
  styleUrls: ['component.scss'],
  standalone: false
})
export abstract class NhUserNotificationsComponent extends NhCollectionBaseComponent<NhUserNotification> implements OnInit, OnDestroy {
  @Input() allowStart: boolean = true;
  @Input() allowStop: boolean = true;
  protected readonly userNotificationService: NhUserNotificationService = inject(NhUserNotificationService);
  protected readonly moduleConfig: NhCommonModuleConfig = inject(NhCommonModuleConfig);
  readonly partialLocalStorageKey = input<string>('nh-user-notification-collection');
  override requestOptions = new NhUserNotificationCollectionHttpRequestOptions();
  userNotificationState = this.userNotificationService.state;
  protected userNotificationSubscription = this.userNotificationService.userNotificationState$.subscribe((state) => {
    this.userNotificationState = state;
  });

  constructor() {
    super();
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

  }

  async markAsRead(notification: NhUserNotification): Promise<TaskResult<unknown>> {
    const taskResult = new TaskResult();
    try {
      await this.userNotificationService.markAsRead(notification.id).lastValueFrom();

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
    } catch (ex) {
      taskResult.addError('', 'Something went wrong while marking all notifications as read.');
      return taskResult;
    }

    return taskResult;
  }
}
