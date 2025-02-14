import {Component, Inject, OnDestroy, OnInit, Renderer2, ViewEncapsulation} from '@angular/core';
import {DOCUMENT} from "@angular/common";

@Component({
    selector: 'app-layout-default',
    encapsulation: ViewEncapsulation.None,
    templateUrl: './default-layout.html',
    styleUrls: ['./default-layout.scss'],
    standalone: false
})
export class AppDefaultLayoutComponent implements OnInit, OnDestroy {

  constructor(
    @Inject(DOCUMENT) private document: Document,
    private renderer: Renderer2
  ) {

  }

  async ngOnInit() {
    this.renderer.addClass(this.document.body, 'layout-default');
  }

  ngOnDestroy() {
    this.renderer.removeClass(this.document.body, 'layout-default');
  }
}
