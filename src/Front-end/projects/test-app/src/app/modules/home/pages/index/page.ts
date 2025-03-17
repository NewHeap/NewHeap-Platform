import {Component} from "@angular/core";
import {ActivatedRoute} from "@angular/router";
import {CollectionHttpRequestOptions, NhApiService, NhPageBaseComponent} from "nh-common";

@Component({
    selector: 'app-home-page-index',
    templateUrl: './page.html',
    styleUrls: ['./page.scss'],
    standalone: false
})
export class IndexHomePage extends NhPageBaseComponent {
  constructor(
    private route: ActivatedRoute,
    private apiService: NhApiService
  ) {
    super();
  }

  override async appOnInit() {

  }
  override async appOnInitAndLoadWithSkipBrowserInitial() {
    this.load().then();
    this.pageSettings.title = this.translateService.instant('Dit is de homepage');
    this.pageSettings.description = this.translateService.instant('Dit is de description');
  }

  override async appAfterViewInit() {
    if(this.isPlatformBrowser()) {
      this.apiService.getCollection<any>('https://localhost:5301/address', new CollectionHttpRequestOptions({})).subscribe((x) => {
        //debugger;
      });
    }

  }

  async load() {
  }
}
