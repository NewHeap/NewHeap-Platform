import {Component} from "@angular/core";
import {ActivatedRoute} from "@angular/router";
import { NhPageBaseComponent } from "nh-common";

@Component({
    selector: 'app-home-page-index',
    templateUrl: './page.html',
    styleUrls: ['./page.scss'],
    standalone: false
})
export class IndexHomePage extends NhPageBaseComponent {
  constructor(
    private route: ActivatedRoute
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
  }

  async load() {
  }
}
