import { DOCUMENT } from '@angular/common';
import { Injectable, inject } from '@angular/core';
import createDOMPurify, { DOMPurify } from 'dompurify';
import { Marked, Renderer, Token, Tokens, TokensList } from 'marked';
import { NhAssistantDocumentHeading } from '../models/assistant-document.models';
import { nhAssistantEscapeHtml, nhAssistantMarkdownAttributes, nhAssistantMarkdownTags } from './nh-assistant-markdown';

/** Links that leave the bundle: only these schemes, and they open in a new tab. */
export const nhAssistantExternalLinkPattern = /^(?:https?:|mailto:)/i;
const externalProtocols = new Set(['http:', 'https:', 'mailto:']);
const schemePattern = /^[a-z][a-z0-9+.-]*:/i;
const controlCharacters = /[\u0000-\u001f\u007f]/;
const maxDiagramReason = 300;

/** The options of one rendering of a document. */
export interface NhAssistantDocumentRenderOptions {
  /** The document's own path in its bundle; relative links resolve from its folder. */
  readonly path: string | null;
  /** The other documents of the bundle that links may lead to. */
  readonly siblingPaths: readonly string[];
  /** The fenced code languages that are diagrams, in lower case. */
  readonly diagramLanguages: readonly string[];
  /** Prefixes every heading id, so two documents on one page never share an id. */
  readonly idPrefix: string;
}

export type NhAssistantDocumentSegment =
  | { readonly kind: 'html'; readonly html: string }
  | { readonly kind: 'diagram'; readonly language: string; readonly source: string };

export interface NhAssistantRenderedDocument {
  readonly segments: readonly NhAssistantDocumentSegment[];
  readonly headings: readonly NhAssistantDocumentHeading[];
}

function decodeOrNull(value: string, decode: (text: string) => string): string | null {
  try {
    return decode(value);
  } catch {
    return null;
  }
}

/**
 * The normalized form of a bundle path: forward slashes only, without `.` and `..` segments and
 * without a leading slash. `null` for a path that names a scheme, escapes the bundle's root or
 * holds a backslash or control character.
 */
export function nhAssistantBundlePath(path: string): string | null {
  const trimmed = path.trim();
  if (!trimmed || trimmed.includes('\\') || controlCharacters.test(trimmed) || schemePattern.test(trimmed) || trimmed.startsWith('//')) {
    return null;
  }

  const segments: string[] = [];
  for (const segment of trimmed.split('/')) {
    if (segment === '' || segment === '.') {
      continue;
    }

    if (segment === '..') {
      if (segments.length === 0) {
        return null;
      }
      segments.pop();
      continue;
    }

    segments.push(segment);
  }

  return segments.length > 0 ? segments.join('/') : null;
}

/**
 * Resolves a relative link of the document at `base` within its bundle: from the document's
 * folder, or from the bundle's root when it starts with `/`. The query is ignored.
 */
export function nhAssistantResolveBundleLink(base: string | null, href: string): { path: string; fragment: string | null } | null {
  const hashIndex = href.indexOf('#');
  const target = hashIndex >= 0 ? href.slice(0, hashIndex) : href;
  const fragment = hashIndex >= 0 ? decodeOrNull(href.slice(hashIndex + 1), decodeURIComponent) : null;
  const pathPart = decodeOrNull(target.split('?')[0], decodeURI);
  if (pathPart === null || pathPart.trim() === '' || (hashIndex >= 0 && fragment === null)) {
    return null;
  }

  const folder = base ? base.split('/').slice(0, -1).join('/') : '';
  const joined = pathPart.startsWith('/') || !folder ? pathPart : `${folder}/${pathPart}`;
  const path = schemePattern.test(pathPart) || pathPart.startsWith('//') ? null : nhAssistantBundlePath(joined);
  return path ? { path, fragment: fragment || null } : null;
}

/** The fragment of a heading: lower case, punctuation removed, spaces as dashes. */
function slugOf(text: string): string {
  const slug = text
    .toLowerCase()
    .trim()
    .replace(/[^\p{L}\p{N}\s_-]/gu, '')
    .replace(/\s+/g, '-');
  return slug || 'section';
}

/** The state of one rendering, which the Markdown renderer and the sanitizer hooks share. */
class DocumentRendering {
  readonly headings: NhAssistantDocumentHeading[] = [];
  /** The bundle hrefs this rendering wrote, the only relative hrefs the sanitizer keeps. */
  readonly bundleHrefs = new Set<string>();
  private readonly siblings: Set<string>;
  private readonly ownPath: string | null;
  private readonly slugs = new Map<string, number>();

  constructor(readonly options: NhAssistantDocumentRenderOptions) {
    this.ownPath = options.path ? nhAssistantBundlePath(options.path) : null;
    this.siblings = new Set(options.siblingPaths
      .map(path => nhAssistantBundlePath(path))
      .filter((path): path is string => !!path));
  }

