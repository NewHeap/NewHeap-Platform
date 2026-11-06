import { Pipe, PipeTransform } from '@angular/core';
import { DomSanitizer } from '@angular/platform-browser';

/**
 * Marks HTML as trusted with `DomSanitizer.bypassSecurityTrustHtml`. This bypasses Angular's
 * sanitizer instead of sanitizing: scripts, event handlers and `javascript:` links in the value
 * run in the application. Use it only for HTML the application itself wrote.
 *
 * @deprecated Show untrusted HTML, such as pasted e-mail or Word content or text users wrote,
 * through `nhSanitizeHtml` or `NhHtmlSanitizer` from `@newheap/platform-common/sanitize`. The
 * behavior of `safeHtml` is unchanged.
 */
@Pipe({
    name: 'safeHtml',
    standalone: false
})
export class SafeHtmlPipe implements PipeTransform  {
  constructor(private sanitized: DomSanitizer) {}
  transform(value: string) {
    return this.sanitized.bypassSecurityTrustHtml(value);
  }
}

@Pipe({
    name: 'safeUrl',
    standalone: false
})
export class SafeUrlPipe implements PipeTransform {
  constructor(private sanitizer: DomSanitizer) {
  }

  transform(value: string) {
    return this.sanitizer.bypassSecurityTrustUrl(value);
  }
}

@Pipe({
    name: 'safeResourceUrl',
    standalone: false
})
export class SafeResourceUrlPipe implements PipeTransform {
  constructor(private sanitizer: DomSanitizer) {
  }

  transform(value: string) {
    return this.sanitizer.bypassSecurityTrustResourceUrl(value);
  }
}
