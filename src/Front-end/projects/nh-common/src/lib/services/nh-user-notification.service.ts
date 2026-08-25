import {inject, Injectable, OnDestroy} from '@angular/core';

import {BehaviorSubject, EMPTY, exhaustMap, from, Observable, Subscription, switchMap, timer} from "rxjs";
import { NhAuthService } from './nh-auth.service';
import {NhCommonModuleConfig} from "../models/config.models";
import { NhApiService } from './nh-api.service';
import {
  NhUserNotification,
  NhUserNotificationCollectionHttpRequestOptions,
  NhUserNotificationOverview,
  NhUserNotificationState
} from '../models/user-notification.models';
import {NhAppService} from "./nh-app.service";
import {CollectionHttpResponse} from "../models/http.models";

@Injectable({
  providedIn: 'root',
})
export class NhUserNotificationService implements OnDestroy {
  protected readonly baseUrl: string;
  protected authService: NhAuthService = inject(NhAuthService);
  protected moduleConfig: NhCommonModuleConfig = inject(NhCommonModuleConfig);
  protected apiService: NhApiService = inject(NhApiService);

  protected runningSubject = new BehaviorSubject<boolean>(false);
  public readonly isRunning$: Observable<boolean> = this.runningSubject.asObservable();

  private task$: Observable<void>|undefined;
  private taskSub: Subscription|undefined;
  private runningConsumers = 0;

  private userNotificationState: NhUserNotificationState = new NhUserNotificationState();
  private readonly userNotificationStateSubject = new BehaviorSubject<NhUserNotificationState>(this.userNotificationState);
  public readonly userNotificationState$ = this.userNotificationStateSubject.asObservable();

  public get state(): NhUserNotificationState {
    return this.userNotificationState ?? new NhUserNotificationState();
  }

  get urlSuffix(): string {
    let suffix = this.moduleConfig.userNotification.urlSuffix;
    if(!suffix.startsWith('/')) {
      suffix = '/' + suffix;
    }

    return suffix;
  }

  public isRunning(): boolean {
    return this.runningSubject.value;
  }

  ngOnDestroy(): void {
    this.taskSub?.unsubscribe();
    this.runningSubject.complete();
    this.userNotificationStateSubject.complete();
  }

  protected constructor(
    private appService: NhAppService
  ) {
    if(!this.appService.isPlatformServer()) {
      this.task$ = this.runningSubject.pipe(
        switchMap(isRunning =>
          isRunning
            ? timer(0, this.moduleConfig.userNotification.pollingInterval).pipe(
              exhaustMap(() => from(this.doWork()))
            )
            : EMPTY
        )
      );

      this.taskSub = this.task$!.subscribe();
    }

    this.baseUrl = this.moduleConfig.apiBaseUrl + this.urlSuffix;
  }

  start(): void {
    this.runningConsumers++;
    if(this.runningConsumers === 1) {
      this.runningSubject.next(true);
    }
  }

  stop(): void {
    if(this.runningConsumers === 0) {
      return;
    }

    this.runningConsumers--;
    if(this.runningConsumers === 0) {
      this.runningSubject.next(false);
    }
  }

  protected async doWork() {
    if (!this.isRunning()) {
      return;
    }

    await this.loadState();

    this.userNotificationStateSubject.next(this.userNotificationState);
  }

  public async loadState() {
    this.userNotificationState ??= new NhUserNotificationState();

    try {
      this.userNotificationState.overview = await this.getOverview().lastValueFrom();
    }catch (error) {
      console.error('Error fetching user notification overview:', error);
    }
  }

  protected getOverview(): Observable<NhUserNotificationOverview> {
    return this.apiService.get<NhUserNotificationOverview>(`${this.baseUrl}/overview`);
  }

  public getNotifications(requestOptions?: NhUserNotificationCollectionHttpRequestOptions): Observable<CollectionHttpResponse<NhUserNotification>> {
    requestOptions ??= new NhUserNotificationCollectionHttpRequestOptions();
    return this.apiService.getCollection<NhUserNotification>(`${this.baseUrl}`, requestOptions);
  }

  public markAsRead(id: string): Observable<unknown> {
    return this.apiService.put<unknown>(`${this.baseUrl}/${id}/MarkAsRead`, {});
  }

  public markAllAsRead(): Observable<unknown> {
    return this.apiService.put<unknown>(`${this.baseUrl}/MarkAllAsRead`, {});
  }

  public archive(id: string): Observable<unknown> {
    return this.apiService.put<unknown>(`${this.baseUrl}/${id}/archive`, {});
  }

  public archiveAll(): Observable<unknown> {
    return this.apiService.put<unknown>(`${this.baseUrl}/ArchiveAll`, {});
  }
}
