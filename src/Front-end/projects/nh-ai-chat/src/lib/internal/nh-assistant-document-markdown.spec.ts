import { TestBed } from '@angular/core/testing';
import { NhAssistantDocumentMarkdownRenderer, NhAssistantDocumentRenderOptions } from './nh-assistant-document-markdown';
import { NhAssistantMarkdownLibraries } from './nh-assistant-markdown-libraries';

describe('NhAssistantDocumentMarkdownRenderer', () => {
  const options: NhAssistantDocumentRenderOptions = {
    path: 'guides/setup.md',
    siblingPaths: ['guides/setup.md', 'guides/deploy.md'],
    diagramLanguages: ['mermaid'],
    idPrefix: 'doc-'
  };
  let renderer: NhAssistantDocumentMarkdownRenderer;

  function toElement(markdown: string): HTMLElement {
    const element = document.createElement('div');
    element.innerHTML = renderer
      .render(markdown, options)
      .segments
      .map(segment => (segment.kind === 'html' ? segment.html : ''))
      .join('');
    return element;
  }

  beforeEach(async () => {
    await TestBed.inject(NhAssistantMarkdownLibraries).load();
    renderer = TestBed.inject(NhAssistantDocumentMarkdownRenderer);
  });

  it('keeps the start of an ordered list and the alignment of table columns', () => {
    const element = toElement([
      '3. three',
      '4. four',
      '',
      '| Left | Center | Right | Plain |',
      '| :--- | :---: | ---: | --- |',
      '| a | b | c | d |'
    ].join('\n'));

    expect(element.querySelector('ol')?.getAttribute('start')).toBe('3');
    expect(Array.from(element.querySelectorAll('th')).map(cell => cell.getAttribute('align'))).toEqual(['left', 'center', 'right', null]);
    expect(Array.from(element.querySelectorAll('td')).map(cell => cell.getAttribute('align'))).toEqual(['left', 'center', 'right', null]);
  });

  it('keeps no attribute outside its allow-list', () => {
    const element = toElement([
      '## Setup',
      '',
      '3. [deploy](deploy.md "Deploy") and [docs](https://example.com/docs)',
      '',
      '- [x] done',
      '',
      '```js',
      'const answer = 42;',
      '```',
      '',
      '| Left | Center |',
      '| :--- | :---: |',
      '| a | b |'
    ].join('\n'));

    const names = new Set(Array.from(element.querySelectorAll('*')).flatMap(node => node.getAttributeNames()));

    expect([...names].sort()).toEqual(['align', 'href', 'id', 'rel', 'start', 'target', 'title']);
    // Marked writes a language class and a task-list checkbox; neither survives.
    expect(element.querySelector('code')?.hasAttribute('class')).toBeFalse();
    expect(element.querySelector('input')).toBeNull();
  });
});
