import {
  Component, inject,
  OnDestroy,
  OnInit,
  input, PLATFORM_ID, Type, model
} from "@angular/core";
import {Observable, Subscription} from "rxjs";
import {ActivatedRoute, Router} from "@angular/router";
import {TranslateService} from "@ngx-translate/core";
import {ToastrService} from "ngx-toastr";
import { ColumnMode } from "@swimlane/ngx-datatable";
import {BaseNhAuthService, NhAuthService} from "../../services/nh-auth.service";
import { NhModalService } from "../../services/nh-modal.service";
import {CollectionHttpRequestOptions, CollectionHttpResponse, OrderByRequestOptions} from "../../models/http.models";
import {ClaimTypes, INhAuthorization, NhAuthorization} from "../../models/auth.models";
import {NhRouterService} from "../../services/nh-router.service";
import {DOCUMENT, isPlatformServer} from "@angular/common";
import {NhAppService} from "../../services/nh-app.service";
import {NhApiUtil} from "../../util/nh-api-util";
import {NhAsyncLock} from "../../util/nh-mutex.util";

@Component({
    selector: 'nh-shared-collection-base-component',
    template: ``,
    standalone: false
})

export abstract class NhCollectionTypeBaseComponent<TCollectionResponseItem, TAuthorization extends INhAuthorization, TAuthService extends BaseNhAuthService<TAuthorization>>
  implements
    OnInit,
    OnDestroy
{
  private readonly loadLock = new NhAsyncLock();
  protected readonly appService = inject(NhAppService);
  protected readonly ColumnMode = ColumnMode;
  protected document: Document = inject(DOCUMENT)
  protected platformId: Object = inject(PLATFORM_ID);
  protected static readonly URL_QUERY_PARAM_KEY = 'q';
  protected authService: TAuthService;
  protected modalService: NhModalService = inject(NhModalService);
  protected translateService: TranslateService = inject(TranslateService);
  protected toastrService: ToastrService = inject(ToastrService);
  protected router: Router = inject(Router);
  protected activatedRoute: ActivatedRoute = inject(ActivatedRoute);
  protected nhRouterService: NhRouterService = inject(NhRouterService);
  tableInfo: any = {offset: 0, limit: 10};
  claimTypes = ClaimTypes;
  readonly queryParamUpdates = model<boolean>(true);
  readonly localStorageUpdates = model<boolean>(true);
  protected filtersActive = false;
  protected isLoading: boolean = false;
  protected requestOptions = new CollectionHttpRequestOptions();
  private activeRequestSubscription: Subscription|undefined;
  protected collectionResponse: CollectionHttpResponse<TCollectionResponseItem> = new CollectionHttpResponse<TCollectionResponseItem>();

  private get localStorageKey(): string {
    if(this.localStorageUpdates() && (this.getLocalStoragePartialKey() ?? '').length < 1) {
      throw new Error('getLocalStoragePartialKey() must return a non-empty string when localStorageUpdates is true, implement getLocalStoragePartialKey() via override getLocalStoragePartialKey().');
    }

    return `app-filters-${this.getLocalStoragePartialKey() ?? ''}`;
  }

  get items(): TCollectionResponseItem[] {
    return this.collectionResponse.items ?? [];
  }

  protected constructor(
    authServiceType: Type<TAuthService>
  ) {
    this.authService = inject(authServiceType);
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
        this.requestOptions = NhApiUtil.ParseCollectionRequestOptions(this.activatedRoute.snapshot.queryParamMap.get(NhCollectionBaseComponent.URL_QUERY_PARAM_KEY));
      } catch (ex) {
      }
    } else {
      let localStorageCollectionRequestModelSuccess = false;

      if (this.localStorageUpdates()) {
        const localStorageCollectionRequestModelString = this.appService.localStorage?.getItem(this.localStorageKey);

        if (localStorageCollectionRequestModelString && localStorageCollectionRequestModelString.length > 0) {
          try {
            this.requestOptions = NhApiUtil.ParseCollectionRequestOptions(localStorageCollectionRequestModelString);
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

  private syncRequestOptionsToTableInfo() {
    if (this.requestOptions.page && this.requestOptions.itemsPerPage) {
      this.tableInfo.offset = (this.requestOptions.page - 1) * this.requestOptions.itemsPerPage;
      this.tableInfo.limit = this.requestOptions.itemsPerPage;
    } else {
      this.tableInfo.offset = 0;
      this.tableInfo.limit = 10; // Default value
    }

    this.tableInfo.sorts = this.requestOptions.orderBy?.map(orderBy => ({
      prop: orderBy.key,
      dir: orderBy.direction.toLowerCase()
    })) || [];
  }

  updateRequestOptions() {
    const q = JSON.stringify(this.requestOptions);

    this.syncRequestOptionsToTableInfo();

    const queryParams: any = {};
    queryParams[NhCollectionBaseComponent.URL_QUERY_PARAM_KEY] = q;

    // Set filters to url.
    if (this.queryParamUpdates()) {
      this.router.navigate([], {
        relativeTo: this.activatedRoute,
        queryParams: queryParams,
        queryParamsHandling: 'merge',
        skipLocationChange: false,
        replaceUrl: true,
        preserveFragment: true
      });
    }

    if(this.localStorageUpdates() && !isPlatformServer(this.platformId)) {
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
      this.isLoading = true;
      await this.beforeLoad();
      this.updateRequestOptions();

      // Pass a copy instead of the source to allow modifications without it modifying the URL;
      // If u need them both modified, modify via this.requestOptions. in the load.
      const requestOptions = NhApiUtil.ParseCollectionRequestOptions(JSON.stringify(this.requestOptions));

      this.activeRequestSubscription?.unsubscribe();
      await this.loadLock.runExclusive(async () => {
        const loadObservable = await this.onLoad(requestOptions);
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
    });
  }


  async search(search: string) {
    if(search === this.requestOptions.search) {
      return;
    }

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
    if(event.page === this.collectionResponse.page && event.itemsPerPage === this.collectionResponse.itemsPerPage) {
      return;
    }

    this.requestOptions.page = event.page;
    this.requestOptions.itemsPerPage = event.itemsPerPage;

    await this.load();
  }

  async firstPage() {
    this.requestOptions.page = 1;
    await this.load();
  }
}


@Component({
  selector: 'nh-shared-collection-type-base-component',
  template: ``,
  standalone: false
})
export abstract class NhCollectionBaseComponent<TCollectionResponseItem> extends NhCollectionTypeBaseComponent<TCollectionResponseItem, NhAuthorization, NhAuthService> {
  constructor() {
    super(NhAuthService);
  }
}
