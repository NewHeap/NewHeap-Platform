import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';
import { NH_ASSISTANT_TRANSLATIONS } from '../../i18n/nh-assistant-translations';
import {
  NhAssistantDiagramRenderer,
  NhAssistantDocumentHeading,
  NhAssistantDocumentLinkEvent
} from '../../models/assistant-document.models';
import { NhAssistantDocumentComponent } from './nh-assistant-document.component';

@Component({
  standalone: true,
  imports: [NhAssistantDocumentComponent],
  template: `
    <nh-assistant-document
      [markdown]="markdown()"
      [path]="path()"
      [siblingPaths]="siblings()"
      [diagramRenderer]="renderer()"
      (linkClick)="onLink($event)"
      (headingsChange)="headings.set($event)" />
  `
})
class DocumentHost {
  readonly markdown = signal('');
  readonly path = signal<string | null>('guides/setup.md');
  readonly siblings = signal<readonly string[]>(['guides/setup.md', 'guides/deploy.md', 'README.md', 'reference/api.md']);
  readonly renderer = signal<NhAssistantDiagramRenderer | null>(null);
  readonly headings = signal<readonly NhAssistantDocumentHeading[]>([]);
  readonly links: NhAssistantDocumentLinkEvent[] = [];

  onLink(link: NhAssistantDocumentLinkEvent): void {
    this.links.push(link);
    // The test never leaves the page: the application decides about external links.
    if (link.kind === 'external') {
      link.event.preventDefault();
    }
  }
}

function flush(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve));
}

