import {
  AdminAlertDelivery,
  AdminAlertKind,
  AdminAlertKindChoices,
  AdminAlertKindNumbers,
  AdminAlertKinds,
  AlertUrgency,
  NH_ASSISTANT_ALERT_URGENCIES,
  RunAlertKind
} from '@newheap/platform-ai-chat';
import { NhAssistantMockResult, mockFailure as failure } from './nh-assistant-mock-admin-shared';

const dayMs = 24 * 60 * 60 * 1000;
const maxPeriodMs = 366 * dayMs;
const maxDigestTimes = 4;
const maxReason = 500;
const timeOfDay = /^([01]\d|2[0-3]):[0-5]\d$/;

const everything: AdminAlertKindChoices = { switchOff: true, digestTimes: true, workdaysOnly: true, immediatelyFrom: true, quietHours: true };
const nothing: AdminAlertKindChoices = { switchOff: false, digestTimes: false, workdaysOnly: false, immediatelyFrom: false, quietHours: false };

/**
 * The administration of alert kinds of the mock API (FB87 phase 2): `GET admin/alert-kinds` lists the
 * scenario's kinds (`admin.alertKinds`, an example without them) with their numbers, and
 * `PUT admin/alert-kinds/{id}` changes one kind's settings as the server does: within what its code lets
 * an administrator change, with the next version, and refused with the server's codes. A delivery equal
 * to the code's is stored as the code's. The numbers stay as the scenario gives them.
 */
export class NhAssistantMockAlertKinds {
  private readonly kinds = new Map<string, AdminAlertKind>();

  constructor(kinds: AdminAlertKind[] | undefined, private readonly now: () => string) {
    for (const kind of kinds ?? exampleKinds()) {
      this.kinds.set(kind.id, structuredClone(kind));
    }
  }

  /**
   * Every kind as the run catalog lists it (FB93), ordered by id: what the run settings of an agent created
   * in the administration may list. A kind in `modelKindIds` has a template for an agent's model.
   */
  runCatalogKinds(modelKindIds: ReadonlySet<string>): RunAlertKind[] {
    return [...this.kinds.values()]
      .sort((left, right) => left.id < right.id ? -1 : left.id > right.id ? 1 : 0)
      .map(kind => ({ id: kind.id, displayNameKey: kind.displayNameKey, urgency: kind.urgency, raisedByModel: modelKindIds.has(kind.id) }));
  }

  /** Answers `admin/alert-kinds` and `admin/alert-kinds/{id}`; `segments` start after `admin`. */
  handle(method: string, segments: string[], body: unknown, query: URLSearchParams): NhAssistantMockResult {
    if (segments.length === 1) {
      return method === 'GET' ? this.list(query) : { status: 405 };
    }
    if (segments.length === 2) {
      return method === 'PUT' ? this.change(segments[1], body) : { status: 405 };
    }

    return failure('assistant-not-found');
  }

  private list(query: URLSearchParams): NhAssistantMockResult {
    const to = query.get('to') ?? this.now();
    const from = query.get('from') ?? new Date(Date.parse(to) - 30 * dayMs).toISOString();
    const span = Date.parse(to) - Date.parse(from);
    if (Number.isNaN(span) || span <= 0 || span > maxPeriodMs) {
      return failure('assistant-message-invalid');
    }

    const body: AdminAlertKinds = {
      from,
      to,
      kinds: [...this.kinds.values()]
        .sort((left, right) => left.id < right.id ? -1 : left.id > right.id ? 1 : 0)
        .map(kind => structuredClone(kind))
    };
    return { status: 200, body };
  }

