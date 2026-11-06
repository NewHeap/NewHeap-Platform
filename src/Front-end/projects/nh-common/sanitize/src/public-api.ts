/*
 * Public API surface of @newheap/platform-common/sanitize.
 *
 * The HTML sanitizer is a separate entry point with `dompurify` as an optional peer dependency,
 * so applications that do not import it need not install `dompurify`.
 */
export {
  NH_HTML_SANITIZER_DEFAULT_ATTRIBUTES,
  NH_HTML_SANITIZER_DEFAULT_TAGS,
  NH_HTML_SANITIZER_DEFAULT_URI_SCHEMES,
  NH_HTML_SANITIZER_OPTIONS,
  provideNhHtmlSanitizer
} from './lib/nh-html-sanitizer-options';
export type {NhHtmlSanitizerOptions} from './lib/nh-html-sanitizer-options';
export * from './lib/nh-html-sanitizer.service';
export * from './lib/nh-sanitize-html.pipe';
