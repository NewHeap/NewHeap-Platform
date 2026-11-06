import {InjectionToken, Provider} from '@angular/core';

/**
 * Configures what `NhHtmlSanitizer` and the `nhSanitizeHtml` pipe keep. Every list replaces the
 * default list rather than extending it; spread the exported defaults to extend them, for example
 * `allowedTags: [...NH_HTML_SANITIZER_DEFAULT_TAGS, 'img']`.
 *
 * Scripts, styles, event handlers, frames, embedded objects, forms and form controls are always
 * removed, also when an allow-list names them.
 */
export interface NhHtmlSanitizerOptions {
  /** Elements that are kept. Any other element is removed; its text stays unless it is a script, style or similar. */
  allowedTags?: readonly string[];
  /** Attributes that are kept on the allowed elements. */
  allowedAttributes?: readonly string[];
  /** URL schemes, without the colon, that `href`, `src` and other URL attributes may use. */
  allowedUriSchemes?: readonly string[];
  /** Whether URL attributes may hold a relative URL such as `/projects`, `#notes` or `page.html`. Defaults to `false`. */
  allowRelativeUrls?: boolean;
  /**
   * Whether links open in a new tab with `target="_blank"`. Defaults to `true`. Every link gets
   * `rel="noopener noreferrer"` either way.
   */
  openLinksInNewTab?: boolean;
}

/** The elements kept by default: the structure and inline formatting of rich text such as pasted e-mail or Word content. */
export const NH_HTML_SANITIZER_DEFAULT_TAGS: readonly string[] = [
  'a', 'abbr', 'b', 'blockquote', 'br', 'caption', 'code', 'col', 'colgroup', 'dd', 'del', 'div', 'dl', 'dt',
  'em', 'h1', 'h2', 'h3', 'h4', 'h5', 'h6', 'hr', 'i', 'ins', 'kbd', 'li', 'mark', 'ol', 'p', 'pre', 'q', 's',
  'small', 'span', 'strong', 'sub', 'sup', 'table', 'tbody', 'td', 'tfoot', 'th', 'thead', 'tr', 'u', 'ul'
];

/** The attributes kept by default. No `class`, `id` or `style`, so pasted content cannot take over the application's styling. */
export const NH_HTML_SANITIZER_DEFAULT_ATTRIBUTES: readonly string[] = [
  'href', 'title', 'lang', 'dir', 'colspan', 'rowspan', 'scope', 'start', 'reversed'
];

/** The URL schemes allowed by default. */
export const NH_HTML_SANITIZER_DEFAULT_URI_SCHEMES: readonly string[] = ['http', 'https', 'mailto', 'tel'];

/**
 * Options for the sanitizer. Provide them at the root for `NhHtmlSanitizer`; a route or component
 * may provide its own options for the `nhSanitizeHtml` pipes in its templates.
 */
export const NH_HTML_SANITIZER_OPTIONS = new InjectionToken<NhHtmlSanitizerOptions>('NH_HTML_SANITIZER_OPTIONS');

/** Provides `NH_HTML_SANITIZER_OPTIONS`, in the root providers or in a component's `providers`. */
export function provideNhHtmlSanitizer(options: NhHtmlSanitizerOptions): Provider {
  return {provide: NH_HTML_SANITIZER_OPTIONS, useValue: options};
}

/** Combines option layers; the first layer that sets a property wins, and an `undefined` property does not count as set. */
export function mergeNhHtmlSanitizerOptions(
  ...layers: ReadonlyArray<NhHtmlSanitizerOptions | null | undefined>
): NhHtmlSanitizerOptions {
  const merged: NhHtmlSanitizerOptions = {};

  for (const layer of layers) {
    if (!layer) {
      continue;
    }

    merged.allowedTags ??= layer.allowedTags;
    merged.allowedAttributes ??= layer.allowedAttributes;
    merged.allowedUriSchemes ??= layer.allowedUriSchemes;
    merged.allowRelativeUrls ??= layer.allowRelativeUrls;
    merged.openLinksInNewTab ??= layer.openLinksInNewTab;
  }

  return merged;
}
