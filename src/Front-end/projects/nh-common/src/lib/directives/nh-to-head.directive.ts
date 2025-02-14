import { DOCUMENT } from '@angular/common';
import { Directive, ElementRef, Inject, OnDestroy, OnInit, Renderer2 } from '@angular/core';

const TO_HEAD_SELECTOR = 'nhToHead';

@Directive({
    selector: `[nhToHead]`,
    exportAs: 'nhToHead',
    standalone: false
})
export class NhToHeadDirective implements OnInit, OnDestroy {
  constructor(
    @Inject(DOCUMENT) private _document: Document,
    private _renderer: Renderer2,
    private _elementRef: ElementRef) {
  }

  ngOnInit(): void {
    this._renderer.appendChild(this._document.head, this._elementRef.nativeElement);
    this._renderer.removeAttribute(this._elementRef.nativeElement, TO_HEAD_SELECTOR.toLowerCase());
  }

  ngOnDestroy(): void {
    this._renderer.removeChild(this._document.head, this._elementRef.nativeElement);
  }
}
