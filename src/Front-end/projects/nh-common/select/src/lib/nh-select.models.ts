import { InjectionToken, Provider, Signal, isSignal } from '@angular/core';
import { Observable } from 'rxjs';

/** One choice of an `nh-combobox` or `nh-multiselect`. */
export interface NhSelectOption<T = unknown> {
  /**
   * The value the control stores. Any primitive is kept as it is, including `0`, `false` and `''`;
   * an object value is compared with the control's `compareWith`.
   */
  value: T;
  /** The visible name; typing filters on it. */
  label: string;
  /** A second line under the name; typing filters on it too. */
  description?: string | null;
  /** Shown but not choosable. A chosen disabled option cannot be removed by the person either. */
  disabled?: boolean;
  /** Icon classes of the application's icon library, such as `ph ph-user`. */
  icon?: string | null;
  /** The address of a small image, such as an avatar. It is shown instead of `icon`. */
  image?: string | null;
  /** Extra CSS classes of the option's row and of its chip. */
  classes?: string | readonly string[] | null;
}

/** Answers the options that match the typed text, from a server or another source. */
export type NhSelectSearch<T> = (query: string) => Observable<readonly NhSelectOption<T>[]> | Promise<readonly NhSelectOption<T>[]>;

/** Creates an option for the typed text, usually on a server, and answers the new option. */
export type NhSelectCreate<T> = (text: string) => Observable<NhSelectOption<T>> | Promise<NhSelectOption<T>>;

/** Says whether two values are the same choice. */
export type NhSelectCompare<T> = (left: T, right: T) => boolean;

/** The height and type size of the field: `compact` suits tables and toolbars. */
export type NhSelectSize = 'default' | 'compact';

/** The work a failure belongs to. */
export type NhSelectOperation = 'search' | 'create';

/**
 * Turns a failed search or create into the text the field shows, such as a translated message for
 * the error's code. Answer `null` to show the generic text of `NhSelectTexts`. Never return raw
 * server text, HTML or stack traces.
 */
export type NhSelectErrorText = (error: unknown, operation: NhSelectOperation) => string | null;

/** The texts of the controls. Texts that depend on a value are functions of it. */
export interface NhSelectTexts {
  /** The accessible name of the button that opens and closes the list. */
  toggle: string;
  /** Shown while a search runs. */
  searching: string;
  /** Shown when no option matches. */
  noResults: string;
  /** Shown with a server search until enough characters are typed. */
  typeMore: (minQueryLength: number) => string;
  /** Shown when a search failed and no `errorText` describes the error. */
  searchFailed: string;
  /** Shown when creating an option failed and no `errorText` describes the error. */
  createFailed: string;
  /** The button that repeats a failed search or create. */
  retry: string;
  /** The row that creates an option for the typed text. */
  create: (text: string) => string;
  /** The create row while the option is being created. */
  creating: string;
  /** The accessible name of a chip's remove button. */
  remove: (label: string) => string;
  /** Announced when the person adds an option to a multiselect. */
  added: (label: string) => string;
  /** Announced when the person removes an option from a multiselect. */
  removed: (label: string) => string;
}

/** The English texts the controls use when neither the input nor `provideNhSelect` gives one. */
export const NH_SELECT_DEFAULT_TEXTS: NhSelectTexts = {
  toggle: 'Show the options',
  searching: 'Searching…',
  noResults: 'Nothing found.',
  typeMore: minQueryLength => minQueryLength === 1
    ? 'Type at least one character.'
    : `Type at least ${minQueryLength} characters.`,
  searchFailed: 'The search failed.',
  createFailed: 'The option could not be created.',
  retry: 'Try again',
  create: text => `Create “${text}”`,
  creating: 'Creating…',
  remove: label => `Remove ${label}`,
  added: label => `${label} added.`,
  removed: label => `${label} removed.`
};

/** Application-wide defaults of every combobox and multiselect below the provider. */
export interface NhSelectConfig {
  /** Texts that replace the English defaults, for example translated ones. */
  texts?: Partial<NhSelectTexts>;
  /** Describes a failed search or create; see `NhSelectErrorText`. */
  errorText?: NhSelectErrorText;
  /** Characters a server search waits for; `0` searches as soon as the list opens. Default `2`. */
  minQueryLength?: number;
  /** Milliseconds a server search waits after the last key. Default `250`. */
  searchDebounceMs?: number;
  /** The default size. Default `'default'`. */
  size?: NhSelectSize;
}

/** A configuration, or a signal of one for texts that follow the application's language. */
export type NhSelectConfigSource = NhSelectConfig | Signal<NhSelectConfig>;

/** The application-wide defaults; register them with `provideNhSelect`. */
export const NH_SELECT_CONFIG = new InjectionToken<NhSelectConfigSource>('NH_SELECT_CONFIG');

/**
 * Registers application-wide defaults for the controls below this injector. Pass a configuration, a
 * signal of one, or a factory that runs in an injection context, for example to read a translation
 * service and answer a signal that changes with the language.
 */
export function provideNhSelect(config: NhSelectConfigSource | (() => NhSelectConfigSource)): Provider {
  if (typeof config === 'function' && !isSignal(config)) {
    return { provide: NH_SELECT_CONFIG, useFactory: config };
  }

  return { provide: NH_SELECT_CONFIG, useValue: config };
}

/** The default comparison: the same primitive or the same object reference. */
export function nhSelectSameValue<T>(left: T, right: T): boolean {
  return left === right;
}
