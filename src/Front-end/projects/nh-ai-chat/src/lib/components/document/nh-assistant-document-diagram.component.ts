import { isPlatformBrowser } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  PLATFORM_ID,
  effect,
  inject,
  input,
  signal,
  untracked,
  viewChild
} from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { NhAssistantDocumentMarkdownRenderer, nhAssistantDiagramReason } from '../../internal/nh-assistant-document-markdown';
import { NhAssistantDiagramRenderer } from '../../models/assistant-document.models';

type DiagramState =
  | { readonly kind: 'source' }
  | { readonly kind: 'drawing' }
  | { readonly kind: 'drawn' }
  | { readonly kind: 'failed'; readonly reason: string; readonly reasonKey: string | null };

/** Sizes the drawing inside its own shadow root, where the diagram's styles stay contained. */
const canvasStyles = ':host { display: block; } svg { display: block; max-width: 100%; height: auto; margin: 0 auto; }';

/**
 * One diagram of a document: the source while it is drawn or when no renderer draws it, the
 * sanitized SVG once drawn, and the source with the reason when it cannot be drawn. The SVG
 * goes into a shadow root, so its styles never reach the page. Internal to
 * `nh-assistant-document`.
 */
@Component({
  selector: 'nh-assistant-document-diagram',
  standalone: true,
  imports: [TranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <figure class="diagram" [attr.data-diagram-state]="state().kind" [attr.aria-busy]="state().kind === 'drawing'">
      <div #canvas class="canvas" [hidden]="state().kind !== 'drawn'"></div>
      @if (state().kind !== 'drawn') {
        <pre class="source"><code>{{ source() }}</code></pre>
      }
      @if (failure(); as failed) {
        <figcaption class="failure" role="note">
          {{ 'nh-assistant.document.diagram-failed' | translate: { reason: failed.reasonKey ? (failed.reasonKey | translate) : failed.reason } }}
        </figcaption>
      }
    </figure>
  `,
  styles: `
    :host { display: block; margin: 0 0 12px; }
    .diagram { margin: 0; }
    .canvas { overflow-x: auto; }
    .source {
      margin: 0;
      overflow-x: auto;
      border-radius: 8px;
      padding: 8px;
      background: var(--nha-surface-strong);
      font-family: var(--nha-mono);
      font-size: 12.5px;
    }
    .failure { margin-top: 6px; color: var(--nha-danger); font-size: 13px; }
  `
})
export class NhAssistantDocumentDiagramComponent {
  private readonly markdown = inject(NhAssistantDocumentMarkdownRenderer);
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));
  private readonly canvas = viewChild.required<ElementRef<HTMLElement>>('canvas');
  private generation = 0;

  /** The block's text. */
  readonly source = input.required<string>();
  /** The block's language in lower case. */
  readonly language = input.required<string>();
  readonly renderer = input<NhAssistantDiagramRenderer | null>(null);

  readonly state = signal<DiagramState>({ kind: 'source' });

  failure(): Extract<DiagramState, { kind: 'failed' }> | null {
    const state = this.state();
    return state.kind === 'failed' ? state : null;
  }

  constructor() {
    effect(() => {
      const source = this.source();
      const language = this.language();
      const renderer = this.renderer();
      untracked(() => {
        void this.draw(source, language, renderer);
      });
    });
  }

  private async draw(source: string, language: string, renderer: NhAssistantDiagramRenderer | null): Promise<void> {
    const generation = ++this.generation;
    if (!this.browser || !renderer) {
      this.state.set({ kind: 'source' });
      return;
    }

    this.state.set({ kind: 'drawing' });
    let svg: unknown;
    try {
      svg = await renderer.render(source, language);
    } catch (error) {
      if (generation === this.generation) {
        const reason = nhAssistantDiagramReason(error);
        this.state.set({ kind: 'failed', reason, reasonKey: reason ? null : 'nh-assistant.document.diagram-no-reason' });
      }
      return;
    }

    if (generation !== this.generation) {
      return;
    }

    const drawing = typeof svg === 'string' ? this.markdown.sanitizeDiagram(svg) : null;
    if (!drawing) {
      this.state.set({ kind: 'failed', reason: '', reasonKey: 'nh-assistant.document.diagram-not-svg' });
      return;
    }

    this.paint(drawing);
    this.state.set({ kind: 'drawn' });
  }

  private paint(drawing: SVGSVGElement): void {
    const canvas = this.canvas().nativeElement;
    const shadow = canvas.shadowRoot ?? canvas.attachShadow({ mode: 'open' });
    const style = canvas.ownerDocument.createElement('style');
    style.textContent = canvasStyles;
    shadow.replaceChildren(style, drawing);
  }
}
