import { ChangeDetectionStrategy, Component, HostBinding, Input } from '@angular/core';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';

/**
 * Port of https://github.com/coryrylan/ngx-lite/blob/master/projects/ngx-json-ld/src/ngx-json-ld.component.ts
 */
@Component({
    selector: 'nh-json-ld',
    template: '',
    changeDetection: ChangeDetectionStrategy.OnPush,
    standalone: false
})
export class NhJsonLdComponent {
  @Input() set json(value: any) {
    this._jsonLD = this._getSafeHTML(value);
  }

  @HostBinding('innerHTML') private _jsonLD?: SafeHtml;

  constructor(private _sanitizer: DomSanitizer) {
  }

  private _getSafeHTML(value: any) {
    const json = value
      ? NhJsonLdComponent.serializeForScriptElement(value)
      : '';
    const html = `<script type="application/ld+json">${json}</script>`;

    return this._sanitizer.bypassSecurityTrustHtml(html);
  }

  /**
   * Serializes the structured data so that no string value can end its script element. Every `<`
   * becomes the JSON escape `\u003c`, which rules out `</script` in any letter case and `<!--`, and
   * the line and paragraph separators U+2028 and U+2029 are escaped as well. `JSON.parse` returns
   * the original values.
   */
  private static serializeForScriptElement(value: unknown): string {
    const json = JSON.stringify(value, null, 2) ?? '';

    return json
      .replace(/</g, '\\u003c')
      .replace(/\u2028/g, '\\u2028')
      .replace(/\u2029/g, '\\u2029');
  }
}
