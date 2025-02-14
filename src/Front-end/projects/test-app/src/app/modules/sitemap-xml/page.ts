import {Component, Inject, NgZone, Optional, REQUEST_CONTEXT} from '@angular/core';
import {
  NhPageBaseComponent,
  NhRouterSetupService
} from "nh-common";

@Component({
    selector: 'app-home-page-sitemap-xml',
    templateUrl: './page.html',
    styleUrls: ['./page.scss'],
    standalone: false
})
export class SitemapXmlHomePage extends NhPageBaseComponent {
    constructor(
        private zone: NgZone,
        @Optional() @Inject(REQUEST_CONTEXT) private requestContext: any,
        private nhRouterSetupService: NhRouterSetupService,
    ) {
        super();
        this.handleSitemapXml();
    }

    override async appOnInit() {

    }

    async load() {
    }

    handleSitemapXml() {
        if (this.isPlatformBrowser()) {
            window.location.reload();
        } else if (this.isPlatformServer()) {
            this.handleServerSitemap();
        }
    }

    private handleServerSitemap() {
        if (!this?.requestContext?.__DO_GENERATE_SITEMAP_DATA__) {
            return;
        }

        const clientSitemap = this.nhRouterService.createSitemap();
    }
}
