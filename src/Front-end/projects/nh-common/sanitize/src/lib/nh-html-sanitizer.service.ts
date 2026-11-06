import {DOCUMENT, isPlatformBrowser} from '@angular/common';
import {inject, Injectable, PLATFORM_ID} from '@angular/core';
import {DomSanitizer, SafeHtml} from '@angular/platform-browser';
import createDOMPurify, {Config, DOMPurify} from 'dompurify';
import {
  mergeNhHtmlSanitizerOptions,
  NH_HTML_SANITIZER_DEFAULT_ATTRIBUTES,
  NH_HTML_SANITIZER_DEFAULT_TAGS,
  NH_HTML_SANITIZER_DEFAULT_URI_SCHEMES,
  NH_HTML_SANITIZER_OPTIONS,
  NhHtmlSanitizerOptions
} from './nh-html-sanitizer-options';

/** Elements that are removed with their content whatever the allow-list says. */
const forbiddenTags: readonly string[] = [
  'script', 'style', 'link', 'meta', 'base', 'title', 'template', 'noscript', 'slot',
  'iframe', 'frame', 'frameset', 'object', 'embed', 'applet', 'portal',
  'form', 'input', 'button', 'select', 'option', 'optgroup', 'textarea', 'datalist', 'output',
  'svg', 'math'
];

/** Attributes that are removed whatever the allow-list says; every `on...` event handler is removed too. */
const forbiddenAttributes: readonly string[] = ['style', 'srcdoc', 'action', 'formaction', 'form', 'xmlns'];

/** Attributes whose value is a URL and must match the allowed schemes. Other allowed attributes skip that check. */
const uriAttributes: ReadonlySet<string> = new Set([
  'href', 'src', 'srcset', 'cite', 'action', 'formaction', 'background', 'poster', 'longdesc', 'xlink:href', 'data'
]);

const uriSchemePattern = /^[a-z][a-z0-9+.-]*$/i;

/** A relative URL: a path that does not start with `//` or `/\`, a query, a fragment, or a path without a scheme. */
const relativeUrlPattern = '\\/(?![\\/\\\\])|[?#]|[^:\\/?#\\\\]+(?:[\\/?#]|$)';

interface NhResolvedHtmlSanitizerOptions {
  readonly allowedTags: string[];
  readonly allowedAttributes: string[];
  readonly uriSafeAttributes: string[];
  readonly allowedUris: RegExp;
  readonly openLinksInNewTab: boolean;
}

