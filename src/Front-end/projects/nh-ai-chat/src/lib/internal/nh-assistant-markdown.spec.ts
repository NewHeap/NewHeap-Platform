import { TestBed } from '@angular/core/testing';
import { NhAssistantMarkdownRenderer } from './nh-assistant-markdown';

describe('NhAssistantMarkdownRenderer', () => {
  let renderer: NhAssistantMarkdownRenderer;

  function toElement(html: string): HTMLElement {
    const element = document.createElement('div');
    element.innerHTML = html;
    return element;
  }

  beforeEach(() => {
    renderer = TestBed.inject(NhAssistantMarkdownRenderer);
  });

  it('renders basic Markdown', () => {
    const element = toElement(renderer.render('**Bold** and `code`\n\n- one\n- two'));

    expect(element.querySelector('strong')?.textContent).toBe('Bold');
    expect(element.querySelector('code')?.textContent).toBe('code');
    expect(element.querySelectorAll('li').length).toBe(2);
  });

  it('never renders a script tag from model text', () => {
    const html = renderer.render('Hello <script>window.__nhAssistantPwned = true</script>');
    const element = toElement(html);

    expect(element.querySelector('script')).toBeNull();
    expect(element.textContent).toContain('<script>');
    expect((window as unknown as Record<string, unknown>)['__nhAssistantPwned']).toBeUndefined();
  });

  it('shows inline HTML as text instead of markup', () => {
    const element = toElement(renderer.render('<img src=x onerror="alert(1)"> <b onclick="x()">b</b> <iframe src="https://example.com"></iframe>'));

    expect(element.querySelector('img, b, iframe')).toBeNull();
    expect(element.querySelectorAll('[onerror], [onclick], [src]').length).toBe(0);
    expect(element.textContent).toContain('<img src=x onerror="alert(1)">');
  });

  it('drops javascript and data URLs from links and never renders images', () => {
    const element = toElement(renderer.render('[click](javascript:alert(1)) [data](data:text/html;base64,PHNjcmlwdD4=) ![pixel](https://tracker.example/p.png)'));

    for (const anchor of Array.from(element.querySelectorAll('a'))) {
      expect(anchor.getAttribute('href')).toBeNull();
    }
    expect(element.querySelector('img')).toBeNull();
    expect(element.textContent).toContain('pixel');
  });

  it('opens safe links in a new tab with rel="noopener noreferrer"', () => {
    const anchor = toElement(renderer.render('[docs](https://example.com/docs)')).querySelector('a');

    expect(anchor?.getAttribute('href')).toBe('https://example.com/docs');
    expect(anchor?.getAttribute('target')).toBe('_blank');
    expect(anchor?.getAttribute('rel')).toBe('noopener noreferrer');
  });
});
