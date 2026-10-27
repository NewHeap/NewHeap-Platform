import {TestBed} from '@angular/core/testing';

import {NhMetaService} from './nh-meta.service';

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

describe('NhMetaService.stripHtml', () => {
  const flags = window as unknown as {nhStripFired?: boolean};
  let service: NhMetaService;

  beforeEach(() => {
    delete flags.nhStripFired;
    service = TestBed.inject(NhMetaService);
  });

  afterEach(() => {
    delete flags.nhStripFired;
  });

  it('does not run inline handlers of the markup it strips', async () => {
    const text = service.stripHtml(`<p>x</p><img src=${brokenImageSource} onerror=window.nhStripFired=true>`);
    await brokenImageSettled();

    expect(flags.nhStripFired).toBeUndefined();
    expect(text).toBe('x');
  });

  it('does not create the stripped elements in the application document', () => {
    const createElement = spyOn(document, 'createElement').and.callThrough();

    service.stripHtml('<p>Plan <strong>projects</strong></p>');

    expect(createElement).not.toHaveBeenCalled();
  });

  it('returns the text of the markup as before', () => {
    expect(service.stripHtml('<p>Plan <strong>projects</strong> &amp; tasks</p>')).toBe('Plan projects & tasks');
    expect(service.stripHtml('Plain text')).toBe('Plain text');
    expect(service.stripHtml('<title>Title</title><script>var a = 1;</script>Body')).toBe('Titlevar a = 1;Body');
    expect(service.stripHtml('<style>p { color: red; }</style><ul><li>One</li><li>Two</li></ul>')).toBe('p { color: red; }OneTwo');
    expect(service.stripHtml('<table><tr><td>Cell</td></tr></table>')).toBe('Cell');
    expect(service.stripHtml('a &lt;b&gt; c')).toBe('a <b> c');
    expect(service.stripHtml('')).toBe('');
    expect(service.stripHtml(null as unknown as string)).toBe('');
  });

  it('parses in an inert document of the application document where DOMParser is missing, as in a server render', async () => {
    const globals = window as unknown as {DOMParser?: typeof DOMParser};
    const domParser = globals.DOMParser;
    globals.DOMParser = undefined;

    try {
      const text = service.stripHtml(`<p>Plan <b>projects</b></p><img src=${brokenImageSource} onerror=window.nhStripFired=true>`);
      await brokenImageSettled();

      expect(flags.nhStripFired).toBeUndefined();
      expect(text).toBe('Plan projects');
    } finally {
      globals.DOMParser = domParser;
    }
  });
});
