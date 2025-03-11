import {
  Component, inject,
  OnDestroy,
  OnInit,
  input
} from "@angular/core";
import {Observable, Subscription} from "rxjs";
import {ActivatedRoute, Router} from "@angular/router";
import {faDownload, faEye, faFileImport, faPlus, faSearch, faSync, faTrash} from "@fortawesome/free-solid-svg-icons";
import {TranslateService} from "@ngx-translate/core";
import {ToastrService} from "ngx-toastr";
import { ColumnMode } from "@swimlane/ngx-datatable";
import { NhAuthService } from "../../services/nh-auth.service";
import { NhModalService } from "../../services/nh-modal.service";
import {CollectionHttpRequestOptions, CollectionHttpResponse, OrderByRequestOptions} from "../../models/http.models";
import { ClaimTypes } from "../../models/auth.models";

@Component({
    selector: 'app-shared-collection-base-component',
    template: ``,
    standalone: false
})

export abstract class NhCollectionBaseComponent<TCollectionResponseItem>
  implements
    OnInit,
    OnDestroy
{
  protected readonly ColumnMode = ColumnMode;
  iconEye = faEye;
  iconTrash = faTrash;
  iconDownload = faDownload;
  protected static readonly URL_QUERY_PARAM_KEY = 'q';
  protected authService: NhAuthService = inject(NhAuthService);
  protected modalService: NhModalService = inject(NhModalService);
  protected translateService: TranslateService = inject(TranslateService);
  protected toastrService: ToastrService = inject(ToastrService);
  protected router: Router = inject(Router);
  protected activatedRoute: ActivatedRoute = inject(ActivatedRoute);
  iconPlus = faPlus;
  iconRefresh = faSync;
  iconImport = faFileImport;
  iconSearch = faSearch;
  tableInfo: any = {offset: 0, limit: 10};
  claimTypes = ClaimTypes;
  readonly queryParamUpdates = input<boolean>(true);
  readonly localStorageUpdates = input<boolean>(true);
  protected filtersActive = false;
  protected isLoading: boolean = false;
  protected requestOptions = new CollectionHttpRequestOptions();
  private activeRequestSubscription: Subscription|undefined;
  protected collectionResponse: CollectionHttpResponse<TCollectionResponseItem> = new CollectionHttpResponse<TCollectionResponseItem>();

  private get localStorageKey(): string {
    return `app-filters-${this.getLocalStoragePartialKey() ?? ''}`;
  }

  get items(): TCollectionResponseItem[] {
    return this.collectionResponse.items ?? [];
  }

  protected constructor(
  ) {
  }

  abstract onLoad(requestOptions: CollectionHttpRequestOptions): Promise<Observable<CollectionHttpResponse<TCollectionResponseItem>>>;
  abstract getInitialRequestOptions(): CollectionHttpRequestOptions;
  abstract getLocalStoragePartialKey(): string|null;

  async beforeLoad(): Promise<void> {

  }

  async afterLoad(): Promise<void> {

  }

  ngOnInit(): void {
    this.initCollectionRequestModel();
    this.load().then();
  }

  ngOnDestroy(): void {
    this.activeRequestSubscription?.unsubscribe();
  }

  initCollectionRequestModel() {
    // Setup filters from url.
    if (this.queryParamUpdates() && this.activatedRoute.snapshot.queryParamMap.get(NhCollectionBaseComponent.URL_QUERY_PARAM_KEY)) {
      try {
        this.requestOptions = new CollectionHttpRequestOptions(
          <CollectionHttpRequestOptions>JSON.parse(this.activatedRoute.snapshot.queryParamMap.get(NhCollectionBaseComponent.URL_QUERY_PARAM_KEY) ?? '')
        );
      } catch (ex) {
      }
    } else {
      let localStorageCollectionRequestModelSuccess = false;

      if (this.localStorageUpdates()) {
        const localStorageCollectionRequestModelString = localStorage.getItem(this.localStorageKey);

        if (localStorageCollectionRequestModelString && localStorageCollectionRequestModelString.length > 0) {
          try {
            this.requestOptions = new CollectionHttpRequestOptions(
              <CollectionHttpRequestOptions>JSON.parse(localStorageCollectionRequestModelString)
            );
            localStorageCollectionRequestModelSuccess = true;
          } catch (ex) {
          }
        }
      }

      if (!localStorageCollectionRequestModelSuccess) {
        this.requestOptions = this.getInitialRequestOptions();
      }
    }
  }

  updateRequestOptions() {
    const q = JSON.stringify(this.requestOptions);

    const queryParams: any = {};
    queryParams[NhCollectionBaseComponent.URL_QUERY_PARAM_KEY] = q;

    // Set filters to url.
    if (this.queryParamUpdates()) {
      this.router.navigate([], {
        relativeTo: this.activatedRoute,
        queryParams: queryParams,
        queryParamsHandling: 'merge',
        skipLocationChange: false,
        replaceUrl: true
      });
    }

    if(this.localStorageUpdates()) {
      localStorage.setItem(this.localStorageKey, JSON.stringify(this.requestOptions));
    }

    this.filtersActive = (q !== JSON.stringify(this.getInitialRequestOptions()));
  }

  resetRequestOptionsAndLoad() {
    this.resetRequestOptions();
    this.load().then();
  }

  resetRequestOptions() {
    this.requestOptions = this.getInitialRequestOptions();
    this.updateRequestOptions();
  }

  load(): Promise<CollectionHttpResponse<TCollectionResponseItem>> {
    return new Promise<CollectionHttpResponse<TCollectionResponseItem>>(async (resolve, reject) => {
      this.activeRequestSubscription?.unsubscribe();
      this.isLoading = true;
      await this.beforeLoad();
      this.updateRequestOptions();

      const loadObservable = await this.onLoad(this.requestOptions);

      this.activeRequestSubscription = loadObservable.subscribe({
        next: (response) => {
          this.isLoading = false;
          this.collectionResponse = response;
          resolve(response);
          this.afterLoad().then();
        },
        error: (error) => {
          this.isLoading = false;
          reject(error);
        }
      });
    });
  }


  async search(search: string) {
    this.requestOptions.page = 1;
    this.requestOptions.search = search;
    await this.load();
  }

  async sort(event: any) {
    //
    // Override in child to handle ur self.
    //

    const sort = event.sorts[0];
    this.requestOptions.orderBy = [];

    const getOrderBy = (key: string) => {
      const orderBy = new OrderByRequestOptions();
      orderBy.key = key;
      orderBy.direction = sort.dir.toUpperCase();
      return orderBy;
    };

    this.requestOptions.orderBy.push(getOrderBy(sort.prop));

    await this.load();
  }

  async setPageByOffsetLimit(event: {offset: number, limit: number}) {
    await this.setPage({
      page: event.offset + 1,
      itemsPerPage: event.limit
    });
  }

  async setPage(event: {page: number, itemsPerPage: number}) {
    this.requestOptions.page = event.page;
    this.requestOptions.itemsPerPage = event.itemsPerPage;

    await this.load();
  }
}
