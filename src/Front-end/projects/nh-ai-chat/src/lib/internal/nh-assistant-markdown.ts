import { DOCUMENT } from '@angular/common';
import { Injectable, inject } from '@angular/core';
import type { DOMPurify } from 'dompurify';
import type { Marked, Tokens } from 'marked';
import { NhAssistantMarkdownLibraries, NhAssistantMarkdownLibrarySet } from './nh-assistant-markdown-libraries';

/** The tags model text may render as; shared with `nh-assistant-document`. */
export const nhAssistantMarkdownTags: readonly string[] = [
  'p', 'br', 'strong', 'em', 'del', 'code', 'pre', 'blockquote', 'ul', 'ol', 'li', 'a',
  'h1', 'h2', 'h3', 'h4', 'h5', 'h6', 'hr', 'table', 'thead', 'tbody', 'tr', 'th', 'td'
];
export const nhAssistantMarkdownAttributes: readonly string[] = ['href', 'title', 'start', 'align'];
/**
 * The allowed attributes that hold plain values rather than URIs: an ordered list's `start` and
 * a table cell's `align`. DOMPurify checks every attribute outside its URI-safe list against
 * `ALLOWED_URI_REGEXP`, which would drop `start="3"` and `align="center"`.
 */
export const nhAssistantMarkdownUriSafeAttributes: readonly string[] = ['start', 'align'];
const allowedUris = /^(?:https?:|mailto:|\/(?!\/)|#)/i;

export function nhAssistantEscapeHtml(text: string): string {
  return text
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

/**
 * Renders model text as Markdown into a strict HTML subset. Inline HTML in the model text
 * is shown as literal text, images render as their alt text, and the result is sanitized
 * with DOMPurify. Links open in a new tab with `rel="noopener noreferrer"`. Until `marked` and
 * DOMPurify have loaded, and on the server, the text shows escaped in one paragraph.
 */
@Injectable({ providedIn: 'root' })
export class NhAssistantMarkdownRenderer {
  private readonly document = inject(DOCUMENT);
  private readonly libraries = inject(NhAssistantMarkdownLibraries);
  private marked: Marked | null = null;
  private purifier: DOMPurify | null = null;

  /** Whether the next `render` gives Markdown rather than the escaped fallback; reads a signal. */
  isReady(): boolean {
    return this.libraries.get() !== null;
  }

  render(text: string): string {
    const libraries = this.libraries.get();
    const purifier = libraries ? this.getPurifier(libraries) : null;
    if (!libraries || !purifier) {
      return `<p>${nhAssistantEscapeHtml(text)}</p>`;
    }

    this.marked ??= this.createMarked(libraries);
    const html = this.marked.parse(text, { async: false });
    return purifier.sanitize(html, {
      ALLOWED_TAGS: [...nhAssistantMarkdownTags],
      ALLOWED_ATTR: [...nhAssistantMarkdownAttributes],
      ADD_URI_SAFE_ATTR: [...nhAssistantMarkdownUriSafeAttributes],
      ALLOWED_URI_REGEXP: allowedUris,
      ALLOW_DATA_ATTR: false,
      ALLOW_ARIA_ATTR: false
    });
  }

  private createMarked(libraries: NhAssistantMarkdownLibrarySet): Marked {
    return new libraries.Marked({
      gfm: true,
      breaks: true,
      async: false,
      renderer: {
        html(token: Tokens.HTML | Tokens.Tag): string {
          return nhAssistantEscapeHtml(token.text);
        },
        image(token: Tokens.Image): string {
          return nhAssistantEscapeHtml(token.text);
        }
      }
    });
  }

  private getPurifier(libraries: NhAssistantMarkdownLibrarySet): DOMPurify | null {
    if (this.purifier) {
      return this.purifier;
    }

    const view = this.document.defaultView;
    if (!view) {
      return null;
    }

    const purifier = libraries.createDOMPurify(view);
    if (!purifier.isSupported) {
      return null;
    }

    purifier.addHook('afterSanitizeAttributes', node => {
      if (node.tagName === 'A') {
        node.setAttribute('target', '_blank');
        node.setAttribute('rel', 'noopener noreferrer');
      }
    });

    this.purifier = purifier;
    return purifier;
  }
}
