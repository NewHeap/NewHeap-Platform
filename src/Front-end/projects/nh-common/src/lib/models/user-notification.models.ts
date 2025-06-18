import {CollectionHttpRequestOptions} from "./http.models";

export class NhUserNotificationCollectionHttpRequestOptions extends CollectionHttpRequestOptions {

  public constructor(init?: Partial<NhUserNotificationCollectionHttpRequestOptions>) {
    super(init);
    Object.assign(this, init);
  }
}
export class NhUserNotificationState {
  overview: NhUserNotificationOverview = new NhUserNotificationOverview();

  public constructor(init?: Partial<NhUserNotificationState>) {
    Object.assign(this, init);
  }
}

export class NhUserNotificationOverview {
  totalCount: number = 0;
  unreadCount: number = 0;
  lastNotificationDate: string = '';

  public constructor(init?: Partial<NhUserNotificationOverview>) {
    Object.assign(this, init);
  }
}

export class NhUserNotification {
  id: string = '';
  creationDateTime: string = '';
  lastModifiedDateTime: string = '';
  messages: NhUserNotificationMessage[] = [];
  lastTitle: string = '';
  lastMessage: string = '';
  isLastRead: boolean = false;
  data: NhUserNotificationData = new NhUserNotificationData();

  public constructor(init?: Partial<NhUserNotification>) {
    Object.assign(this, init);
  }
}

export class NhUserNotificationMessage {
  id: string = '';
  creationDateTime: string = '';
  lastModifiedDateTime: string = '';
  title: string = '';
  message: string = '';
  userNotificationId: string = '';

  public constructor(init?: Partial<NhUserNotificationMessage>) {
    Object.assign(this, init);
  }
}

export class NhUserNotificationData {
  url: string = '';
  urlInNewTab: boolean = false;

  public constructor(init?: Partial<NhUserNotificationData>) {
    Object.assign(this, init);
  }
}
