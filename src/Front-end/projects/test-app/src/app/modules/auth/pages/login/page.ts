import {Component, signal} from "@angular/core";
import {ActivatedRoute} from "@angular/router";
import {NhPageBaseComponent} from "@newheap/platform-common";
import {ZenoEditionModel} from "../../../../models/zeno-edition-model";
import {ZenoService} from "../../services/zeno.service";
import {
  ApiCollectionRequestOptions,
  ApiCollectionResponse
} from "../../../../models/api-collection-request-options-model";
import {CollectionHttpRequestOptions} from "@newheap/platform-common/lib/models/http.models";

@Component({
    selector: 'app-zeno-page-index',
    templateUrl: './page.html',
    styleUrls: ['./page.scss'],
    standalone: false
})
export class ZenoPage extends NhPageBaseComponent {

  data = signal<ApiCollectionResponse<ZenoEditionModel>>(new ApiCollectionResponse<ZenoEditionModel>());
  items : ZenoEditionModel[] = [];
  isLoading = false;
  protected requestOptions = new ApiCollectionRequestOptions();

  constructor(
    private route: ActivatedRoute,
    private zenoService: ZenoService
  ) {
    super();
  }

  override async appOnInit() {

  }
  override async appOnInitAndLoadWithSkipBrowserInitial() {
    this.load().then();
    this.pageSettings.title = this.translateService.instant('Zeno');
    this.pageSettings.description = this.translateService.instant('Zeno description');
  }

  override async appAfterViewInit() {
  }

  async load() {
    this.isLoading = true;
    this.zenoService.getEditions(this.requestOptions).subscribe(x => {
      this.data.set(x);
      this.items = x.items!;
      console.log(this.data());
      this.isLoading = false;
    });
  }


  async setPage(event: {pageSize: number, limit: number, offset: number}) {
    this.requestOptions.page = event.offset;

    await this.load();
  }
}
