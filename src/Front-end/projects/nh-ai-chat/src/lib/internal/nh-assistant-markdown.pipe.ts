import { Pipe, PipeTransform, inject } from '@angular/core';
import { NhAssistantMarkdownRenderer } from './nh-assistant-markdown';

/**
 * Renders model text into sanitized HTML. Impure, so text that rendered as the escaped fallback
 * renders again once the Markdown libraries have loaded; it keeps its last result, so unchanged
 * text is not rendered again.
 */
@Pipe({ name: 'nhAssistantMarkdown', standalone: true, pure: false })
export class NhAssistantMarkdownPipe implements PipeTransform {
  private readonly renderer = inject(NhAssistantMarkdownRenderer);
  private lastText: string | null = null;
  private lastReady = false;
  private lastHtml = '';

  transform(text: string): string {
    const ready = this.renderer.isReady();
    if (text === this.lastText && ready === this.lastReady) {
      return this.lastHtml;
    }

    this.lastHtml = this.renderer.render(text);
    this.lastText = text;
    this.lastReady = ready;
    return this.lastHtml;
  }
}