/** Escapes text so that it shows as text when it is used as HTML. */
function escapeHtml(text: string): string {
  return text
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

function normalizedNames(names: readonly string[]): string[] {
  return [...new Set(names.map(name => name.trim().toLowerCase()).filter(name => name.length > 0))];
}

function allowedUriPattern(schemes: readonly string[], allowRelativeUrls: boolean): RegExp {
  const alternatives: string[] = [];

  for (const scheme of schemes) {
    if (!uriSchemePattern.test(scheme)) {
      throw new Error(`NhHtmlSanitizer: '${scheme}' is not a URI scheme. Pass scheme names such as 'https', without the colon.`);
    }
  }

  if (schemes.length > 0) {
    alternatives.push(`(?:${schemes.map(scheme => scheme.replace(/[.+-]/g, '\\$&')).join('|')}):`);
  }

  if (allowRelativeUrls) {
    alternatives.push(relativeUrlPattern);
  }

  if (alternatives.length === 0) {
    // Nothing is allowed: a pattern that never matches.
    return /(?!)/;
  }

  return new RegExp(`^(?:${alternatives.join('|')})`, 'i');
}

/**
 * Sanitizes untrusted HTML, such as pasted e-mail or Word content, with DOMPurify and a strict
 * allow-list. Scripts, styles, event handlers, frames, embedded objects and forms never survive;
 * URL attributes keep only the allowed schemes, and every link gets `rel="noopener noreferrer"`.
 *
 * Without a DOM, during server-side rendering, the HTML is escaped and shows as text.
 *
 * Configure it with `provideNhHtmlSanitizer(...)` at the root, or pass options per call.
 */
@Injectable({providedIn: 'root'})
export class NhHtmlSanitizer {
  private readonly document = inject(DOCUMENT);
  private readonly isBrowser = isPlatformBrowser(inject(PLATFORM_ID));
  private readonly domSanitizer = inject(DomSanitizer);
  private readonly rootOptions = inject(NH_HTML_SANITIZER_OPTIONS, {optional: true});
  private purifier: DOMPurify | null | undefined;
  private activeOptions: NhResolvedHtmlSanitizerOptions | null = null;

  /** Whether HTML is sanitized here. `false` on the server or without a DOM, where HTML is escaped as text. */
  get isSupported(): boolean {
    return this.getPurifier() !== null;
  }

  /**
   * Returns the sanitized HTML, or the escaped text when there is no DOM. `null`, `undefined`
   * and an empty string give an empty string. `options` win over the root options.
   */
  sanitize(html: string | null | undefined, options?: NhHtmlSanitizerOptions): string {
    if (html === null || html === undefined || html === '') {
      return '';
    }

    const text = String(html);
    const resolved = this.resolve(options);
    const purifier = this.getPurifier();
    if (!purifier) {
      return escapeHtml(text);
    }

    const config: Config = {
      ALLOWED_TAGS: resolved.allowedTags,
      ALLOWED_ATTR: resolved.allowedAttributes,
      ADD_URI_SAFE_ATTR: resolved.uriSafeAttributes,
      ALLOWED_URI_REGEXP: resolved.allowedUris,
      FORBID_TAGS: [...forbiddenTags],
      FORBID_ATTR: [...forbiddenAttributes],
      ALLOW_DATA_ATTR: false,
      ALLOW_ARIA_ATTR: false,
      ALLOW_UNKNOWN_PROTOCOLS: false,
      KEEP_CONTENT: true
    };

    this.activeOptions = resolved;
    try {
      return purifier.sanitize(text, config);
    } finally {
      this.activeOptions = null;
    }
  }

  /**
   * Sanitizes like `sanitize` and marks the result as trusted, so `[innerHTML]` renders exactly
   * what this sanitizer kept instead of sanitizing it a second time with Angular's stricter rules.
   */
  sanitizeToSafeHtml(html: string | null | undefined, options?: NhHtmlSanitizerOptions): SafeHtml {
    return this.domSanitizer.bypassSecurityTrustHtml(this.sanitize(html, options));
  }

  private resolve(options: NhHtmlSanitizerOptions | undefined): NhResolvedHtmlSanitizerOptions {
    const merged = mergeNhHtmlSanitizerOptions(options, this.rootOptions);
    const allowedTags = normalizedNames(merged.allowedTags ?? NH_HTML_SANITIZER_DEFAULT_TAGS)
      .filter(tag => !forbiddenTags.includes(tag));
    const allowedAttributes = normalizedNames(merged.allowedAttributes ?? NH_HTML_SANITIZER_DEFAULT_ATTRIBUTES)
      .filter(attribute => !forbiddenAttributes.includes(attribute) && !attribute.startsWith('on'));
    const schemes = normalizedNames(merged.allowedUriSchemes ?? NH_HTML_SANITIZER_DEFAULT_URI_SCHEMES);

    return {
      allowedTags,
      allowedAttributes,
      uriSafeAttributes: allowedAttributes.filter(attribute => !uriAttributes.has(attribute)),
      allowedUris: allowedUriPattern(schemes, merged.allowRelativeUrls ?? false),
      openLinksInNewTab: merged.openLinksInNewTab ?? true
    };
  }

  private getPurifier(): DOMPurify | null {
    if (this.purifier !== undefined) {
      return this.purifier;
    }

    const view = this.isBrowser ? this.document.defaultView : null;
    if (!view) {
      this.purifier = null;
      return null;
    }

    const purifier = createDOMPurify(view);
    if (!purifier.isSupported) {
      this.purifier = null;
      return null;
    }

    purifier.addHook('afterSanitizeAttributes', node => {
      if (node.nodeName !== 'A') {
        return;
      }

      node.setAttribute('rel', 'noopener noreferrer');
      if (this.activeOptions?.openLinksInNewTab && node.hasAttribute('href')) {
        node.setAttribute('target', '_blank');
      }
    });

    this.purifier = purifier;
    return purifier;
  }
}
