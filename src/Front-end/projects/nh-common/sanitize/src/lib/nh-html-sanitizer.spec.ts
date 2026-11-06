import {DOCUMENT} from '@angular/common';
import {Component, PLATFORM_ID} from '@angular/core';
import {TestBed} from '@angular/core/testing';
import {
  NH_HTML_SANITIZER_DEFAULT_ATTRIBUTES,
  NH_HTML_SANITIZER_DEFAULT_TAGS,
  NhHtmlSanitizer,
  NhHtmlSanitizerOptions,
  NhSanitizeHtmlPipe,
  provideNhHtmlSanitizer
} from '@newheap/platform-common/sanitize';

/** An image source that fails to load at once, so a rendered image would run its error handler. */
const brokenImageSource = 'data:image/png;base64,bm90LWFuLWltYWdl';

/** Resolves once an image with the broken source has failed, by which time a rendered one has too. */
function brokenImageSettled(): Promise<void> {
  return new Promise(resolve => {
    const probe = new Image();
    probe.onerror = () => resolve();
    probe.onload = () => resolve();
    probe.src = brokenImageSource;
  });
}

/** Parses sanitized HTML into an inert template, so tests read elements and attributes, not attribute order. */
function parse(html: string): DocumentFragment {
  const template = document.createElement('template');
  template.innerHTML = html;
  return template.content;
}

function link(html: string): HTMLAnchorElement {
  const anchor = parse(html).querySelector('a');
  if (!anchor) {
    throw new Error(`No link in ${html}`);
  }

  return anchor;
}

