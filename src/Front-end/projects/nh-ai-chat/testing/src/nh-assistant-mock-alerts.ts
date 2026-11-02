import {
  Alert,
  AlertGroup,
  AlertStatus,
  AlertSubjectGroup,
  AlertUrgency,
  NH_ASSISTANT_ALERT_URGENCIES,
  NotificationAlertKind,
  NotificationAlertKindChange
} from '@newheap/platform-ai-chat';
import { NhAssistantMockResult, mockFailure } from './nh-assistant-mock-admin-shared';
import { NhAssistantMockAlert, NhAssistantMockAlertScenario } from './nh-assistant-mock.models';

/** The time zone the mock reports without one in the scenario. */
const defaultTimeZone = 'Europe/Amsterdam';

/** The most kinds one alert filter names, as on the server. */
const maxKindFilters = 20;

const listStatuses = new Set(['unread', 'read', 'resolved', 'expired', 'dismissed', 'open', 'closed']);
const timeOfDay = /^([01]\d|2[0-3]):[0-5]\d$/;

/**
 * The person's alerts and their choices per kind, as the server keeps them (FB87): `GET alerts` with its
 * filters, paging and groups per parent, `GET alerts/summary`, `POST alerts/{id}/read` and
 * `POST alerts/{id}/dismiss`, and the alert kinds and time zone of `GET` and `PUT notifications`. A
 * kind the person does not have, or a change the kind does not allow, is refused with `400` and the
 * server's code, such as `assistant-notification-mute-not-allowed`.
 */
export class NhAssistantMockAlerts {
  private readonly alerts = new Map<string, Alert>();
  private readonly kinds = new Map<string, NotificationAlertKind>();
  private readonly timeZone: string | null;

  constructor(scenario: NhAssistantMockAlertScenario | undefined, private readonly now: () => string) {
    for (const alert of scenario?.items ?? []) {
      const stored = normalize(alert, now());
      this.alerts.set(stored.id, stored);
    }
    for (const kind of scenario?.kinds ?? []) {
      this.kinds.set(kind.id, structuredClone(kind));
    }
    this.timeZone = scenario ? scenario.timeZone ?? defaultTimeZone : null;
  }

  /**
   * Raises an alert for the person while the mock runs, as a rule or the application's code would, or
   * updates the one with the same id; `nh-assistant-alerts` and the badge show it on their next read.
   */
  raise(alert: NhAssistantMockAlert): Alert {
    const stored = normalize({ ...alert, updatedAt: alert.updatedAt ?? this.now() }, this.now());
    this.alerts.set(stored.id, stored);
    return structuredClone(stored);
  }

  /** Resolves an alert while the mock runs; the list shows it as resolved. */
  resolve(alertId: string): void {
    const alert = this.alerts.get(alertId);
    if (alert && !alert.closedAt) {
      this.alerts.set(alertId, withStatus({ ...alert, isResolved: true, closedAt: this.now() }));
    }
  }

  /** Replaces or adds a kind of the person's choices, as when the application changes its kinds in code. */
  setKind(kind: NotificationAlertKind): void {
    this.kinds.set(kind.id, structuredClone(kind));
  }

  /** Answers `alerts`, `alerts/summary` and `alerts/{id}/read|dismiss`, or `null` for another path. */
  handle(method: string, segments: string[], query: URLSearchParams): NhAssistantMockResult | null {
    if (segments[0] !== 'alerts') {
      return null;
    }
    if (method === 'GET' && segments.length === 1) {
      return this.list(query);
    }
    if (method === 'GET' && segments.length === 2 && segments[1] === 'summary') {
      return this.summary(query);
    }
    if (method === 'POST' && segments.length === 3 && (segments[2] === 'read' || segments[2] === 'dismiss')) {
      return this.change(segments[1], segments[2]);
    }

    return { status: 404 };
  }

