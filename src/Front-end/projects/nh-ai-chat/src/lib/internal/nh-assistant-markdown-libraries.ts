import { DOCUMENT } from '@angular/common';
import { Injectable, PendingTasks, inject, signal } from '@angular/core';
import type createDOMPurify from 'dompurify';
import type { Marked } from 'marked';

/** The Markdown parser and the sanitizer the assistant renders model text and documents with. */
export interface NhAssistantMarkdownLibrarySet {
  readonly Marked: typeof Marked;
  readonly createDOMPurify: typeof createDOMPurify;
}

/**
 * Loads `marked` and DOMPurify on the first Markdown rendering, so an application ships them in a
 * lazy chunk instead of its initial bundle. Until they have arrived the renderers show the text
 * escaped, as they do on the server. `get()` reads a signal, so a template, pipe or `computed`
 * that rendered that fallback renders again once the libraries are there. The load is a pending
 * task of the application, so `ApplicationRef.whenStable()` and a test's `fixture.whenStable()`
 * wait for it.
 */
@Injectable({ providedIn: 'root' })
export class NhAssistantMarkdownLibraries {
  private readonly browser = inject(DOCUMENT).defaultView !== null;
  private readonly pendingTasks = inject(PendingTasks);
  private readonly libraries = signal<NhAssistantMarkdownLibrarySet | null>(null);
  private loading: Promise<NhAssistantMarkdownLibrarySet> | null = null;

  /** The libraries once they have loaded, otherwise `null`; the first call starts loading them. */
  get(): NhAssistantMarkdownLibrarySet | null {
    const libraries = this.libraries();
    if (!libraries && this.browser) {
      // A failed load leaves the fallback in place; the next rendering tries again.
      void this.load().catch(() => undefined);
    }

    return libraries;
  }

  /** Loads the libraries, or returns the load that is already under way. */
  load(): Promise<NhAssistantMarkdownLibrarySet> {
    if (this.loading) {
      return this.loading;
    }

    const removePendingTask = this.pendingTasks.add();
    const loading = Promise.all([import('marked'), import('dompurify')]).then(
      ([marked, dompurify]) => {
        const libraries: NhAssistantMarkdownLibrarySet = {
          Marked: marked.Marked,
          createDOMPurify: dompurify.default
        };
        this.libraries.set(libraries);
        return libraries;
      },
      error => {
        this.loading = null;
        throw error;
      }
    );
    loading.then(removePendingTask, removePendingTask);

    this.loading = loading;
    return loading;
  }
}