async function settle(fixture: ComponentFixture<unknown>, rounds = 4): Promise<HTMLElement> {
  for (let round = 0; round < rounds; round++) {
    fixture.detectChanges();
    await flush();
  }
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

function text(element: Element | null | undefined): string {
  return element?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

function hrefs(element: HTMLElement): (string | null)[] {
  return Array.from(element.querySelectorAll('a')).map(anchor => anchor.getAttribute('href'));
}

describe('NhAssistantDocumentComponent', () => {
  let fixture: ComponentFixture<DocumentHost>;
  let host: DocumentHost;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      imports: [DocumentHost],
      providers: [provideTranslateService({ fallbackLang: 'en' })]
    });
    const translate = TestBed.inject(TranslateService);
    translate.setTranslation('en', NH_ASSISTANT_TRANSLATIONS.en, true);
    await firstValueFrom(translate.use('en'));
    fixture = TestBed.createComponent(DocumentHost);
    host = fixture.componentInstance;
  });

  afterEach(() => {
    delete (window as unknown as Record<string, unknown>)['__nhDocumentPwned'];
  });

  async function render(markdown: string): Promise<HTMLElement> {
    host.markdown.set(markdown);
    return settle(fixture);
  }

  it('shows raw HTML as text and runs none of it', async () => {
    const element = await render([
      'Intro <script>window.__nhDocumentPwned = true</script>',
      '',
      '<img src=x onerror="window.__nhDocumentPwned = true">',
      '',
      '<div onclick="window.__nhDocumentPwned = true">block</div>'
    ].join('\n'));

    expect(element.querySelector('script, img, iframe, div[onclick]')).toBeNull();
    expect(element.querySelectorAll('[onerror], [onclick], [src]').length).toBe(0);
    expect(text(element)).toContain('<script>window.__nhDocumentPwned = true</script>');
    expect(text(element)).toContain('<img src=x onerror="window.__nhDocumentPwned = true">');
    expect((window as unknown as Record<string, unknown>)['__nhDocumentPwned']).toBeUndefined();
  });

  it('refuses executable and unknown URLs and keeps https and mailto links', async () => {
    const element = await render([
      '[run](javascript:alert(1)) [page](data:text/html;base64,PHNjcmlwdD4=) [basic](vbscript:msgbox)',
      '[disk](file:///etc/passwd) [other host](//evil.example/x) [encoded](javascript&#58;alert(1))',
      '[docs](https://example.com/docs) [mail](mailto:team@example.com) ![pixel](https://tracker.example/p.png)'
    ].join('\n'));

    expect(hrefs(element)).toEqual(['https://example.com/docs', 'mailto:team@example.com']);
    for (const label of ['run', 'page', 'basic', 'disk', 'other host', 'encoded', 'pixel']) {
      expect(text(element)).toContain(label);
    }
    expect(element.querySelector('img')).toBeNull();

    const external = element.querySelector('a[href^="https:"]')!;
    expect(external.getAttribute('target')).toBe('_blank');
    expect(external.getAttribute('rel')).toBe('noopener noreferrer');
  });

  it('resolves relative links within the bundle and reports them instead of navigating', async () => {
    const element = await render([
      '## Install',
      '',
      '[deploy](deploy.md#rollback) [readme](../README.md) [api](/reference/api.md) [self](setup.md#install)',
      '[missing](missing.md) [outside](../../etc/passwd) [section](#install) [query](deploy.md?plain=1)'
    ].join('\n'));

    expect(hrefs(element)).toEqual([
      'guides/deploy.md#rollback',
      'README.md',
      'reference/api.md',
      '#install',
      '#install',
      'guides/deploy.md'
    ]);
    expect(text(element)).toContain('missing');
    expect(text(element)).toContain('outside');

    const deploy = element.querySelector<HTMLAnchorElement>('a[href="guides/deploy.md#rollback"]')!;
    const click = new MouseEvent('click', { bubbles: true, cancelable: true });
    deploy.dispatchEvent(click);
    expect(click.defaultPrevented).toBeTrue();
    expect(host.links.at(-1)).toEqual(jasmine.objectContaining({ kind: 'document', path: 'guides/deploy.md', fragment: 'rollback' }));

    // A fragment of the document scrolls to its heading and focuses it.
    element.querySelectorAll<HTMLAnchorElement>('a[href="#install"]')[1].click();
    expect(host.links.at(-1)).toEqual(jasmine.objectContaining({ kind: 'anchor', path: null, fragment: 'install' }));
    expect(document.activeElement?.tagName).toBe('H2');
    expect(text(document.activeElement)).toBe('Install');

    element.querySelector<HTMLAnchorElement>('a[href="README.md"]')!.click();
    expect(host.links.at(-1)).toEqual(jasmine.objectContaining({ kind: 'document', path: 'README.md', fragment: null }));
  });

  it('reports external links and lets the application keep them from opening', async () => {
    const element = await render('[docs](https://example.com/docs)');
    const click = new MouseEvent('click', { bubbles: true, cancelable: true });

    element.querySelector('a')!.dispatchEvent(click);

    expect(host.links).toEqual([jasmine.objectContaining({ kind: 'external', href: 'https://example.com/docs', path: null })]);
    expect(click.defaultPrevented).toBeTrue();
  });

  it('reports its headings with unique ids and fragments once they are in the page', async () => {
    const element = await render('# Setup\n\n## Install\n\n## Install\n\n### Notes *now*\n\nText.');
    const headings = host.headings();

    expect(headings.map(heading => [heading.level, heading.fragment, heading.text])).toEqual([
      [1, 'setup', 'Setup'],
      [2, 'install', 'Install'],
      [2, 'install-1', 'Install'],
      [3, 'notes-now', 'Notes now']
    ]);
    for (const heading of headings) {
      expect(heading.id).toMatch(/^nh-document-\d+-/);
      expect(element.querySelector(`[id="${heading.id}"]`)?.tagName).toBe(`H${heading.level}`);
    }
  });

  it('keeps fenced blocks as code without a diagram renderer', async () => {
    const element = await render('```mermaid\ngraph TD; A-->B\n```');

    expect(element.querySelector('nh-assistant-document-diagram')).toBeNull();
    expect(text(element.querySelector('pre code'))).toBe('graph TD; A-->B');
  });

  it('calls the diagram renderer for its languages and shows only sanitized SVG', async () => {
    const svg = [
      '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 40 10">',
      '<rect id="box" width="10" height="10" onclick="window.__nhDocumentPwned = true"/>',
      '<script>window.__nhDocumentPwned = true</script>',
      '<foreignObject width="10" height="10"><div>html</div></foreignObject>',
      '<image href="https://tracker.example/p.png" width="5" height="5"/>',
      '<a href="javascript:alert(1)"><text>link</text></a>',
      '<style>@import url(https://evil.example/x.css); rect { fill: red; }</style>',
      '<style>rect { stroke: blue; }</style>',
      '<rect id="remote" fill="url(https://evil.example/p.svg#x)" width="1" height="1"/>',
      '<rect id="local" fill="url(#box)" width="2" height="2"/>',
      '</svg>'
    ].join('');
    const draw = jasmine.createSpy('draw').and.resolveTo(svg);
    host.renderer.set({ languages: ['flow'], render: draw });

    const element = await render('Before.\n\n```FLOW\nA -> B\n```\n\nAfter.');

    expect(draw).toHaveBeenCalledOnceWith('A -> B', 'flow');
    const diagram = element.querySelector('nh-assistant-document-diagram figure')!;
    expect(diagram.getAttribute('data-diagram-state')).toBe('drawn');
    expect(diagram.querySelector('pre')).toBeNull();
    const shadow = diagram.querySelector('.canvas')!.shadowRoot!;
    const drawing = shadow.querySelector('svg')!;
    expect(drawing).not.toBeNull();
    expect(drawing.querySelector('script, foreignObject, image, a')).toBeNull();
    expect(drawing.querySelectorAll('[onclick]').length).toBe(0);
    const styles = Array.from(drawing.querySelectorAll('style')).map(style => style.textContent);
    expect(styles).toEqual(['', 'rect { stroke: blue; }']);
    expect(drawing.querySelector('#remote')!.hasAttribute('fill')).toBeFalse();
    expect(drawing.querySelector('#local')!.getAttribute('fill')).toBe('url(#box)');
    // Its styles stay in the shadow root, away from the page.
    expect(element.querySelector('style')).toBeNull();
    expect(text(element)).toContain('Before.');
    expect(text(element)).toContain('After.');
    expect((window as unknown as Record<string, unknown>)['__nhDocumentPwned']).toBeUndefined();
  });

  it('shows a diagram it cannot draw as its source with the reason', async () => {
    host.renderer.set({
      languages: ['flow'],
      render: source => source.includes('->')
        ? Promise.resolve('<div>not a drawing</div>')
        : Promise.reject(new Error('Line 1: expected an arrow such as A -> B.'))
    });

    const element = await render('```flow\nA B\n```\n\n```flow\nA -> B\n```');
    const [broken, notSvg] = Array.from(element.querySelectorAll('nh-assistant-document-diagram figure'));

    expect(broken.getAttribute('data-diagram-state')).toBe('failed');
    expect(text(broken.querySelector('pre code'))).toBe('A B');
    expect(text(broken.querySelector('figcaption'))).toBe('This diagram could not be drawn: Line 1: expected an arrow such as A -> B.');
    expect(broken.querySelector('figcaption')?.getAttribute('role')).toBe('note');
    expect(text(notSvg.querySelector('pre code'))).toBe('A -> B');
    expect(text(notSvg.querySelector('figcaption'))).toBe('This diagram could not be drawn: the renderer returned no SVG.');
  });
});
