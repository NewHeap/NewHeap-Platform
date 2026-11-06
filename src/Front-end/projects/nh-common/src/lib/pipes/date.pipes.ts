import { DatePipe, isPlatformBrowser } from '@angular/common';
import { ChangeDetectorRef, inject, OnDestroy, Pipe, PipeTransform, PLATFORM_ID } from '@angular/core';
import {NhCommonModuleConfig} from "../models/config.models";
import {NhRelativeTimeRefresh} from "./nh-relative-time-refresh";

@Pipe({
    name: 'nhDate',
    pure: false,
    standalone: false
})
export class NhDatePipe implements PipeTransform {

  constructor(private moduleConfig: NhCommonModuleConfig) {
  }

  transform(value: any, pattern?: string): any {
    pattern ??= this.moduleConfig.defaultDateFormat;
    const datePipe: DatePipe = new DatePipe(this.moduleConfig.language);
    return datePipe.transform(value, pattern);
  }
}

@Pipe({
  name: 'nhDateTime',
  pure: false,
  standalone: false
})
export class NhDateTimePipe implements PipeTransform {

  constructor(private moduleConfig: NhCommonModuleConfig) {
  }

  transform(value: any, pattern?: string): any {
    pattern ??= this.moduleConfig.defaultDateTimeFormat;
    const datePipe: NhDatePipe = new NhDatePipe(this.moduleConfig);
    return datePipe.transform(value, pattern);
  }
}

@Pipe({
  name: 'nhDateUtc',
  pure: false,
  standalone: false
})
export class NhDateUtcPipe implements PipeTransform {

  constructor(private moduleConfig: NhCommonModuleConfig) {
  }

  transform(value: any, pattern: string = 'dd-MM-yyyy HH:mm:ss'): any {
    if(value && !value.toString().endsWith('Z')) {
      value = value + 'Z';
    }
    const datePipe: DatePipe = new DatePipe(this.moduleConfig.language);
    return datePipe.transform(value, pattern);
  }
}

/** A moment for `nhRelativeTime`: a `Date`, epoch milliseconds, or a string that `Date` parses. */
export type NhRelativeTimeInput = Date | number | string | null | undefined;

export interface NhRelativeTimeOptions {
  /** `long` (default) as in "3 minutes ago", `short` as in "3 min. ago", or `narrow`, the shortest form of the language. */
  style?: 'long' | 'short' | 'narrow';
  /**
   * `always` (default) gives a number for every amount, as in "1 day ago". `auto` lets the language
   * use words such as "yesterday" or "next week"; those words then mean a rounded day or week, not
   * calendar days or weeks. Less than half a second always reads as "now".
   */
  numeric?: 'always' | 'auto';
}

type NhRelativeTimeUnit = 'second' | 'minute' | 'hour' | 'day' | 'week' | 'month' | 'year';

interface NhRelativeTimeStep {
  readonly unit: NhRelativeTimeUnit;
  readonly size: number;
  readonly below: number;
}

interface NhRelativeTimeResult {
  readonly text: string;
  readonly refreshAfter: number;
}

const second = 1000;
const minute = 60 * second;
const hour = 60 * minute;
const day = 24 * hour;
const week = 7 * day;
const month = 30 * day;
const year = 365 * day;

/** The units from small to large; a month counts 30 days and a year 365 days. */
const relativeTimeSteps: readonly NhRelativeTimeStep[] = [
  {unit: 'second', size: second, below: minute},
  {unit: 'minute', size: minute, below: hour},
  {unit: 'hour', size: hour, below: day},
  {unit: 'day', size: day, below: week},
  {unit: 'week', size: week, below: month},
  {unit: 'month', size: month, below: year},
  {unit: 'year', size: year, below: Number.POSITIVE_INFINITY}
];

const minimumRefresh = second;
const maximumRefresh = hour;
const relativeTimeFormats = new Map<string, Intl.RelativeTimeFormat>();

function relativeTimestamp(value: NhRelativeTimeInput): number | null {
  let time = Number.NaN;
  if (value instanceof Date) {
    time = value.getTime();
  } else if (typeof value === 'number') {
    time = value;
  } else if (typeof value === 'string' && value.trim().length > 0) {
    time = Date.parse(value);
  }

  return Number.isFinite(time) ? time : null;
}

function relativeTimeFormat(
  language: string,
  style: Intl.RelativeTimeFormatStyle,
  numeric: Intl.RelativeTimeFormatNumeric
): Intl.RelativeTimeFormat {
  const key = `${language}|${style}|${numeric}`;
  let format = relativeTimeFormats.get(key);
  if (!format) {
    try {
      format = new Intl.RelativeTimeFormat(language || 'en', {style, numeric});
    } catch {
      // Not a language tag: fall back to English rather than failing the template.
      format = new Intl.RelativeTimeFormat('en', {style, numeric});
    }

    relativeTimeFormats.set(key, format);
  }

  return format;
}

/** The index of the largest unit that fits the distance. */
function relativeTimeStepIndex(distance: number): number {
  const index = relativeTimeSteps.findIndex(candidate => distance < candidate.below);
  return index < 0 ? relativeTimeSteps.length - 1 : index;
}

