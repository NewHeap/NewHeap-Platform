/**
 * Draws diagrams of fenced code blocks for `nh-assistant-document`, such as ```` ```mermaid ````.
 * The application supplies it and loads its drawing library lazily inside `render`, so a page
 * without diagrams never downloads it; the library itself depends on no diagram package.
 */
export interface NhAssistantDiagramRenderer {
  /**
   * The languages of the fenced code blocks it draws, compared without case, for example
   * `['mermaid']`. Blocks of other languages stay code.
   */
  readonly languages: readonly string[];

  /**
   * Draws one diagram as SVG markup. The document sanitizes the result before it shows it, so
   * scripts, event handlers, foreign content and external references never run or load.
   * Reject, or throw, with a short reason when the source cannot be drawn: the document then
   * shows the source with that reason.
   *
   * @param source The text of the block, untrusted like the rest of the document.
   * @param language The block's language in lower case.
   */
  render(source: string, language: string): Promise<string>;
}

/** A heading of the rendered document, in document order, for example for a table of contents. */
export interface NhAssistantDocumentHeading {
  /** The element id of the heading in the page, unique per document component. */
  readonly id: string;
  /** The fragment that links to it within the document, as in `[Setup](#setup)`. */
  readonly fragment: string;
  /** 1 to 6. */
  readonly level: number;
  /** The heading as plain text. */
  readonly text: string;
}

/**
 * What a link of the document leads to:
 * - `document`: another document of the bundle, one of the sibling paths;
 * - `anchor`: a heading of this document;
 * - `external`: an `https:`, `http:` or `mailto:` address, which opens in a new tab.
 */
export type NhAssistantDocumentLinkKind = 'document' | 'anchor' | 'external';

/**
 * A link the reader activated. For `document` and `anchor` links the document prevents the
 * browser's navigation: the application opens the sibling document, and the document scrolls
 * to its own heading. An `external` link opens unless the handler calls
 * `event.preventDefault()`.
 */
export interface NhAssistantDocumentLinkEvent {
  readonly kind: NhAssistantDocumentLinkKind;
  /** The address as rendered: the bundle path with its fragment, `#fragment` or the external URL. */
  readonly href: string;
  /** The sibling path for a `document` link, otherwise `null`. */
  readonly path: string | null;
  /** The fragment without `#`, or `null`. */
  readonly fragment: string | null;
  /** The click, so the handler can prevent an external link from opening. */
  readonly event: MouseEvent;
}