  addHeading(text: string, level: number): NhAssistantDocumentHeading {
    const base = slugOf(text);
    const seen = this.slugs.get(base) ?? 0;
    this.slugs.set(base, seen + 1);
    const fragment = seen === 0 ? base : `${base}-${seen}`;
    const heading = { id: `${this.options.idPrefix}${fragment}`, fragment, level, text };
    this.headings.push(heading);
    return heading;
  }

  /**
   * The href to write for a link, or `null` to show only its text: a fragment of this
   * document, an `https:`, `http:` or `mailto:` address, or a sibling of the bundle. Every
   * other scheme, such as `javascript:`, `data:` or `file:`, and every path outside the bundle
   * is refused.
   */
  hrefOf(raw: string): string | null {
    const href = raw.trim();
    if (!href || controlCharacters.test(href)) {
      return null;
    }

    if (href.startsWith('#')) {
      return href.length > 1 ? href : null;
    }

    if (nhAssistantExternalLinkPattern.test(href)) {
      return this.externalHref(href);
    }

    const resolved = schemePattern.test(href) || href.startsWith('//') ? null : nhAssistantResolveBundleLink(this.ownPath, href);
    if (!resolved) {
      return null;
    }

    const fragment = resolved.fragment ? `#${encodeURIComponent(resolved.fragment)}` : '';
    if (resolved.path === this.ownPath) {
      return fragment || null;
    }

    if (!this.siblings.has(resolved.path)) {
      return null;
    }

    const bundleHref = `${encodeURI(resolved.path)}${fragment}`;
    this.bundleHrefs.add(bundleHref);
    return bundleHref;
  }

  private externalHref(href: string): string | null {
    try {
      const url = new URL(href);
      return externalProtocols.has(url.protocol) ? url.href : null;
    } catch {
      return null;
    }
  }
}

