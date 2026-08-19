import {DOCUMENT} from "@angular/common";
import {Inject, Injectable, Renderer2, RendererFactory2} from '@angular/core';
import {KeyValue} from "@angular/common";
import {PreConnectUrlItem, PreLoadUrlItem} from "../models/misc.models";

@Injectable({
  providedIn: 'root'
})
export class NhHeadService {
  private renderer: Renderer2;

  constructor(
    @Inject(DOCUMENT) private document: Document,
    rendererFactory: RendererFactory2
  ) {
    this.renderer = rendererFactory.createRenderer(null, null);
  }

  addPreConnectUrl(url: PreConnectUrlItem) {
    const connectParts = [];

    if(url.preConnect) {
      connectParts.push('preconnect');
    }

    if(url.dnsPrefetch) {
      connectParts.push('dns-prefetch');
    }

    const rel = connectParts.join(' ').trim();

    this.addLinkTag(rel, url.url, url.withCrossOrigin, url.crossOrigin, url.additionalAttributes);
  }

  addPreLoadUrl(url: PreLoadUrlItem) {
    const additionalAttributes: KeyValue<string, string>[] = url.additionalAttributes ?? [];

    additionalAttributes.push({key: 'as', value: url.as});
    additionalAttributes.push({key: 'type', value: url.type});

    this.addLinkTag('preload', url.url, url.withCrossOrigin, url.crossOrigin, additionalAttributes);
  }

  addLinkTag(rel: string, href: string, withCrossOrigin: boolean = false, crossOrigin?: string, additionalAttributes: KeyValue<string, string>[] = []) {
    const link = this.renderer.createElement('link');

    if(additionalAttributes) {
      for(const additionalAttribute of additionalAttributes) {
        this.renderer.setAttribute(link, additionalAttribute.key, additionalAttribute.value);
      }
    }

    this.renderer.setAttribute(link, 'rel', rel);
    this.renderer.setAttribute(link, 'href', href);

    if(withCrossOrigin) {
      this.renderer.setAttribute(link, 'crossorigin', crossOrigin ? crossOrigin : '');
    }


    this.addElementToHead(link);
  }

  addElementToHead(element: HTMLElement) {
    this.renderer.appendChild(this.document.head, element);
  }
}
