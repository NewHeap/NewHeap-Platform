import {ChangeDetectionStrategy, Component, PLATFORM_ID} from '@angular/core';
import {ComponentFixture, fakeAsync, TestBed, tick} from '@angular/core/testing';
import {NhCommonModuleConfig} from '../models/config.models';
import {nhFormatRelativeTime, NhRelativeTimeInput, NhRelativeTimeOptions, NhRelativeTimePipe} from './date.pipes';

const now = Date.parse('2026-11-06T12:00:00Z');
const second = 1000;
const minute = 60 * second;
const hour = 60 * minute;
const day = 24 * hour;

function inEnglish(offset: number, options?: NhRelativeTimeOptions): string | null {
  return nhFormatRelativeTime(now + offset, now, 'en', options);
}

describe('nhFormatRelativeTime', () => {
  it('picks the largest unit that fits, from seconds to years, and rounds down', () => {
    const cases: Array<[number, string]> = [
      [-999, 'now'],
      [-1 * second, '1 second ago'],
      [-59 * second, '59 seconds ago'],
      [-60 * second, '1 minute ago'],
      [-90 * second, '1 minute ago'],
      [-59 * minute, '59 minutes ago'],
      [-1 * hour, '1 hour ago'],
      [-23 * hour - 59 * minute, '23 hours ago'],
      [-1 * day, '1 day ago'],
      [-6 * day, '6 days ago'],
      [-7 * day, '1 week ago'],
      [-29 * day, '4 weeks ago'],
      [-30 * day, '1 month ago'],
      [-364 * day, '12 months ago'],
      [-365 * day, '1 year ago'],
      [-800 * day, '2 years ago']
    ];

    for (const [offset, expected] of cases) {
      expect(inEnglish(offset)).withContext(`${offset} ms`).toBe(expected);
    }
  });

  it('formats future moments', () => {
    expect(inEnglish(0)).toBe('now');
    expect(inEnglish(999)).toBe('now');
    expect(inEnglish(1 * second)).toBe('in 1 second');
    expect(inEnglish(5 * minute + 30 * second)).toBe('in 5 minutes');
    expect(inEnglish(2 * day)).toBe('in 2 days');
    expect(inEnglish(14 * day)).toBe('in 2 weeks');
    expect(inEnglish(400 * day)).toBe('in 1 year');
  });

  it('formats in the requested language', () => {
    expect(nhFormatRelativeTime(now - 3 * minute, now, 'nl')).toBe('3 minuten geleden');
    expect(nhFormatRelativeTime(now + 2 * day, now, 'nl')).toBe('over 2 dagen');
    expect(nhFormatRelativeTime(now, now, 'nl')).toBe('nu');
    expect(nhFormatRelativeTime(now - 3 * minute, now, 'de')).toBe('vor 3 Minuten');
  });

  it('applies the style and numeric options', () => {
    expect(inEnglish(-3 * minute, {style: 'short'})).toBe('3 min. ago');
    expect(inEnglish(-1 * day)).toBe('1 day ago');
    expect(inEnglish(-1 * day, {numeric: 'auto'})).toBe('yesterday');
    expect(nhFormatRelativeTime(now + 1 * day, now, 'nl', {numeric: 'auto'})).toBe('morgen');
  });

  it('accepts dates, epoch milliseconds and strings', () => {
    expect(nhFormatRelativeTime(new Date(now - 2 * hour), new Date(now), 'en')).toBe('2 hours ago');
    expect(nhFormatRelativeTime('2026-11-06T11:00:00Z', '2026-11-06T12:00:00Z', 'en')).toBe('1 hour ago');
    expect(nhFormatRelativeTime('2026-11-06T13:00:00+01:00', now, 'en')).toBe('now');
  });

  it('returns null for a missing or invalid moment', () => {
    for (const value of [null, undefined, '', '   ', 'not a date', Number.NaN, Number.POSITIVE_INFINITY, new Date('invalid')]) {
      expect(nhFormatRelativeTime(value, now, 'en')).withContext(String(value)).toBeNull();
    }
    expect(nhFormatRelativeTime({} as unknown as NhRelativeTimeInput, now, 'en')).toBeNull();
    expect(nhFormatRelativeTime(now, 'not a date', 'en')).toBeNull();
    expect(nhFormatRelativeTime(now, null, 'en')).toBeNull();
  });

  it('falls back to English for a value that is not a language tag', () => {
    expect(nhFormatRelativeTime(now - 3 * minute, now, 'not a language!')).toBe('3 minutes ago');
    expect(nhFormatRelativeTime(now - 3 * minute, now, '')).toBe('3 minutes ago');
  });
});