/** The nearest whole amount of the largest unit that fits; 59.6 seconds give "1 minute", not "60 seconds". */
function relativeTimeAmount(distance: number): {readonly step: NhRelativeTimeStep; readonly amount: number} {
  const index = relativeTimeStepIndex(distance);
  const step = relativeTimeSteps[index];
  const amount = Math.round(distance / step.size);

  if (amount * step.size >= step.below) {
    const larger = relativeTimeSteps[index + 1];
    return {step: larger, amount: Math.round(distance / larger.size)};
  }

  return {step, amount};
}

/**
 * How long the text stays the same: until the distance reaches the next point where a rounded
 * amount or the unit changes. Bounded so that a sleeping device or a changed clock catches up
 * within an hour, and so that a pipe refreshes at most once a second.
 */
function relativeTimeRefreshAfter(difference: number, distance: number, step: NhRelativeTimeStep, amount: number): number {
  const rawIndex = relativeTimeStepIndex(distance);
  const raw = relativeTimeSteps[rawIndex];
  const rawAmount = Math.round(distance / raw.size);

  let delay: number;
  if (difference <= 0) {
    // In the past the distance grows: a rounded amount goes up halfway to its next whole unit.
    const boundaries = [(rawAmount + 0.5) * raw.size, raw.below, (amount + 0.5) * step.size, step.below];
    delay = Math.min(...boundaries.filter(boundary => boundary > distance)) - distance;
  } else {
    // In the future the distance shrinks: a rounded amount goes down below the half of its whole unit.
    const smallerUnitStart = rawIndex === 0 ? 0 : relativeTimeSteps[rawIndex - 1].below;
    const boundaries = [0, (rawAmount - 0.5) * raw.size, smallerUnitStart, (amount - 0.5) * step.size];
    delay = distance - Math.max(...boundaries.filter(boundary => boundary < distance)) + 1;
  }

  return Math.min(Math.max(delay, minimumRefresh), maximumRefresh);
}

function relativeTime(
  value: NhRelativeTimeInput,
  now: number,
  language: string,
  options?: NhRelativeTimeOptions
): NhRelativeTimeResult | null {
  const time = relativeTimestamp(value);
  if (time === null) {
    return null;
  }

  const difference = time - now;
  const distance = Math.abs(difference);
  const {step, amount} = relativeTimeAmount(distance);
  const style = options?.style ?? 'long';

  let text: string;
  if (amount === 0) {
    text = relativeTimeFormat(language, style, 'auto').format(0, 'second');
  } else {
    const signedAmount = difference < 0 ? -amount : amount;
    text = relativeTimeFormat(language, style, options?.numeric ?? 'always').format(signedAmount, step.unit);
  }

  return {text, refreshAfter: relativeTimeRefreshAfter(difference, distance, step, amount)};
}

/**
 * Formats `value` relative to `now` in `language` with `Intl.RelativeTimeFormat`, as the
 * `nhRelativeTime` pipe does: "3 minutes ago", "in 2 days", "now". The unit is the largest that
 * fits, from seconds to years, and the amount is rounded to the nearest whole unit; less than
 * half a second reads as "now". Returns `null` when `value` or `now` is missing or not a valid
 * moment.
 */
export function nhFormatRelativeTime(
  value: NhRelativeTimeInput,
  now: NhRelativeTimeInput,
  language: string,
  options?: NhRelativeTimeOptions
): string | null {
  const nowTime = relativeTimestamp(now);
  if (nowTime === null) {
    return null;
  }

  return relativeTime(value, nowTime, language, options)?.text ?? null;
}

/**
 * Shows a moment relative to now in the active language, which it takes from the module
 * configuration as `nhDate` does: `{{ task.lastModifiedDateTime | nhRelativeTime }}` gives
 * "3 minutes ago" or "in 2 days".
 *
 * Without `now` the pipe reads the clock and keeps itself current in the browser: it marks its
 * view for check when its text would next change, at most once a second while it counts seconds
 * and at least once an hour, through one timer outside Angular that all these pipes share. It
 * also works in `OnPush` and zoneless components.
 *
 * Pass `now` (a `Date`, epoch milliseconds or a string) for a fixed reference, for example in
 * tests or in a server render that must match a known time. The text then changes only when
 * `value`, `now` or the language changes, and no timer runs. A missing or invalid `now` reads the
 * clock. No timer runs during server-side rendering. A missing or invalid `value` gives `null`.
 */
@Pipe({
  name: 'nhRelativeTime',
  pure: false,
  standalone: true
})
export class NhRelativeTimePipe implements PipeTransform, OnDestroy {
  private readonly moduleConfig = inject(NhCommonModuleConfig, {optional: true});
  private readonly changeDetector = inject(ChangeDetectorRef);
  private readonly refresh = inject(NhRelativeTimeRefresh);
  private readonly isBrowser = isPlatformBrowser(inject(PLATFORM_ID));
  private readonly markForCheck = () => this.changeDetector.markForCheck();

  transform(value: NhRelativeTimeInput, now?: NhRelativeTimeInput, options?: NhRelativeTimeOptions): string | null {
    const fixedNow = relativeTimestamp(now);
    const nowTime = fixedNow ?? this.refresh.now();
    const language = this.moduleConfig?.language || this.moduleConfig?.defaultLanguage || 'en';
    const result = relativeTime(value, nowTime, language, options);

    if (result !== null && fixedNow === null && this.isBrowser) {
      this.refresh.schedule(this, nowTime + result.refreshAfter, this.markForCheck);
    } else {
      this.refresh.cancel(this);
    }

    return result?.text ?? null;
  }

  ngOnDestroy(): void {
    this.refresh.cancel(this);
  }
}
