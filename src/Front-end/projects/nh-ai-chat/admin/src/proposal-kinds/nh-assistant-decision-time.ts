/** A duration as the administration writes it: a translation key with its parameters. */
export interface NhAssistantDurationText {
  key: string;
  params: Record<string, number>;
}

const minute = 60;
const hour = 60 * minute;
const day = 24 * hour;

/**
 * Writes whole seconds in the two largest units that matter, rounded down: seconds below a minute,
 * minutes below an hour, hours and minutes below a day, otherwise days and hours.
 */
export function nhAssistantDurationText(seconds: number): NhAssistantDurationText {
  const value = Math.max(0, Math.floor(seconds));
  if (value < minute) {
    return { key: 'nh-assistant.admin.proposal-kinds.duration.seconds', params: { count: value } };
  }
  if (value < hour) {
    return { key: 'nh-assistant.admin.proposal-kinds.duration.minutes', params: { count: Math.floor(value / minute) } };
  }
  if (value < day) {
    return {
      key: 'nh-assistant.admin.proposal-kinds.duration.hours',
      params: { hours: Math.floor(value / hour), minutes: Math.floor((value % hour) / minute) }
    };
  }

  return {
    key: 'nh-assistant.admin.proposal-kinds.duration.days',
    params: { days: Math.floor(value / day), hours: Math.floor((value % day) / hour) }
  };
}
