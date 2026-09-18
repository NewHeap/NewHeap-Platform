import { Pipe, PipeTransform, inject } from '@angular/core';
import { NhAssistantMarkdownRenderer } from './nh-assistant-markdown';

/** Renders model text into sanitized HTML. Pure, so unchanged text is not rendered again. */
@Pipe({ name: 'nhAssistantMarkdown', standalone: true })
export class NhAssistantMarkdownPipe implements PipeTransform {
  private readonly renderer = inject(NhAssistantMarkdownRenderer);

  transform(text: string): string {
    return this.renderer.render(text);
  }
}