  private change(kindId: string, body: unknown): NhAssistantMockResult {
    const kind = this.kinds.get(kindId);
    if (!kind) {
      return failure('assistant-alert-kind-unknown');
    }

    const request = (body ?? {}) as { enabled?: unknown; delivery?: unknown; expectedVersion?: unknown; reason?: unknown };
    const reason = typeof request.reason === 'string' && request.reason.trim().length > 0 ? request.reason.trim() : null;
    const fieldErrors: Record<string, string[]> = {};
    if (typeof request.enabled !== 'boolean') {
      fieldErrors['enabled'] = ['required'];
    }
    if (request.expectedVersion !== undefined && request.expectedVersion !== null &&
      (typeof request.expectedVersion !== 'number' || request.expectedVersion < 0)) {
      fieldErrors['expectedVersion'] = ['invalid'];
    }
    if (reason !== null && reason.length > maxReason) {
      fieldErrors['reason'] = ['too-long'];
    }
    if (Object.keys(fieldErrors).length > 0) {
      return failure('assistant-validation', fieldErrors);
    }
    if (request.enabled === false && !kind.administratorMayChange.switchOff) {
      return failure('assistant-alert-kind-switch-off-not-allowed');
    }

    const read = readDelivery(request.delivery);
    if ('invalid' in read) {
      return failure('assistant-alert-kind-delivery-invalid', read.invalid);
    }

    let wanted = read.delivery;
    if (wanted) {
      const notAllowed = outsideBounds(kind, wanted);
      if (Object.keys(notAllowed).length > 0) {
        return failure('assistant-alert-kind-delivery-not-allowed', notAllowed);
      }
      if (sameDelivery(wanted, kind.codeDelivery)) {
        wanted = null;
      }
    }
    if (((request.expectedVersion as number | null | undefined) ?? 0) !== kind.settings.version) {
      return failure('assistant-version-conflict');
    }

    const enabled = request.enabled as boolean;
    const unchanged = kind.settings.enabled === enabled && sameStored(kind.settings.delivery, wanted);
    if (!unchanged) {
      kind.settings = {
        enabled,
        delivery: wanted,
        version: kind.settings.version + 1,
        changedBy: 'current-user',
        changedAt: this.now(),
        reason
      };
      kind.enabled = enabled || !kind.administratorMayChange.switchOff;
      kind.delivery = structuredClone(wanted ?? kind.codeDelivery);
    }

    return { status: 200, body: { ...structuredClone(kind), numbers: null, shadow: [] } };
  }
}

/** Reads the delivery of a change as the server does; `null` for the code's. */
function readDelivery(value: unknown): { delivery: AdminAlertDelivery | null } | { invalid: Record<string, string[]> } {
  if (value === undefined || value === null) {
    return { delivery: null };
  }

  const input = value as Partial<Record<keyof AdminAlertDelivery, unknown>>;
  const invalid: Record<string, string[]> = {};
  const mode = input.mode;
  if (mode !== 'immediately' && mode !== 'daily-digest') {
    invalid['delivery.mode'] = [mode === undefined || mode === null ? 'required' : 'invalid'];
  }

  const given = input.digestTimes ?? [];
  const times: unknown[] = Array.isArray(given) ? given : [];
  const validTimes = Array.isArray(given)
    && times.every(time => typeof time === 'string' && timeOfDay.test(time))
    && new Set(times).size === times.length;
  if (!validTimes) {
    invalid['delivery.digestTimes'] = ['invalid'];
  }

  const from = input.immediatelyFrom ?? null;
  if (from !== null && !NH_ASSISTANT_ALERT_URGENCIES.includes(from as AlertUrgency)) {
    invalid['delivery.immediatelyFrom'] = ['invalid'];
  }

  const quiet = input.quietHours as { start?: unknown; end?: unknown } | null | undefined;
  if (quiet !== undefined && quiet !== null &&
    (typeof quiet.start !== 'string' || typeof quiet.end !== 'string' ||
      !timeOfDay.test(quiet.start) || !timeOfDay.test(quiet.end) || quiet.start === quiet.end)) {
    invalid['delivery.quietHours'] = ['invalid'];
  }

  if (mode === 'daily-digest' && (times.length < 1 || times.length > maxDigestTimes)) {
    invalid['delivery.digestTimes'] = ['invalid'];
  }
  if (mode === 'immediately') {
    if (times.length > 0) {
      invalid['delivery.digestTimes'] = ['invalid'];
    }
    if (input.workdaysOnly === true) {
      invalid['delivery.workdaysOnly'] = ['invalid'];
    }
    if (from !== null) {
      invalid['delivery.immediatelyFrom'] = ['invalid'];
    }
  }
  if (Object.keys(invalid).length > 0) {
    return { invalid };
  }

  const quietHours = quiet ? { start: quiet.start as string, end: quiet.end as string } : null;
  return {
    delivery: mode === 'daily-digest'
      ? { mode, digestTimes: [...(times as string[])].sort(), workdaysOnly: input.workdaysOnly === true, immediatelyFrom: from as AlertUrgency | null, quietHours }
      : { mode: 'immediately', digestTimes: [], workdaysOnly: false, immediatelyFrom: null, quietHours }
  };
}