describe('NhHtmlSanitizer', () => {
  let sanitizer: NhHtmlSanitizer;

  describe('with the default allow-list', () => {
    beforeEach(() => {
      sanitizer = TestBed.inject(NhHtmlSanitizer);
    });

    it('keeps the structure and inline formatting of rich text', () => {
      const html = '<h2>Plan</h2><p>Ship <strong>v2</strong>, <em>then</em> <u>review</u>.</p>'
        + '<ol start="3"><li>One</li></ol><blockquote>Quote</blockquote>'
        + '<table><thead><tr><th scope="col">Name</th></tr></thead><tbody><tr><td colspan="2" rowspan="1">Cell</td></tr></tbody></table>';

      const fragment = parse(sanitizer.sanitize(html));

      expect(fragment.querySelector('h2')?.textContent).toBe('Plan');
      expect(fragment.querySelector('p strong')?.textContent).toBe('v2');
      expect(fragment.querySelector('p em')?.textContent).toBe('then');
      expect(fragment.querySelector('p u')?.textContent).toBe('review');
      expect(fragment.querySelector('ol')?.getAttribute('start')).toBe('3');
      expect(fragment.querySelector('blockquote')?.textContent).toBe('Quote');
      expect(fragment.querySelector('th')?.getAttribute('scope')).toBe('col');
      expect(fragment.querySelector('td')?.getAttribute('colspan')).toBe('2');
      expect(fragment.querySelector('td')?.getAttribute('rowspan')).toBe('1');
    });

    it('removes scripts, styles, event handlers, frames, objects and forms', () => {
      const payloads = [
        '<script>window.nhSanitizeFired = true</script>',
        '<p onclick="window.nhSanitizeFired = true" onmouseover="alert(1)">x</p>',
        '<img src="x" onerror="window.nhSanitizeFired = true">',
        '<svg onload="window.nhSanitizeFired = true"><circle r="1"></circle></svg>',
        '<math><mi xlink:href="javascript:alert(1)">x</mi></math>',
        '<iframe src="https://example.com" srcdoc="<script>alert(1)</script>"></iframe>',
        '<object data="https://example.com/x.swf"></object><embed src="https://example.com/x.swf">',
        '<form action="https://example.com"><input name="password"><button formaction="javascript:alert(1)">Go</button><textarea></textarea><select><option>1</option></select></form>',
        '<style>body { display: none; }</style><link rel="stylesheet" href="https://example.com/x.css">',
        '<p style="position: fixed; inset: 0; background: url(https://example.com/track)">Styled</p>',
        '<meta http-equiv="refresh" content="0; url=https://example.com"><base href="https://example.com/">',
        '<template><script>alert(1)</script></template><noscript><p>x</p></noscript>',
        '<details open ontoggle="alert(1)"><summary>x</summary></details>',
        '<div data-bind="alert(1)" aria-hidden="true" class="position-fixed" id="app-root">Text</div>'
      ];

      const output = sanitizer.sanitize(payloads.join(''));

      for (const forbidden of [
        '<script', 'onclick', 'onmouseover', 'onerror', 'onload', 'ontoggle', 'javascript:', '<svg', '<math',
        '<iframe', 'srcdoc', '<object', '<embed', '<form', '<input', '<button', '<textarea', '<select',
        '<style', '<link', 'style=', '<meta', '<base', '<template', '<noscript', '<img', 'data-bind',
        'aria-hidden', 'class=', 'id='
      ]) {
        expect(output).withContext(forbidden).not.toContain(forbidden);
      }
      expect(output).toContain('Styled');
      expect(output).toContain('Text');
    });

    it('turns pasted Word content into plain rich text', () => {
      const pasted = '<html xmlns:o="urn:schemas-microsoft-com:office:office"><head>'
        + '<meta charset="utf-8"><title>Doc</title><style>p.MsoNormal { margin: 0; }</style></head><body>'
        + '<!--[if gte mso 9]><xml><o:OfficeDocumentSettings></o:OfficeDocumentSettings></xml><![endif]-->'
        + '<p class="MsoNormal" style="color: red"><b><span lang="EN-GB" style="font-size: 11pt">Budget</span></b>'
        + '<o:p></o:p></p><p class="MsoNormal"><font face="Arial">Approved</font></p></body></html>';

      expect(sanitizer.sanitize(pasted)).toBe('<p><b><span lang="EN-GB">Budget</span></b></p><p>Approved</p>');
    });

    it('does not run handlers of the content when it is rendered', async () => {
      const flags = window as unknown as {nhSanitizeFired?: boolean};
      delete flags.nhSanitizeFired;
      const container = document.createElement('div');
      document.body.appendChild(container);

      try {
        container.innerHTML = sanitizer.sanitize(
          `<p>x</p><img src=${brokenImageSource} onerror="window.nhSanitizeFired = true">`,
          {allowedTags: [...NH_HTML_SANITIZER_DEFAULT_TAGS, 'img'], allowedAttributes: [...NH_HTML_SANITIZER_DEFAULT_ATTRIBUTES, 'src']}
        );
        await brokenImageSettled();

        expect(container.querySelector('img')?.getAttribute('src')).toBe(brokenImageSource);
        expect(container.querySelector('img')?.hasAttribute('onerror')).toBeFalse();
        expect(flags.nhSanitizeFired).toBeUndefined();
      } finally {
        container.remove();
        delete flags.nhSanitizeFired;
      }
    });

    it('opens links in a new tab without access to the application or its address', () => {
      const anchor = link(sanitizer.sanitize('<a href="https://example.com/a" target="_self" rel="opener">Site</a>'));

      expect(anchor.getAttribute('href')).toBe('https://example.com/a');
      expect(anchor.getAttribute('target')).toBe('_blank');
      expect(anchor.getAttribute('rel')).toBe('noopener noreferrer');
    });

    it('keeps the safe schemes and removes every other link target', () => {
      expect(link(sanitizer.sanitize('<a href="mailto:team@example.com">Mail</a>')).getAttribute('href')).toBe('mailto:team@example.com');
      expect(link(sanitizer.sanitize('<a href="tel:+31201234567">Call</a>')).getAttribute('href')).toBe('tel:+31201234567');
      expect(link(sanitizer.sanitize('<a href="HTTPS://EXAMPLE.COM">Upper</a>')).getAttribute('href')).toBe('HTTPS://EXAMPLE.COM');

      for (const href of [
        'javascript:alert(1)',
        'JaVaScRiPt:alert(1)',
        'java&#x09;script:alert(1)',
        ' javascript:alert(1)',
        'vbscript:msgbox(1)',
        'data:text/html,<script>alert(1)</script>',
        'file:///etc/passwd',
        '/projects',
        '#notes',
        'page.html',
        '//evil.example',
        '/\\evil.example'
      ]) {
        const anchor = link(sanitizer.sanitize(`<a href="${href}" title="t">Link</a>`));

        expect(anchor.hasAttribute('href')).withContext(href).toBeFalse();
        expect(anchor.hasAttribute('target')).withContext(href).toBeFalse();
        expect(anchor.getAttribute('rel')).withContext(href).toBe('noopener noreferrer');
        expect(anchor.textContent).withContext(href).toBe('Link');
      }
    });

    it('allows relative links when asked, but never ones that leave the site', () => {
      const options: NhHtmlSanitizerOptions = {allowRelativeUrls: true};

      for (const href of ['/projects/42', '#notes', '?page=2', 'page.html', './docs/a', '../b']) {
        expect(link(sanitizer.sanitize(`<a href="${href}">x</a>`, options)).getAttribute('href')).withContext(href).toBe(href);
      }
      for (const href of ['//evil.example', '/\\evil.example', '/\t/evil.example', 'javascript:alert(1)']) {
        expect(link(sanitizer.sanitize(`<a href="${href}">x</a>`, options)).hasAttribute('href')).withContext(href).toBeFalse();
      }
    });

    it('keeps links in the page when new tabs are turned off', () => {
      const anchor = link(sanitizer.sanitize('<a href="https://example.com" target="_blank">x</a>', {openLinksInNewTab: false}));

      expect(anchor.hasAttribute('target')).toBeFalse();
      expect(anchor.getAttribute('rel')).toBe('noopener noreferrer');
    });

    it('follows an allow-list passed per call and keeps the text of removed elements', () => {
      expect(sanitizer.sanitize('<p>Keep <strong>bold</strong> <a href="https://example.com">link</a></p>', {allowedTags: ['p']}))
        .toBe('<p>Keep bold link</p>');
      expect(sanitizer.sanitize('<a href="ftp://files.example.com/a">x</a>', {allowedUriSchemes: ['ftp']}))
        .toContain('href="ftp://files.example.com/a"');
      expect(link(sanitizer.sanitize('<a href="https://example.com">x</a>', {allowedUriSchemes: ['ftp']})).hasAttribute('href')).toBeFalse();
    });

    it('never keeps a forbidden element or attribute that an allow-list names', () => {
      const output = sanitizer.sanitize(
        '<p style="color: red" onclick="alert(1)" class="lead">x</p><script>alert(1)</script><iframe src="https://example.com"></iframe><form></form>',
        {
          allowedTags: ['p', 'script', 'iframe', 'form', 'style'],
          allowedAttributes: ['style', 'onclick', 'class', 'src', 'srcdoc']
        }
      );

      expect(output).toBe('<p class="lead">x</p>');
    });

    it('rejects a scheme that is not a scheme name', () => {
      expect(() => sanitizer.sanitize('<a href="https://example.com">x</a>', {allowedUriSchemes: ['https:']}))
        .toThrowError(/'https:' is not a URI scheme/);
    });

    it('returns an empty string for missing input and sanitizes in the browser', () => {
      expect(sanitizer.sanitize(null)).toBe('');
      expect(sanitizer.sanitize(undefined)).toBe('');
      expect(sanitizer.sanitize('')).toBe('');
      // Text without markup cannot create elements; DOMPurify returns it as it is.
      expect(sanitizer.sanitize('Plain & simple')).toBe('Plain & simple');
      expect(sanitizer.sanitize('Tom &amp; <b>Jerry</b>')).toBe('Tom &amp; <b>Jerry</b>');
      expect(sanitizer.isSupported).toBeTrue();
    });
  });

  describe('with root options', () => {
    beforeEach(() => {
      TestBed.configureTestingModule({
        providers: [provideNhHtmlSanitizer({allowedTags: ['p', 'a'], openLinksInNewTab: false})]
      });
      sanitizer = TestBed.inject(NhHtmlSanitizer);
    });

    it('uses them, and options per call win', () => {
      expect(sanitizer.sanitize('<p><strong>x</strong></p>')).toBe('<p>x</p>');
      expect(link(sanitizer.sanitize('<a href="https://example.com">x</a>')).hasAttribute('target')).toBeFalse();
      expect(sanitizer.sanitize('<p><strong>x</strong></p>', {allowedTags: ['p', 'strong']})).toBe('<p><strong>x</strong></p>');
    });
  });

  describe('without a DOM', () => {
    it('escapes the HTML as text during a server render', () => {
      TestBed.configureTestingModule({providers: [{provide: PLATFORM_ID, useValue: 'server'}]});
      sanitizer = TestBed.inject(NhHtmlSanitizer);

      expect(sanitizer.isSupported).toBeFalse();
      expect(sanitizer.sanitize('<p onclick="x">Tom & "Jerry" \'s</p><script>alert(1)</script>'))
        .toBe('&lt;p onclick=&quot;x&quot;&gt;Tom &amp; &quot;Jerry&quot; &#39;s&lt;/p&gt;&lt;script&gt;alert(1)&lt;/script&gt;');
    });

    it('escapes the HTML as text in a document without a window', () => {
      TestBed.configureTestingModule({
        providers: [{provide: DOCUMENT, useValue: document.implementation.createHTMLDocument('')}]
      });
      sanitizer = TestBed.inject(NhHtmlSanitizer);

      expect(sanitizer.isSupported).toBeFalse();
      expect(sanitizer.sanitize('<b>x</b>')).toBe('&lt;b&gt;x&lt;/b&gt;');
    });
  });
});

