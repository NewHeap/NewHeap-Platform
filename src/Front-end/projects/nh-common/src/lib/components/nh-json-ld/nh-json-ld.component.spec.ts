import {TestBed} from '@angular/core/testing';

import {NhJsonLdDataItem, NhJsonLdService, REVIEW_KEY} from '../../services/nh-json-ld.service';
import {NhJsonLdComponent} from './nh-json-ld.component';

/** An image source that fails to load at once, so an injected image would run its error handler. */
const brokenImageSource = 'data:image/png;base64,bm90LWFuLWltYWdl';

/**
 * A string value that tries to end the script element and add an image with an inline handler. The
 * attributes are unquoted because JSON escapes double quotes.
 */
const breakout = (endTag: string) => `${endTag}<img src=${brokenImageSource} onerror=window.nhJsonLdFired=true>`;

/** Resolves once an image with the broken source has failed, by which time an injected one has too. */
function brokenImageSettled(): Promise<void> {
  return new Promise(resolve => {
    const probe = new Image();
    probe.onerror = () => resolve();
    probe.onload = () => resolve();
    probe.src = brokenImageSource;
  });
}

describe('NhJsonLdComponent', () => {
  const flags = window as unknown as {nhJsonLdFired?: boolean};

  beforeEach(() => {
    delete flags.nhJsonLdFired;
    TestBed.configureTestingModule({declarations: [NhJsonLdComponent]});
  });

  afterEach(() => {
    delete flags.nhJsonLdFired;
  });

  const render = (json: unknown): HTMLElement => {
    const fixture = TestBed.createComponent(NhJsonLdComponent);
    fixture.componentRef.setInput('json', json);
    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  };

  const expectSingleScript = (host: HTMLElement): HTMLScriptElement => {
    expect(host.children.length).toBe(1);
    expect(host.querySelector('img')).toBeNull();

    const script = host.firstElementChild as HTMLScriptElement;
    expect(script.tagName).toBe('SCRIPT');
    expect(script.type).toBe('application/ld+json');

    return script;
  };

  for (const endTag of ['</script>', '</SCRIPT>', '</Script>', '</script >', '</script/>', '<!--<script>']) {
    it(`keeps a string value with ${endTag} inside one script element`, async () => {
      const value = {'@context': 'https://schema.org/', '@type': 'Thing', name: breakout(endTag)};

      const script = expectSingleScript(render(value));
      await brokenImageSettled();

      expect(flags.nhJsonLdFired).toBeUndefined();
      expect(JSON.parse(script.textContent ?? '')).toEqual(value);
    });
  }

  it('keeps a review body resolved through NhJsonLdService inside one script element', async () => {
    const service = new NhJsonLdService();
    const reviewKey = REVIEW_KEY('sample-product');
    service.addItem(new NhJsonLdDataItem({
      id: 'sample-product',
      placeholderKeys: [reviewKey],
      data: {'@type': 'Product', name: 'Sample product', review: reviewKey}
    }));
    service.addItem(new NhJsonLdDataItem({
      id: 'sample-review',
      resolvePlaceholderKey: reviewKey,
      data: {'@type': 'Review', reviewBody: breakout('</SCRIPT>')}
    }));
    const structuredData = service.build();

    const script = expectSingleScript(render(structuredData));
    await brokenImageSettled();

    expect(flags.nhJsonLdFired).toBeUndefined();
    expect(JSON.parse(script.textContent ?? '')['@graph'][0].review.reviewBody).toBe(breakout('</SCRIPT>'));
  });

  it('writes the JSON without a raw < or line and paragraph separators, and it still parses', () => {
    const value = {name: 'a < b', description: 'line\u2028paragraph\u2029end', note: '<b>bold</b> & more'};

    const script = expectSingleScript(render(value));
    const text = script.textContent ?? '';

    expect(text).not.toContain('<');
    expect(text).not.toContain('\u2028');
    expect(text).not.toContain('\u2029');
    expect(JSON.parse(text)).toEqual(value);
  });

  it('renders an empty script element without data', () => {
    const script = expectSingleScript(render(null));

    expect(script.textContent).toBe('');
  });
});
