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
      ? JSON.stringify(value, null, 2).replace(/<\/script>/g, '<\\/script>')
      : '';
    const html = `<script type="application/ld+json">${json}</script>`;

    return this._sanitizer.bypassSecurityTrustHtml(html);
  }
}