  /** The alert kinds and time zone of `GET notifications`; nothing for a scenario without alerts, as an older server. */
  settings(): { timeZone?: string | null; alertKinds?: NotificationAlertKind[] } {
    if (this.timeZone === null) {
      return {};
    }

    return { timeZone: this.timeZone, alertKinds: [...this.kinds.values()].map(kind => structuredClone(kind)) };
  }

  /**
   * Applies the changes per kind of `PUT notifications`, all or none: a kind the person does not have,
   * a change the kind does not allow and a time that is no `HH:mm` refuse the whole request.
   */
  update(changes: unknown): NhAssistantMockResult | null {
    if (changes === undefined) {
      return null;
    }
    if (!Array.isArray(changes)) {
      return mockFailure('assistant-validation', { alertKinds: ['invalid'] });
    }

    const updated = new Map<string, NotificationAlertKind>();
    for (const change of changes as NotificationAlertKindChange[]) {
      const kind = this.kinds.get(change?.id);
      if (!kind) {
        return mockFailure('assistant-notification-kind-unknown');
      }

      const next = applied(updated.get(kind.id) ?? kind, change);
      if (typeof next === 'string') {
        return mockFailure(next);
      }

      updated.set(next.id, next);
    }
    for (const [id, kind] of updated) {
      this.kinds.set(id, kind);
    }

    return null;
  }

  private list(query: URLSearchParams): NhAssistantMockResult {
    const page = Number(query.get('page') ?? 1);
    const size = Number(query.get('itemsPerPage') ?? 20);
    const status = query.get('status');
    const filter = this.filterOf(query);
    if (!filter || !Number.isInteger(page) || page < 1 || !Number.isInteger(size) || size < 1 || size > 100
      || (status !== null && status !== '' && !listStatuses.has(status))) {
      return mockFailure('assistant-alert-filter-invalid');
    }

    const grouped = query.get('grouped') === 'true';
    const listed = this.select(filter, status || null);
    const ordered = grouped ? groupedOrder(listed) : listed;
    const start = (page - 1) * size;
    const items = ordered.slice(start, start + size);
    return {
      status: 200,
      body: {
        items: items.map(item => structuredClone(item)),
        total: ordered.length,
        status: status || null,
        groups: grouped ? groupsOf(ordered, start, items.length) : null
      }
    };
  }

  private summary(query: URLSearchParams): NhAssistantMockResult {
    const filter = this.filterOf(query);
    if (!filter) {
      return mockFailure('assistant-alert-filter-invalid');
    }

    // A kind the person muted, or an administrator switched off, is listed but never asks for their attention.
    const unread = this.select({ ...filter, urgencies: [] }, 'unread')
      .filter(alert => !(alert.isWithheld && (alert.withheldReason === 'muted' || alert.withheldReason === 'kind-off')));
    const count = (urgency: AlertUrgency) => unread.filter(alert => alert.urgency === urgency).length;
    return {
      status: 200,
      body: { unread: unread.length, unreadByUrgency: { info: count('info'), warning: count('warning'), urgent: count('urgent') } }
    };
  }

  private change(alertId: string, action: 'read' | 'dismiss'): NhAssistantMockResult {
    const alert = this.alerts.get(alertId);
    if (!alert) {
      return mockFailure('assistant-alert-not-found');
    }

    const now = this.now();
    let changed = alert;
    if (action === 'read' && alert.status === 'unread') {
      changed = withStatus({ ...alert, isRead: true, readAt: now, isWithheld: false, withheldReason: null });
    } else if (action === 'dismiss' && alert.status !== 'dismissed') {
      changed = withStatus({ ...alert, isDismissed: true, dismissedAt: now });
    }
    this.alerts.set(alertId, changed);
    return { status: 200, body: structuredClone(changed) };
  }

