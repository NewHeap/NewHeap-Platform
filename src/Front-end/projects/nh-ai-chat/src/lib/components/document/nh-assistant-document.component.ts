import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  afterRenderEffect,
  computed,
  inject,
  input,
  output
} from '@angular/core';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import {
  NhAssistantDocumentMarkdownRenderer,
  nhAssistantExternalLinkPattern
} from '../../internal/nh-assistant-document-markdown';
import {
  NhAssistantDiagramRenderer,
  NhAssistantDocumentHeading,
  NhAssistantDocumentLinkEvent
} from '../../models/assistant-document.models';
import { NhAssistantDocumentDiagramComponent } from './nh-assistant-document-diagram.component';

let nextDocumentId = 0;

type DocumentPart =
  | { readonly kind: 'html'; readonly html: SafeHtml }
  | { readonly kind: 'diagram'; readonly language: string; readonly source: string };

function decoded(value: string, decode: (text: string) => string): string {
  try {
    return decode(value);
  } catch {
    return value;
  }
}

/**
 * Renders an untrusted Markdown document, for example one file of a skill or knowledge bundle,
 * with the assistant's safety defaults: inline HTML shows as text, images as their alt text, and
 * a link keeps its address only for a heading of the document, a sibling of the bundle or an
 * `https:`, `http:` or `mailto:` address; `javascript:`, `data:` and every other scheme show as
 * text. Relative links resolve from the document's `path` against `siblingPaths`.
 *
 * Fenced code blocks of the languages of `diagramRenderer` become diagrams: the application's
 * renderer draws them, the document sanitizes its SVG and shows it in a shadow root, and a block
 * it cannot draw shows its source with the reason. Without a renderer they stay code.
 *
 * `linkClick` reports every link the reader activates; `headingsChange` the headings once they
 * are in the page. The texts are `nh-assistant.document.*`, which `provideNhAssistant` merges;
 * an application without it starts `NhAssistantTranslationMerger` or translates the keys itself.
 */
@Component({
  selector: 'nh-assistant-document',
  standalone: true,
  imports: [NhAssistantDocumentDiagramComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-document.component.html',
  styleUrl: './nh-assistant-document.component.scss',
  host: {
    class: 'nh-assistant-document',
    '(click)': 'onClick($event)'
  }
})
export class NhAssistantDocumentComponent {
  private readonly markdownRenderer = inject(NhAssistantDocumentMarkdownRenderer);
  private readonly sanitizer = inject(DomSanitizer);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly idPrefix = `nh-document-${++nextDocumentId}-`;

  /** The document's Markdown. Treated as untrusted. */
  readonly markdown = input.required<string>();
  /** The document's own path in its bundle, for example `guides/setup.md`. */
  readonly path = input<string | null>(null);
  /** The paths of the other documents in the bundle that its links may open. */
  readonly siblingPaths = input<readonly string[]>([]);
  /** Draws the bundle's diagrams; the application loads its drawing library lazily. */
  readonly diagramRenderer = input<NhAssistantDiagramRenderer | null>(null);

  /** A link the reader activated; see `NhAssistantDocumentLinkEvent`. */
  readonly linkClick = output<NhAssistantDocumentLinkEvent>();
  /** The headings, in document order, each time they changed and are in the page. */
  readonly headingsChange = output<readonly NhAssistantDocumentHeading[]>();

  private readonly rendered = computed(() => this.markdownRenderer.render(this.markdown() ?? '', {
    path: this.path(),
    siblingPaths: this.siblingPaths() ?? [],
    diagramLanguages: this.diagramRenderer()?.languages ?? [],
    idPrefix: this.idPrefix
  }));

  /** The headings of the rendered document. */
  readonly headings = computed(() => this.rendered().headings);

  readonly parts = computed<readonly DocumentPart[]>(() => this.rendered().segments.map(segment => {
    if (segment.kind === 'diagram') {
      return segment;
    }

    // DOMPurify sanitized the HTML; Angular's own sanitizer would drop the heading ids.
    return { kind: 'html', html: this.sanitizer.bypassSecurityTrustHtml(segment.html) };
  }));

  constructor() {
    afterRenderEffect(() => {
      this.headingsChange.emit(this.headings());
    });
  }

  /**
   * Scrolls to a heading of the document by its fragment and focuses it; false when the
   * document has no such heading.
   */
  scrollToHeading(fragment: string): boolean {
    const heading = this.headings().find(item => item.fragment === fragment);
    const element = heading ? this.host.nativeElement.querySelector<HTMLElement>(`[id="${CSS.escape(heading.id)}"]`) : null;
    if (!element) {
      return false;
    }

    if (!element.hasAttribute('tabindex')) {
      element.setAttribute('tabindex', '-1');
    }
    element.scrollIntoView({ block: 'start' });
    element.focus({ preventScroll: true });
    return true;
  }

  onClick(event: MouseEvent): void {
    const target = event.target instanceof Element ? event.target : null;
    const anchor = target?.closest('a[href]');
    if (!anchor || !this.host.nativeElement.contains(anchor)) {
      return;
    }

    const href = anchor.getAttribute('href') ?? '';
    if (href.startsWith('#')) {
      const fragment = decoded(href.slice(1), decodeURIComponent);
      event.preventDefault();
      this.scrollToHeading(fragment);
      this.linkClick.emit({ kind: 'anchor', href, path: null, fragment, event });
      return;
    }

    if (nhAssistantExternalLinkPattern.test(href)) {
      this.linkClick.emit({ kind: 'external', href, path: null, fragment: null, event });
      return;
    }

    // A sibling of the bundle: the application opens it, the browser does not navigate.
    event.preventDefault();
    const hashIndex = href.indexOf('#');
    const path = decoded(hashIndex >= 0 ? href.slice(0, hashIndex) : href, decodeURI);
    const fragment = hashIndex >= 0 ? decoded(href.slice(hashIndex + 1), decodeURIComponent) : null;
    this.linkClick.emit({ kind: 'document', href, path, fragment, event });
  }
}