/** CSS that could run code or load something, which a diagram's styles may not contain. */
function isUnsafeCss(css: string): boolean {
  if (/\\|@import|@font-face|expression\s*\(|-moz-binding|behavior\s*:|image-set\s*\(|\bimage\s*\(|cross-fade\s*\(|\bsrc\s*\(/i.test(css)) {
    return true;
  }

  return hasExternalReference(css);
}

/** `url(...)` that names anything but a fragment of the diagram itself, such as `url(#arrow)`. */
function hasExternalReference(value: string): boolean {
  for (const match of value.matchAll(/url\s*\(\s*(['"]?)\s*([^)'"]*)/gi)) {
    if (!match[2].trim().startsWith('#')) {
      return true;
    }
  }

  return false;
}

/** The reason a renderer gave, as one short line; empty when it gave none. */
export function nhAssistantDiagramReason(error: unknown): string {
  const text = error instanceof Error ? error.message : typeof error === 'string' ? error : '';
  const line = text.replace(/\s+/g, ' ').trim();
  return line.length > maxDiagramReason ? `${line.slice(0, maxDiagramReason - 1)}…` : line;
}

/**
 * Renders untrusted Markdown documents of a bundle with the assistant's safety defaults: inline
 * HTML shows as text, images as their alt text, links only to fragments, `https:`, `http:`,
 * `mailto:` and sibling documents, and DOMPurify sanitizes the result. Top-level fenced code
 * blocks of a diagram language become diagram segments. Internal to `nh-assistant-document`.
 */
@Injectable({ providedIn: 'root' })
export class NhAssistantDocumentMarkdownRenderer {
  private readonly document = inject(DOCUMENT);
  private documentPurifier: DOMPurify | null = null;
  private diagramPurifier: DOMPurify | null = null;
  /** The rendering being sanitized; the purifier's hooks read it. */
  private current: DocumentRendering | null = null;

  render(markdown: string, options: NhAssistantDocumentRenderOptions): NhAssistantRenderedDocument {
    const rendering = new DocumentRendering(options);
    const purifier = this.getDocumentPurifier();
    if (!purifier) {
      return { segments: [{ kind: 'html', html: `<p>${nhAssistantEscapeHtml(markdown)}</p>` }], headings: [] };
    }

    const marked = this.createMarked(rendering);
    const tokens = marked.lexer(markdown);
    const diagramLanguages = new Set(options.diagramLanguages.map(language => language.toLowerCase()));
    const segments: NhAssistantDocumentSegment[] = [];
    let pending: Token[] = [];

    const flush = () => {
      if (pending.length === 0) {
        return;
      }

      const html = marked.parser(Object.assign(pending, { links: tokens.links }) as TokensList);
      segments.push({ kind: 'html', html: this.sanitize(purifier, rendering, html) });
      pending = [];
    };

    for (const token of tokens) {
      const language = token.type === 'code' ? (token as Tokens.Code).lang?.trim().split(/\s+/)[0]?.toLowerCase() : undefined;
      if (language && diagramLanguages.has(language)) {
        flush();
        segments.push({ kind: 'diagram', language, source: (token as Tokens.Code).text });
        continue;
      }

      pending.push(token);
    }
    flush();

    return { segments, headings: rendering.headings };
  }

  /**
   * Sanitizes a renderer's SVG: SVG and its filters only, without scripts, event handlers,
   * links, images, `use`, foreign content, external references or CSS that could load or run
   * anything. `null` when the result is not one SVG element.
   */
  sanitizeDiagram(svg: string): SVGSVGElement | null {
    const purifier = this.getDiagramPurifier();
    if (!purifier) {
      return null;
    }

    const fragment = purifier.sanitize(svg, {
      USE_PROFILES: { svg: true, svgFilters: true },
      FORBID_TAGS: ['a', 'image', 'use', 'feImage', 'foreignObject', 'script', 'iframe'],
      FORBID_ATTR: ['href', 'xlink:href'],
      ALLOW_DATA_ATTR: false,
      RETURN_DOM_FRAGMENT: true
    });
    const elements = Array.from(fragment.children);
    const root = elements[0];
    if (elements.length !== 1 || root.namespaceURI !== 'http://www.w3.org/2000/svg' || root.localName !== 'svg') {
      return null;
    }

    return root as SVGSVGElement;
  }

  private createMarked(rendering: DocumentRendering): Marked {
    return new Marked({
      gfm: true,
      breaks: false,
      async: false,
      renderer: {
        html(token: Tokens.HTML | Tokens.Tag): string {
          return nhAssistantEscapeHtml(token.text);
        },
        image(token: Tokens.Image): string {
          return nhAssistantEscapeHtml(token.text);
        },
        heading(this: Renderer, token: Tokens.Heading): string {
          const text = this.parser.parseInline(token.tokens, this.parser.textRenderer);
          const heading = rendering.addHeading(text, token.depth);
          return `<h${token.depth} id="${nhAssistantEscapeHtml(heading.id)}">${this.parser.parseInline(token.tokens)}</h${token.depth}>\n`;
        },
        link(this: Renderer, token: Tokens.Link): string {
          const label = this.parser.parseInline(token.tokens);
          const href = rendering.hrefOf(token.href);
          if (!href) {
            return label;
          }

          const title = token.title ? ` title="${nhAssistantEscapeHtml(token.title)}"` : '';
          return `<a href="${nhAssistantEscapeHtml(href)}"${title}>${label}</a>`;
        }
      }
    });
  }

  private sanitize(purifier: DOMPurify, rendering: DocumentRendering, html: string): string {
    this.current = rendering;
    try {
      return purifier.sanitize(html, {
        ALLOWED_TAGS: [...nhAssistantMarkdownTags],
        ALLOWED_ATTR: [...nhAssistantMarkdownAttributes, 'id'],
        ALLOWED_URI_REGEXP: /^(?:https?:|mailto:|#)/i,
        ALLOW_DATA_ATTR: false,
        ALLOW_ARIA_ATTR: false
      });
    } finally {
      this.current = null;
    }
  }

  private createPurifier(): DOMPurify | null {
    const view = this.document.defaultView;
    if (!view) {
      return null;
    }

    const purifier = createDOMPurify(view);
    return purifier.isSupported ? purifier : null;
  }

  private getDocumentPurifier(): DOMPurify | null {
    if (this.documentPurifier) {
      return this.documentPurifier;
    }

    const purifier = this.createPurifier();
    if (!purifier) {
      return null;
    }

    purifier.addHook('uponSanitizeAttribute', (_node, event) => {
      const rendering = this.current;
      if (event.attrName === 'href' && rendering?.bundleHrefs.has(event.attrValue)) {
        // A sibling document this rendering resolved itself; the URI pattern knows no paths.
        event.forceKeepAttr = true;
      } else if (event.attrName === 'id' && !(rendering && event.attrValue.startsWith(rendering.options.idPrefix))) {
        event.keepAttr = false;
      }
    });
    purifier.addHook('afterSanitizeAttributes', node => {
      const href = node.tagName === 'A' ? node.getAttribute('href') : null;
      if (href && nhAssistantExternalLinkPattern.test(href)) {
        node.setAttribute('target', '_blank');
        node.setAttribute('rel', 'noopener noreferrer');
      }
    });

    this.documentPurifier = purifier;
    return purifier;
  }

  private getDiagramPurifier(): DOMPurify | null {
    if (this.diagramPurifier) {
      return this.diagramPurifier;
    }

    const purifier = this.createPurifier();
    if (!purifier) {
      return null;
    }

    purifier.addHook('uponSanitizeElement', (node, event) => {
      if (event.tagName === 'style' && isUnsafeCss(node.textContent ?? '')) {
        node.textContent = '';
      }
    });
    purifier.addHook('uponSanitizeAttribute', (_node, event) => {
      const unsafe = event.attrName === 'style' ? isUnsafeCss(event.attrValue) : hasExternalReference(event.attrValue);
      if (unsafe) {
        event.keepAttr = false;
      }
    });

    this.diagramPurifier = purifier;
    return purifier;
  }
}
