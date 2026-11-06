import {inject, Pipe, PipeTransform} from '@angular/core';
import {SafeHtml} from '@angular/platform-browser';
import {mergeNhHtmlSanitizerOptions, NH_HTML_SANITIZER_OPTIONS, NhHtmlSanitizerOptions} from './nh-html-sanitizer-options';
import {NhHtmlSanitizer} from './nh-html-sanitizer.service';

/**
 * Sanitizes untrusted HTML for `[innerHTML]` with `NhHtmlSanitizer`:
 * `<div [innerHTML]="message.body | nhSanitizeHtml"></div>`.
 *
 * The options argument wins over `NH_HTML_SANITIZER_OPTIONS` provided for the template, which win
 * over the root options. During server-side rendering the HTML is escaped and shows as text.
 */
@Pipe({
  name: 'nhSanitizeHtml',
  standalone: true
})
export class NhSanitizeHtmlPipe implements PipeTransform {
  private readonly sanitizer = inject(NhHtmlSanitizer);
  private readonly templateOptions = inject(NH_HTML_SANITIZER_OPTIONS, {optional: true});

  transform(html: string | null | undefined, options?: NhHtmlSanitizerOptions): SafeHtml {
    return this.sanitizer.sanitizeToSafeHtml(html, mergeNhHtmlSanitizerOptions(options, this.templateOptions));
  }
}
