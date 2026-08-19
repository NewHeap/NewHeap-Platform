import {Directive, ElementRef, HostListener, Inject, Input, OnInit, Optional, Renderer2} from '@angular/core';
import {
  ActivatedRouteSnapshot,
  PRIMARY_OUTLET,
  Router,
  RouterLink,
  UrlSegmentGroup
} from "@angular/router";
import {NH_ROUTER_LANGUAGE_CHANGE_METHOD, NhRouterService} from "../services/nh-router.service";
import {NhRouterLink} from "../models/misc.models";
import {TranslateService} from "@ngx-translate/core";

@Directive({
    selector: `[nhRouterLink]`,
    exportAs: 'nhRouterLink',
    hostDirectives: [{
            directive: RouterLink,
            inputs: [
                'target: target',
                'queryParams: queryParams',
                'fragment: fragment',
                'queryParamsHandling: queryParamsHandling',
                'state: state',
                'info: info',
                'relativeTo: relativeTo',
                'preserveFragment: preserveFragment',
                'skipLocationChange: skipLocationChange',
                'replaceUrl: replaceUrl'
            ]
        }],
    standalone: false
})
export class NhRouterLinkDirective implements OnInit {
  private _nhRouterLink: NhRouterLink | null | undefined;
  private _resolvedNhRouterLink: any[] | string | null | undefined;

  @Input() set nhRouterLink(nhRouterLink: NhRouterLink | undefined) {
    this._nhRouterLink = nhRouterLink;

    if (this.hostRouterLink) {
      this._resolvedNhRouterLink = this.transformRouteLink(this._nhRouterLink);
      this.hostRouterLink.routerLink = this._resolvedNhRouterLink;
      this.hostRouterLink.ngOnChanges(<any>{}); // Trigger fake ngOnChanges
    }
  }

  get nhRouterLink(): NhRouterLink | null | undefined {
    return this._nhRouterLink;
  }

  constructor(
    private elementRef: ElementRef,
    private renderer: Renderer2,
    private hostRouterLink: RouterLink,
    private router: Router,
    private routerService: NhRouterService,
    private translateService: TranslateService,
    @Optional() @Inject(NH_ROUTER_LANGUAGE_CHANGE_METHOD) private changeLanguageMethod: (newLanguage: string) => Promise<void>
  ) {
  }

  ngOnInit() {

  }

  private transformRouteLink(routeLink: NhRouterLink | null | undefined): any[] | string | null | undefined {
    if (routeLink === null || routeLink === undefined) {
      return routeLink;
    }

    return this.routerService.createUrlForNavigationItem(routeLink);
  }

  /** @nodoc */
  @HostListener('click', [
    '$event.button',
    '$event.ctrlKey',
    '$event.shiftKey',
    '$event.altKey',
    '$event.metaKey',
  ])
  async onClick(
    button: number,
    ctrlKey: boolean,
    shiftKey: boolean,
    altKey: boolean,
    metaKey: boolean,
  ): Promise<boolean> {
    if (this._nhRouterLink && this._nhRouterLink.language && this._nhRouterLink.language !== this.translateService.currentLang) {
      // We detected a language change, change the language and reload the routes so navigation works.
      if (!this.changeLanguageMethod) {
        throw new Error('Nh_ROUTER_LANGUAGE_CHANGE_METHOD is not defined via DI injection token.');
      }
      // We detected a language change, change the language and reload the routes so navigation works.
      await this.changeLanguageMethod(this._nhRouterLink.language);
      this.routerService.reloadRoutes();
    }
    if (window?.scrollTo && this._nhRouterLink?.scrollToTop !== false) {
      setTimeout(() => window.scrollTo(0, 0), 10);
    }
    return this.hostRouterLink.onClick(button, ctrlKey, shiftKey, altKey, metaKey);
  }
}
