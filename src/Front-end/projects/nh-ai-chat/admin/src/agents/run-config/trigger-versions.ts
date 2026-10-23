import { DataTrigger, DataTriggerVersion, TriggerConflict } from '@newheap/platform-ai-chat';

/** A part of a trigger's definition that a candidate changes. */
export type TriggerField = 'source' | 'event' | 'filters' | 'debounce' | 'cron' | 'time-zone' | 'subject-set' | 'message' | 'skill' | 'conflict';

/**
 * One difference between the live version of a trigger and its candidate, with the values as stored:
 * a skill id, a conflict or a source the page names in the reader's language, `null` for none.
 */
export interface TriggerChange {
  field: TriggerField;
  live: string | null;
  candidate: string | null;
}

/** The definition of a trigger version, as both a trigger and an entry of its history carry it. */
type TriggerDefinition = Pick<DataTrigger, 'source' | 'event' | 'cron' | 'timeZone' | 'subjectSet' | 'message' | 'skillId' | 'conflict' | 'debounceSeconds'> & {
  filters?: readonly string[] | null;
};

/** The translation key of a field's label in the trigger form. */
export const triggerFieldKeys: Record<TriggerField, string> = {
  source: 'nh-assistant.admin.run-config.triggers.source',
  event: 'nh-assistant.admin.run-config.triggers.event',
  filters: 'nh-assistant.admin.run-config.triggers.filters.title',
  debounce: 'nh-assistant.admin.run-config.triggers.debounce',
  cron: 'nh-assistant.admin.run-config.triggers.cron',
  'time-zone': 'nh-assistant.admin.run-config.triggers.time-zone',
  'subject-set': 'nh-assistant.admin.run-config.triggers.subject-set',
  message: 'nh-assistant.admin.run-config.triggers.message',
  skill: 'nh-assistant.admin.run-config.triggers.skill',
  conflict: 'nh-assistant.admin.run-config.triggers.conflict'
};

/**
 * What the candidate changes against the live version, in the order of the trigger form: the source,
 * then what an event or a schedule says, then the skill and the conflict. A field of the other source
 * is left out, so an event trigger shows no schedule fields.
 */
export function triggerChanges(live: TriggerDefinition, candidate: TriggerDefinition): TriggerChange[] {
  const event = live.source === 'event' || candidate.source === 'event';
  const schedule = live.source === 'schedule' || candidate.source === 'schedule';
  const values: [TriggerField, string | null, string | null, boolean][] = [
    ['source', live.source, candidate.source, true],
    ['event', live.event, candidate.event, event],
    ['filters', joined(live.filters), joined(candidate.filters), event],
    ['debounce', debounceOf(live), debounceOf(candidate), event],
    ['cron', live.cron, candidate.cron, schedule],
    ['time-zone', live.timeZone, candidate.timeZone, schedule],
    ['subject-set', live.subjectSet, candidate.subjectSet, schedule],
    ['message', live.message, candidate.message, schedule],
    ['skill', live.skillId, candidate.skillId, true],
    ['conflict', live.conflict, candidate.conflict, true]
  ];
  return values
    .filter(([, before, after, shown]) => shown && (before ?? null) !== (after ?? null))
    .map(([field, before, after]) => ({ field, live: before ?? null, candidate: after ?? null }));
}

/** The short definition of a version in the history: what starts it and when. */
export function versionSummary(version: DataTriggerVersion): string {
  if (version.source === 'schedule') {
    return [version.cron, version.timeZone ?? 'UTC', version.subjectSet].filter(part => !!part).join(' ');
  }

  return [version.event, ...(version.filters.length > 0 ? [`(${version.filters.join(', ')})`] : [])].filter(part => !!part).join(' ');
}

/** Whether a stored conflict is one the page names. */
export function isConflict(value: string | null): value is TriggerConflict {
  return value === 'coalesce' || value === 'queue' || value === 'supersede' || value === 'ignore';
}

function joined(filters: readonly string[] | null | undefined): string | null {
  return filters && filters.length > 0 ? filters.join(', ') : null;
}

function debounceOf(definition: TriggerDefinition): string | null {
  return definition.source === 'event' ? String(definition.debounceSeconds) : null;
}
