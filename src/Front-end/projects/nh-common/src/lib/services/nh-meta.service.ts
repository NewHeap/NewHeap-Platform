import { Injectable, Renderer2, RendererFactory2 } from '@angular/core';
import { Meta, MetaDefinition } from '@angular/platform-browser';

@Injectable({
  providedIn: 'root'
})
export class NhMetaService {
  private _renderer: Renderer2;

  constructor(
    private angularMetaService: Meta,
    private _rendererFactory: RendererFactory2
  ) {
    this._renderer = this._rendererFactory.createRenderer(null, null);
  }

  addTag(tag: MetaDefinition, forceCreation?: boolean): HTMLMetaElement | null {
    NhMetaService.processMetaTags([tag]);
    return this.angularMetaService.addTag(tag, forceCreation);
  }
  addTags(tags: MetaDefinition[], forceCreation?: boolean): HTMLMetaElement[] {
    NhMetaService.processMetaTags(tags);
    return this.angularMetaService.addTags(tags, forceCreation);
  }
  getTag(attrSelector: string): HTMLMetaElement | null {
    return this.angularMetaService.getTag(attrSelector);
  }
  getTags(attrSelector: string): HTMLMetaElement[] {
    return this.angularMetaService.getTags(attrSelector);
  }
  updateTag(tag: MetaDefinition, selector?: string): HTMLMetaElement | null {
    NhMetaService.processMetaTags([tag]);
    return this.angularMetaService.updateTag(tag, selector);
  }

  removeTag(attrSelector: string): void {
    this.angularMetaService.removeTag(attrSelector);
  }

  removeTags(attrSelector: string): void {
    const tags = this.getTags(attrSelector);
    for (const tag of tags) {
      this.angularMetaService.removeTagElement(tag);
    }
  }

  removeTagElement(meta: HTMLMetaElement): void {
    this.angularMetaService.removeTagElement(meta);
  }

  stripHtml(html: string): string {
    const tmp = this._renderer.createElement('div');
    this._renderer.setProperty(tmp, 'innerHTML', html ?? '');

    return tmp.textContent ?? tmp.innerText ?? '';
  }

  private static processMetaTags(tags: MetaDefinition[], maxLength: number = 200, replacementText: string = '...') {
    for (const tag of tags) {
      if (tag.name?.trim().toLowerCase() === 'description'
        || tag.property?.trim().toLowerCase() === 'og:description') {
        if ((tag.content?.length ?? 0) > maxLength) {
          tag.content = tag.content!.slice(0, maxLength - replacementText.length) + replacementText;
        }
      }
    }
  }
}