  private filterOf(query: URLSearchParams): AlertFilter | null {
    const kinds = query.getAll('kind');
    const urgencies = query.getAll('urgency');
    const subjectType = query.get('subjectType');
    const subjectId = query.get('subjectId');
    if (kinds.length > maxKindFilters
      || urgencies.some(urgency => !NH_ASSISTANT_ALERT_URGENCIES.includes(urgency as AlertUrgency))
      || (subjectId !== null && subjectType === null)) {
      return null;
    }

    return { kinds, urgencies: urgencies as AlertUrgency[], subjectType, subjectId };
  }

  /** The alerts a filter and status select, the most recently raised first. */
  private select(filter: AlertFilter, status: string | null): Alert[] {
    return [...this.alerts.values()]
      .map(alert => withStatus(alert))
      .filter(alert => matches(alert.status, status))
      .filter(alert => filter.kinds.length === 0 || filter.kinds.includes(alert.kindId))
      .filter(alert => filter.urgencies.length === 0 || filter.urgencies.includes(alert.urgency))
      .filter(alert => filter.subjectType === null || alert.subjectType === filter.subjectType)
      .filter(alert => filter.subjectId === null || alert.subjectId === filter.subjectId)
      .sort((left, right) => right.updatedAt.localeCompare(left.updatedAt) || left.id.localeCompare(right.id));
  }
}

interface AlertFilter {
  kinds: string[];
  urgencies: AlertUrgency[];
  subjectType: string | null;
  subjectId: string | null;
}

function matches(state: AlertStatus, status: string | null): boolean {
  switch (status) {
    case null:
      return state !== 'dismissed';
    case 'open':
      return state === 'unread' || state === 'read';
    case 'closed':
      return state === 'resolved' || state === 'expired';
    default:
      return state === status;
  }
}

/** A scenario's alert as the server stores it; its `status` sets the person's state. */
function normalize(alert: NhAssistantMockAlert, now: string): Alert {
  const { status, ...fields } = structuredClone(alert);
  return withStatus({
    deliveryId: `${alert.id}-delivery`,
    displayNameKey: `nh-assistant.alert-kinds.${alert.kindId}.name`,
    text: null,
    subjectDisplayName: null,
    createdAt: alert.createdAt ?? alert.updatedAt ?? now,
    updatedAt: now,
    revision: 1,
    ...fields,
    isRead: fields.isRead ?? status === 'read',
    isDismissed: fields.isDismissed ?? status === 'dismissed',
    isResolved: fields.isResolved ?? status === 'resolved',
    isExpired: fields.isExpired ?? status === 'expired',
    status: 'unread'
  });
}

/** The person's state of an alert, by the server's rule: a dismissal wins, then a closed alert. */
function withStatus(alert: Alert): Alert {
  const expired = alert.isExpired === true || (!!alert.expiresAt && Date.parse(alert.expiresAt) <= Date.now());
  const status: AlertStatus = alert.isDismissed
    ? 'dismissed'
    : alert.isResolved
      ? 'resolved'
      : expired
        ? 'expired'
        : alert.isRead ? 'read' : 'unread';
  return {
    ...alert,
    status,
    isExpired: expired,
    isRead: alert.isRead ?? false,
    isDismissed: alert.isDismissed ?? false,
    isResolved: alert.isResolved ?? false,
    isWithheld: alert.isWithheld ?? false,
    withheldReason: alert.isWithheld ? alert.withheldReason ?? null : null
  };
}

function groupOf(alert: Alert): string {
  return alert.parent ? `${alert.parent.subjectType}|${alert.parent.subjectId}` : `${alert.subjectType}|${alert.subjectId}`;
}

function subjectOf(alert: Alert): string {
  return `${alert.subjectType}|${alert.subjectId}`;
}

