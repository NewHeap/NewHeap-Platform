import { DOCUMENT } from '@angular/common';
import { Injectable, inject } from '@angular/core';
import createDOMPurify, { DOMPurify } from 'dompurify';
import { Marked, Tokens } from 'marked';

const allowedTags = [
  'p', 'br', 'strong', 'em', 'del', 'code', 'pre', 'blockquote', 'ul', 'ol', 'li', 'a',
  'h1', 'h2', 'h3', 'h4', 'h5', 'h6', 'hr', 'table', 'thead', 'tbody', 'tr', 'th', 'td'
];
const allowedAttributes = ['href', 'title', 'start', 'align'];
const allowedUris = /^(?:https?:|mailto:|\/(?!\/)|#)/i;

function escapeHtml(text: string): string {
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
 * with DOMPurify. Links open in a new tab with `rel="noopener noreferrer"`.
 */
@Injectable({ providedIn: 'root' })
export class NhAssistantMarkdownRenderer {
  private readonly document = inject(DOCUMENT);
  private readonly marked = new Marked({
    gfm: true,
    breaks: true,
    async: false,
    renderer: {
      html(token: Tokens.HTML | Tokens.Tag): string {
        return escapeHtml(token.text);
      },
      image(token: Tokens.Image): string {
        return escapeHtml(token.text);
      }
    }
  });
  private purifier: DOMPurify | null = null;

  render(text: string): string {
    const purifier = this.getPurifier();
    if (!purifier) {
      return `<p>${escapeHtml(text)}</p>`;
    }

    const html = this.marked.parse(text, { async: false });
    return purifier.sanitize(html, {
      ALLOWED_TAGS: allowedTags,
      ALLOWED_ATTR: allowedAttributes,
      ALLOWED_URI_REGEXP: allowedUris,
      ALLOW_DATA_ATTR: false,
      ALLOW_ARIA_ATTR: false
    });
  }

  private getPurifier(): DOMPurify | null {
    if (this.purifier) {
      return this.purifier;
    }

    const view = this.document.defaultView;
    if (!view) {
      return null;
    }

    const purifier = createDOMPurify(view);
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
