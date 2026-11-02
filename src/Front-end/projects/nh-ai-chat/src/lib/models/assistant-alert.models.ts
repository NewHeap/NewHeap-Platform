/**
 * Alerts (FB87): warnings about an object that an agent's rules or the application raised for a person,
 * with nothing to approve. The models of `GET alerts`, `GET alerts/summary`, `POST alerts/{id}/read` and
 * `POST alerts/{id}/dismiss`, and of a person's choices per kind in `GET` and `PUT notifications`.
 */

/** How much an alert asks for a person's attention, lowest first. */
export type AlertUrgency = 'info' | 'warning' | 'urgent';

/** The urgencies in their order, lowest first. */
export const NH_ASSISTANT_ALERT_URGENCIES: readonly AlertUrgency[] = ['info', 'warning', 'urgent'];

/**
 * How a person sees one alert: `unread` or `read` while it is open, `resolved` or `expired` once it
 * closed, and `dismissed` once they dismissed it. A dismissal wins, then a closed alert.
 */
export type AlertStatus = 'unread' | 'read' | 'resolved' | 'expired' | 'dismissed';

/**
 * The `status` filter of `GET alerts`: one state, `open` for unread and read, or `closed` for resolved
 * and expired. Without one the list shows every alert the person did not dismiss.
 */
export type AlertListStatus = AlertStatus | 'open' | 'closed';

/** The subject an alert's subject belongs to, which the grouped list groups by. */
export interface AlertParent {
  subjectType: string;
  subjectId: string;
  displayName: string | null;
  /** The name of the parent's type in the caller's language, when the application named it. */
  subjectTypeName?: string | null;
  /** Where the application shows the parent; `null` without a link template. */
  subjectLink?: string | null;
}

/** One alert in the caller's list, with its title and text in the caller's language. */
export interface Alert {
  /** The alert, as `markAlertRead` and `dismissAlert` name it. */
  id: string;
  /** The caller's own delivery of the alert. */
  deliveryId: string;
  kindId: string;
  /** The translation key of the kind's name. */
  displayNameKey: string;
  urgency: AlertUrgency;
  /** The title; `null` when the kind's title cannot be written, for which the list shows a neutral text. */
  title: string | null;
  /** The text; `null` for a kind with a title only. */
  text: string | null;
  subjectType: string;
  subjectId: string;
  subjectDisplayName: string | null;
  /** The name of the subject's type in the caller's language, when the application named it. */
  subjectTypeName?: string | null;
  /** Where the application shows the subject; `null` without a link template. */
  subjectLink?: string | null;
  parent?: AlertParent | null;
  status: AlertStatus;
  revision?: number;
  /** When the alert was first raised. */
  createdAt: string;
  /** When it was last raised again; the list shows the most recent first. */
  updatedAt: string;
  expiresAt?: string | null;
  closedAt?: string | null;
  isRead?: boolean;
  readAt?: string | null;
  isDismissed?: boolean;
  dismissedAt?: string | null;
  isResolved?: boolean;
  isExpired?: boolean;
  /**
   * The alert reached none of the person's channels, for example because they muted its kind; it is in
   * the list all the same, and a muted one does not count on the badge.
   */
  isWithheld?: boolean;
  /** Why: `muted`, `limit-reached`, `kind-unknown` or `text-invalid`; `null` when the alert was not withheld. */
  withheldReason?: string | null;
}

/** One subject within a group of the grouped alert list; its name and link are those of its items. */
export interface AlertSubjectGroup {
  subjectType: string;
  subjectId: string;
  count: number;
  total: number;
  continuesFromPreviousPage: boolean;
  continuesOnNextPage: boolean;
}

/**
 * One group on a page of the grouped alert list: the parent the alerts' subjects belong to, or the
 * subject of alerts without one. Groups come in the order of their most recent alert.
 */
export interface AlertGroup {
  subjectType: string;
  subjectId: string;
  displayName: string | null;
  subjectTypeName?: string | null;
  subjectLink?: string | null;
  /** How many of the page's items belong to the group. */
  count: number;
  /** How many alerts of the list belong to the group. */
  total: number;
  continuesFromPreviousPage: boolean;
  continuesOnNextPage: boolean;
  /** The group's subjects on this page in order, the group's own subject first. */
  subjects: AlertSubjectGroup[];
}

/** A page of the caller's alerts (`GET alerts`). */
export interface AlertList {
  items: Alert[];
  /** How many alerts the list shows with the filter; the server reads the newest 500. */
  total: number;
  status?: AlertListStatus | null;
  /** With `grouped`, the groups of the page; the items are contiguous per group. `null` for the flat list. */
  groups?: AlertGroup[] | null;
}

/** The filter and paging of `GET alerts`. */
export interface AlertListFilter {
  status?: AlertListStatus | null;
  /** One or more kinds, at most 20. */
  kind?: string | readonly string[] | null;
  subjectType?: string | null;
  /** Needs `subjectType`. */
  subjectId?: string | null;
  urgency?: AlertUrgency | readonly AlertUrgency[] | null;
  grouped?: boolean;
  page?: number;
  /** 1 to 100; 20 by default. */
  itemsPerPage?: number;
}

/** The filter of `GET alerts/summary`, for example one object's badge. */
export interface AlertSummaryFilter {
  kind?: string | readonly string[] | null;
  subjectType?: string | null;
  subjectId?: string | null;
}

/** A count per urgency. */
export interface AlertUrgencyCounts {
  info: number;
  warning: number;
  urgent: number;
}

/** The counts of a badge (`GET alerts/summary`): the caller's unread open alerts. */
export interface AlertSummary {
  unread: number;
  unreadByUrgency: AlertUrgencyCounts;
}

/** What the host routes when a person opens an alert's subject (`openSubject` of `nh-assistant-alerts`). */
export interface AlertSubjectTarget {
  subjectType: string;
  subjectId: string;
  /** Where the application shows the subject, when its type has a link template. */
  link: string | null;
  /** The alert the person opened it from. */
  alert: Alert;
}

/** A channel that delivers alerts of a kind. */
export type NotificationAlertChannel = 'push' | 'in-app';

/** Whether each channel delivers a kind's alerts. */
export interface NotificationAlertChannels {
  push: boolean;
  inApp: boolean;
}

/**
 * One kind of alert in a person's notification choices (`GET notifications`), with what the kind lets
 * the person change and the person's choices. Times are `HH:mm` in the person's time zone.
 */
export interface NotificationAlertKind {
  id: string;
  /** The translation key of the kind's name. */
  displayNameKey: string;
  urgency: AlertUrgency;
  /** Nobody mutes the kind. */
  required: boolean;
  mayMute: boolean;
  mayChangeDigestTime: boolean;
  mayChooseChannels: boolean;
  muted: boolean;
  /** The kind's daily digest time; `null` when the kind delivers immediately. */
  defaultDigestTime: string | null;
  /** The person's own digest time; `null` for the default. */
  digestTime: string | null;
  /** The kind's digest goes out on working days only. */
  workdaysOnly: boolean;
  /** The channels that deliver the kind for the person, as they apply. */
  channels: NotificationAlertChannels;
  /** The channels the person may choose from. */
  availableChannels: NotificationAlertChannel[];
}

/**
 * A person's change of one kind in `PUT notifications`: only what is named changes. `digestTime` `null`
 * returns to the kind's default. A change the kind does not allow is refused with `400` and its code.
 */
export interface NotificationAlertKindChange {
  id: string;
  muted?: boolean;
  digestTime?: string | null;
  channels?: Partial<NotificationAlertChannels>;
}
