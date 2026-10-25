import { Component, computed, signal, viewChild } from '@angular/core';
import { TranslateModule } from '@ngx-translate/core';
import {
  NhAssistantDocumentComponent,
  NhAssistantDocumentHeading,
  NhAssistantDocumentLinkEvent
} from '@newheap/platform-ai-chat';
import { PROJECT_HANDBOOK } from './project-handbook.documents';
import { sampleFlowDiagramRenderer } from './sample-flow-diagram-renderer';

/**
 * SPM-336: the project handbook, a bundle of Markdown documents, read with `nh-assistant-document`
 * of `@newheap/platform-ai-chat`. The documents are untrusted: inline HTML shows as text, and a
 * `javascript:` link or a path outside the bundle is no link. A link to another document of the
 * bundle resolves from the open document's folder against `siblingPaths`; the document reports it
 * through `linkClick` and the page opens that document, at the heading its fragment names. The
 * headings the document reports through `headingsChange` are the page's "On this page" list.
 * `flow` blocks are drawn by the portal's own renderer, which loads its drawing code only when a
 * document has one; a block it cannot draw shows its source with the reason.
 */
@Component({
  selector: 'app-project-handbook-page',
  standalone: true,
  imports: [TranslateModule, NhAssistantDocumentComponent],
  templateUrl: './project-handbook-page.component.html',
  styleUrl: './project-handbook-page.component.scss'
})
export class ProjectHandbookPageComponent {
  private readonly document = viewChild(NhAssistantDocumentComponent);
  /** The heading to show once the document that a link opened is in the page. */
  private pendingFragment: string | null = null;

  readonly documents = PROJECT_HANDBOOK;
  readonly paths = PROJECT_HANDBOOK.map(item => item.path);
  readonly diagramRenderer = sampleFlowDiagramRenderer;
  readonly selectedPath = signal(PROJECT_HANDBOOK[0].path);
  readonly selected = computed(() =>
    this.documents.find(item => item.path === this.selectedPath()) ?? this.documents[0]);
  readonly headings = signal<readonly NhAssistantDocumentHeading[]>([]);

  open(path: string, fragment: string | null = null): void {
    if (!this.paths.includes(path)) {
      return;
    }

    this.pendingFragment = fragment;
    if (path === this.selectedPath() && fragment) {
      this.showHeading(fragment);
      return;
    }

    this.selectedPath.set(path);
  }

  /** A link to another document of the bundle opens it here; external links open in a new tab. */
  onLink(link: NhAssistantDocumentLinkEvent): void {
    if (link.kind === 'document' && link.path) {
      this.open(link.path, link.fragment);
    }
  }

  onHeadings(headings: readonly NhAssistantDocumentHeading[]): void {
    this.headings.set(headings);
    if (this.pendingFragment) {
      this.showHeading(this.pendingFragment);
    }
  }

  showHeading(fragment: string): void {
    this.pendingFragment = null;
    this.document()?.scrollToHeading(fragment);
  }
}