@Component({
  selector: 'nh-sanitize-html-host',
  imports: [NhSanitizeHtmlPipe],
  template: `
    <div class="default" [innerHTML]="html | nhSanitizeHtml"></div>
    <div class="custom" [innerHTML]="html | nhSanitizeHtml: {allowedTags: ['p', 'em']}"></div>
  `
})
class SanitizeHtmlHostComponent {
  html = '<p>Hello <em>there</em> <strong>you</strong><img src="x" onerror="alert(1)"></p>';
}

@Component({
  selector: 'nh-sanitize-html-scoped-host',
  imports: [NhSanitizeHtmlPipe],
  providers: [provideNhHtmlSanitizer({allowedTags: ['p']})],
  template: '<div [innerHTML]="html | nhSanitizeHtml"></div>'
})
class ScopedSanitizeHtmlHostComponent {
  html = '<p>Hello <em>there</em></p>';
}

describe('NhSanitizeHtmlPipe', () => {
  it('renders the sanitized HTML through innerHTML without a second sanitizing pass', () => {
    const fixture = TestBed.createComponent(SanitizeHtmlHostComponent);
    fixture.detectChanges();
    const element: HTMLElement = fixture.nativeElement;

    expect(element.querySelector('.default')?.innerHTML).toBe('<p>Hello <em>there</em> <strong>you</strong></p>');
    expect(element.querySelector('.custom')?.innerHTML).toBe('<p>Hello <em>there</em> you</p>');
  });

  it('uses the options that a component provides for its template', () => {
    const fixture = TestBed.createComponent(ScopedSanitizeHtmlHostComponent);
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).querySelector('div')?.innerHTML).toBe('<p>Hello there</p>');
  });

  it('renders escaped text during a server render', () => {
    TestBed.configureTestingModule({providers: [{provide: PLATFORM_ID, useValue: 'server'}]});
    const fixture = TestBed.createComponent(ScopedSanitizeHtmlHostComponent);
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).querySelector('div')?.textContent).toBe('<p>Hello <em>there</em></p>');
  });
});