/** The parts of `wanted` that differ from the code's delivery where the code does not let an administrator change them. */
function outsideBounds(kind: AdminAlertKind, wanted: AdminAlertDelivery): Record<string, string[]> {
  const allowed = kind.administratorMayChange;
  const code = kind.codeDelivery;
  const errors: Record<string, string[]> = {};
  if (!allowed.digestTimes && (wanted.mode !== code.mode || wanted.digestTimes.join(',') !== code.digestTimes.join(','))) {
    errors['delivery.digestTimes'] = ['not-allowed'];
  }
  if (!allowed.quietHours && !sameQuietHours(wanted, code)) {
    errors['delivery.quietHours'] = ['not-allowed'];
  }
  if (wanted.mode === 'daily-digest') {
    if (!allowed.workdaysOnly && wanted.workdaysOnly !== code.workdaysOnly) {
      errors['delivery.workdaysOnly'] = ['not-allowed'];
    }
    if (!allowed.immediatelyFrom && wanted.immediatelyFrom !== code.immediatelyFrom) {
      errors['delivery.immediatelyFrom'] = ['not-allowed'];
    }
  }

  return errors;
}

function sameQuietHours(left: AdminAlertDelivery, right: AdminAlertDelivery): boolean {
  return (left.quietHours?.start ?? null) === (right.quietHours?.start ?? null)
    && (left.quietHours?.end ?? null) === (right.quietHours?.end ?? null);
}

function sameDelivery(left: AdminAlertDelivery, right: AdminAlertDelivery): boolean {
  return left.mode === right.mode
    && left.digestTimes.join(',') === right.digestTimes.join(',')
    && left.workdaysOnly === right.workdaysOnly
    && left.immediatelyFrom === right.immediatelyFrom
    && sameQuietHours(left, right);
}

function sameStored(left: AdminAlertDelivery | null, right: AdminAlertDelivery | null): boolean {
  return left === null || right === null ? left === right : sameDelivery(left, right);
}

function numbers(raised: number, delivered: number, read: number, dismissed: number, resolvedBeforeReading: number, withheld: number, muted: number): AdminAlertKindNumbers {
  return { raised, delivered, read, dismissed, resolvedBeforeReading, withheld, muted };
}

function kind(
  id: string,
  urgency: AlertUrgency,
  required: boolean,
  allowed: AdminAlertKindChoices,
  codeDelivery: AdminAlertDelivery,
  kindNumbers: AdminAlertKindNumbers,
  shadow: AdminAlertKind['shadow'] = []
): AdminAlertKind {
  return {
    id,
    displayNameKey: `nh-assistant.alert-kinds.${id}.name`,
    urgency,
    required,
    administratorMayChange: required ? { ...allowed, switchOff: false } : allowed,
    codeDelivery,
    settings: { enabled: true, delivery: null, version: 0, changedBy: null, changedAt: null, reason: null },
    enabled: true,
    delivery: structuredClone(codeDelivery),
    numbers: kindNumbers,
    shadow
  };
}

/**
 * The example of a scenario without alert kinds: a task due today in a morning digest on working days
 * that urgent alerts skip, with a deadline agent in shadow; an overdue invoice that is required and goes
 * out at once; and a weekly report reminder whose code lets an administrator change nothing.
 */
function exampleKinds(): AdminAlertKind[] {
  const digest: AdminAlertDelivery = { mode: 'daily-digest', digestTimes: ['07:30'], workdaysOnly: true, immediatelyFrom: 'urgent', quietHours: null };
  const immediately: AdminAlertDelivery = { mode: 'immediately', digestTimes: [], workdaysOnly: false, immediatelyFrom: null, quietHours: null };
  return [
    kind('invoice-overdue', 'urgent', true, everything, immediately, numbers(4, 4, 3, 1, 0, 0, 0)),
    kind('report-reminder', 'info', false, nothing, { ...digest, immediatelyFrom: null, workdaysOnly: false, digestTimes: ['16:00'] }, numbers(12, 9, 4, 6, 0, 3, 3)),
    kind('task-due-today', 'warning', false, everything, digest, numbers(48, 41, 30, 12, 9, 7, 5), [
      { agentId: 'deadline-watch-shadow', agentDisplayName: null, wouldHaveSent: 17 }
    ])
  ];
}