@Component({
  selector: 'nh-relative-time-live-host',
  imports: [NhRelativeTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '<span>{{ moment | nhRelativeTime }}</span>'
})
class LiveRelativeTimeHostComponent {
  moment: NhRelativeTimeInput = null;
}

@Component({
  selector: 'nh-relative-time-fixed-host',
  imports: [NhRelativeTimePipe],
  template: '<span>{{ moment | nhRelativeTime: fixedNow : options }}</span>'
})
class FixedRelativeTimeHostComponent {
  moment: NhRelativeTimeInput = null;
  fixedNow: NhRelativeTimeInput = null;
  options?: NhRelativeTimeOptions;
}

function text(fixture: ComponentFixture<unknown>): string {
  return ((fixture.nativeElement as HTMLElement).querySelector('span')?.textContent ?? '').trim();
}

function createLiveHost(moment: NhRelativeTimeInput): ComponentFixture<LiveRelativeTimeHostComponent> {
  const fixture = TestBed.createComponent(LiveRelativeTimeHostComponent);
  fixture.componentInstance.moment = moment;
  fixture.detectChanges();
  return fixture;
}

function createFixedHost(
  moment: NhRelativeTimeInput,
  fixedNow: NhRelativeTimeInput,
  options?: NhRelativeTimeOptions
): ComponentFixture<FixedRelativeTimeHostComponent> {
  const fixture = TestBed.createComponent(FixedRelativeTimeHostComponent);
  fixture.componentInstance.moment = moment;
  fixture.componentInstance.fixedNow = fixedNow;
  fixture.componentInstance.options = options;
  fixture.detectChanges();
  return fixture;
}

// fakeAsync fails a test that leaves a timer queued, so the tests without fixture.destroy() also
// prove that no refresh timer was scheduled.
describe('NhRelativeTimePipe', () => {
  let config: NhCommonModuleConfig;

  beforeEach(() => {
    config = new NhCommonModuleConfig({language: 'en', defaultLanguage: 'en'});
    TestBed.configureTestingModule({providers: [{provide: NhCommonModuleConfig, useValue: config}]});
  });

  it('formats against a fixed now in the active language without a timer', fakeAsync(() => {
    const fixture = createFixedHost(now - 3 * minute, now, {style: 'short'});

    expect(text(fixture)).toBe('3 min. ago');

    tick(10 * minute);
    fixture.detectChanges();
    expect(text(fixture)).toBe('3 min. ago');
  }));

  it('follows a change of the active language', fakeAsync(() => {
    const fixture = createFixedHost(now - 3 * minute, now);
    expect(text(fixture)).toBe('3 minutes ago');

    config.language = 'nl';
    fixture.detectChanges();

    expect(text(fixture)).toBe('3 minuten geleden');
  }));

  it('falls back to the default language and then to English', fakeAsync(() => {
    config.language = '';
    config.defaultLanguage = 'nl';
    const fixture = createFixedHost(now + 2 * day, now);
    expect(text(fixture)).toBe('over 2 dagen');

    config.defaultLanguage = '';
    fixture.detectChanges();
    expect(text(fixture)).toBe('in 2 days');
  }));

  it('reads the clock for a missing or invalid now', fakeAsync(() => {
    const fixture = createFixedHost(Date.now() - 2 * hour, 'not a date');

    expect(text(fixture)).toBe('2 hours ago');
    fixture.destroy();
  }));

  it('renders nothing for a missing or invalid value, without a timer', fakeAsync(() => {
    const live = createLiveHost(null);
    const invalid = createLiveHost('not a date');

    expect(text(live)).toBe('');
    expect(text(invalid)).toBe('');
  }));

  it('keeps its text current on its own in an OnPush view', fakeAsync(() => {
    const fixture = createLiveHost(Date.now() - 58 * second);
    expect(text(fixture)).toBe('58 seconds ago');

    tick(second);
    fixture.detectChanges();
    expect(text(fixture)).toBe('59 seconds ago');

    tick(second);
    fixture.detectChanges();
    expect(text(fixture)).toBe('1 minute ago');

    // The next change is a minute away; nothing marks the view before it.
    tick(30 * second);
    fixture.detectChanges();
    expect(text(fixture)).toBe('1 minute ago');

    tick(30 * second);
    fixture.detectChanges();
    expect(text(fixture)).toBe('2 minutes ago');

    // Destroying the view stops its timer.
    fixture.destroy();
  }));

  it('counts down to a future moment and past it', fakeAsync(() => {
    const fixture = createLiveHost(Date.now() + 2 * second + 500);
    expect(text(fixture)).toBe('in 2 seconds');

    tick(second);
    fixture.detectChanges();
    expect(text(fixture)).toBe('in 1 second');

    tick(2 * second);
    fixture.detectChanges();
    expect(text(fixture)).toBe('now');

    tick(second);
    fixture.detectChanges();
    expect(text(fixture)).toBe('1 second ago');
    fixture.destroy();
  }));

  it('refreshes each pipe at its own moment and stops when the last one is destroyed', fakeAsync(() => {
    const recent = createLiveHost(Date.now() - 10 * second);
    const older = createLiveHost(Date.now() - 3 * minute);

    tick(second);
    recent.detectChanges();
    older.detectChanges();
    expect(text(recent)).toBe('11 seconds ago');
    expect(text(older)).toBe('3 minutes ago');

    recent.destroy();
    tick(minute);
    older.detectChanges();
    expect(text(older)).toBe('4 minutes ago');
    older.destroy();
  }));

  it('uses English without a module configuration', fakeAsync(() => {
    TestBed.resetTestingModule();
    const fixture = createFixedHost(now - 2 * day, now);

    expect(text(fixture)).toBe('2 days ago');
  }));
});

describe('NhRelativeTimePipe during a server render', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        {provide: PLATFORM_ID, useValue: 'server'},
        {provide: NhCommonModuleConfig, useValue: new NhCommonModuleConfig({language: 'en'})}
      ]
    });
  });

  it('formats from the clock without scheduling a timer', fakeAsync(() => {
    const fixture = createLiveHost(Date.now() - 5 * minute);

    expect(text(fixture)).toBe('5 minutes ago');
  }));
});