/** Groups by parent in the order of their most recent alert, a group's own subject first. */
function groupedOrder(alerts: Alert[]): Alert[] {
  const groups = new Map<string, Alert[]>();
  for (const alert of alerts) {
    groups.set(groupOf(alert), [...(groups.get(groupOf(alert)) ?? []), alert]);
  }

  return [...groups.entries()].flatMap(([key, items]) => {
    const subjects = new Map<string, Alert[]>();
    for (const alert of items) {
      subjects.set(subjectOf(alert), [...(subjects.get(subjectOf(alert)) ?? []), alert]);
    }
    return [...subjects.entries()]
      .sort(([left], [right]) => (left === key ? 0 : 1) - (right === key ? 0 : 1))
      .flatMap(([, subject]) => subject);
  });
}

function groupsOf(ordered: Alert[], start: number, count: number): AlertGroup[] {
  const end = start + count;
  const groups: AlertGroup[] = [];
  let index = 0;
  while (index < ordered.length) {
    const key = groupOf(ordered[index]);
    const first = index;
    while (index < ordered.length && groupOf(ordered[index]) === key) {
      index++;
    }

    const onPage = Math.min(index, end) - Math.max(first, start);
    if (onPage <= 0) {
      continue;
    }

    const head = ordered[first];
    const parent = head.parent;
    groups.push({
      subjectType: parent?.subjectType ?? head.subjectType,
      subjectId: parent?.subjectId ?? head.subjectId,
      displayName: parent ? parent.displayName : head.subjectDisplayName,
      subjectTypeName: parent ? parent.subjectTypeName ?? null : head.subjectTypeName ?? null,
      subjectLink: parent ? parent.subjectLink ?? null : head.subjectLink ?? null,
      count: onPage,
      total: index - first,
      continuesFromPreviousPage: first < start,
      continuesOnNextPage: index > end,
      subjects: subjectGroupsOf(ordered, first, index, start, end)
    });
  }

  return groups;
}

function subjectGroupsOf(ordered: Alert[], first: number, last: number, start: number, end: number): AlertSubjectGroup[] {
  const subjects: AlertSubjectGroup[] = [];
  let index = first;
  while (index < last) {
    const key = subjectOf(ordered[index]);
    const subjectFirst = index;
    while (index < last && subjectOf(ordered[index]) === key) {
      index++;
    }

    const onPage = Math.min(index, end) - Math.max(subjectFirst, start);
    if (onPage > 0) {
      subjects.push({
        subjectType: ordered[subjectFirst].subjectType,
        subjectId: ordered[subjectFirst].subjectId,
        count: onPage,
        total: index - subjectFirst,
        continuesFromPreviousPage: subjectFirst < start,
        continuesOnNextPage: index > end
      });
    }
  }

  return subjects;
}

/**
 * A kind with one change applied, or the server's code of what the kind does not allow: muting a kind that
 * may not be muted, a digest time it does not let the person choose or that is no time of day, and channels
 * it does not let the person choose or does not offer.
 */
function applied(kind: NotificationAlertKind, change: NotificationAlertKindChange): NotificationAlertKind | string {
  const next = structuredClone(kind);
  if (change.muted !== undefined) {
    if (!kind.mayMute || kind.required) {
      return 'assistant-notification-mute-not-allowed';
    }
    next.muted = change.muted === true;
  }
  if (change.digestTime !== undefined) {
    if (!kind.mayChangeDigestTime) {
      return 'assistant-notification-digest-time-not-allowed';
    }
    if (change.digestTime !== null && !timeOfDay.test(change.digestTime)) {
      return 'assistant-notification-digest-time-invalid';
    }
    next.digestTime = change.digestTime;
  }
  if (change.channels !== undefined) {
    const push = change.channels.push;
    const inApp = change.channels.inApp;
    if (!kind.mayChooseChannels
      || (push !== undefined && !kind.availableChannels.includes('push'))
      || (inApp !== undefined && !kind.availableChannels.includes('in-app'))) {
      return 'assistant-notification-channels-not-allowed';
    }
    next.channels.push = push === undefined ? next.channels.push : push === true;
    next.channels.inApp = inApp === undefined ? next.channels.inApp : inApp === true;
  }

  return next;
}
