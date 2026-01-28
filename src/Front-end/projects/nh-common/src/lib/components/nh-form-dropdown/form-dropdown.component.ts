import {
  ChangeDetectorRef,
  Component,
  EventEmitter,
  Input,
  OnInit, output,
  Output,
  ViewChild,
  ViewEncapsulation
} from '@angular/core';
import {TranslateService} from '@ngx-translate/core';
import {Observable} from 'rxjs';
import {HttpErrorResponse} from '@angular/common/http';
import {debounceTime, distinctUntilChanged} from 'rxjs/operators';
import {
  IMultiSelectOption,
  IMultiSelectSettings,
  IMultiSelectTexts,
  NgxDropdownMultiselectComponent
} from 'ngx-bootstrap-multiselect';
import {AbstractValueAccessor, MakeProvider} from "../../accessors/abstract-value.accessor";
import {CollectionHttpRequestOptions, CollectionHttpResponse} from '../../models/http.models';

export class DefaultMultiSelectSettings implements IMultiSelectSettings {
  pullRight = false;
  enableSearch = true;
  checkedStyle: 'checkboxes' | 'glyphicon' | 'fontawesome' | 'visual' = 'fontawesome';
  buttonClasses = 'btn btn-default btn-secondary';
  selectionLimit = 1;
  closeOnSelect = false;
  autoUnselect = true;
  showCheckAll = false;
  showUncheckAll = false;
  fixedTitle = false;
  dynamicTitleMaxItems = 3;
  maxHeight = '300px';
  isLazyLoad = true;
  loadViewDistance = 1;
  stopScrollPropagation = true;
  selectAddedValues = true;

  public constructor(init?: Partial<DefaultMultiSelectSettings>) {
    Object.assign(this, init);
  }
}

export class DefaultMultiSelectTexts implements IMultiSelectTexts {
  checkAll = 'Select all';
  uncheckAll = 'Unselect all';
  checked = 'item selected';
  checkedPlural = 'items selected';
  searchPlaceholder = 'Find';
  searchEmptyResult = 'Nothing found...';
  searchNoRenderText = 'Type in search box to see results...';
  defaultTitle = 'Select';
  allSelected = 'All selected';

  public constructor(init?: Partial<DefaultMultiSelectTexts>) {
    Object.assign(this, init);
  }
}

export class NhFormDropDownSettings {

  lazyLoad = true;
  translateOptionValue = true;
  multiSelectSettings: IMultiSelectSettings = new DefaultMultiSelectSettings({});
  multiSelectTexts: IMultiSelectTexts = new DefaultMultiSelectTexts();
  requestOptions = new CollectionHttpRequestOptions();
  debounceTime = 300;
  selectedRequestOptions = new CollectionHttpRequestOptions();
  loadLambda?: () => Observable<Array<any>>;
  lazyLoadLambda?: (options: CollectionHttpRequestOptions) => Observable<CollectionHttpResponse<any>>;
  selectedLazyLoadLambda?: (options: CollectionHttpRequestOptions, value?: any) => Observable<CollectionHttpResponse<any>>;
  componentRef?: NhFormDropDownComponent;
  keyGetLambda = (x: any) => {
    return x.id
  };
  imageGetLambda = (x: any) => undefined;
  valueGetLambda = (x: any) => {
    return x.name
  };
  onSuccess = () => {
    return;
  };

  public constructor(init?: Partial<NhFormDropDownSettings>) {
    Object.assign(this, init);
  }
}

@Component({
  selector: 'nh-form-dropdown',
  templateUrl: './form-dropdown.component.html',
  styleUrls: ['./form-dropdown.component.scss'],
  encapsulation: ViewEncapsulation.None,
  providers: [MakeProvider(NhFormDropDownComponent)],
  standalone: false
})
export class NhFormDropDownComponent extends AbstractValueAccessor implements OnInit {
  @ViewChild('lazyLoadComponent') lazyLoadComponent?: NgxDropdownMultiselectComponent;
  @ViewChild('noLazyLoadComponent') noLazyLoadComponent?: NgxDropdownMultiselectComponent;
  defaultTexts = new DefaultMultiSelectTexts();
  isLoading = false;
  hasMoreLazyLoadItems = true;
  lastLazyLoadEvent: any;
  hasFocus = false;
  @Input() disabled : boolean = false;
  @Output() onLazyLoadError = new EventEmitter();
  private _settings: NhFormDropDownSettings = new NhFormDropDownSettings();
  public readonly closed = output<void>();
  public readonly opened = output<void>();

  get settings() {
    return this._settings;
  }

  @Input() set settings (value: NhFormDropDownSettings) {
    if (!value.multiSelectTexts) {
      value.multiSelectTexts = new DefaultMultiSelectTexts();
    }

    value.multiSelectTexts = {
      checkAll: (value.multiSelectTexts.checkAll === this.defaultTexts.checkAll) ? this.translate.instant('form.form-dropdown.check-all') : value.multiSelectTexts.checkAll,
      uncheckAll: (value.multiSelectTexts.uncheckAll === this.defaultTexts.uncheckAll) ? this.translate.instant('form.form-dropdown.uncheck-all') : value.multiSelectTexts.uncheckAll,
      checked: (value.multiSelectTexts.checked === this.defaultTexts.checked) ? this.translate.instant('form.form-dropdown.checked') : value.multiSelectTexts.checked,
      checkedPlural: (value.multiSelectTexts.checkedPlural === this.defaultTexts.checkedPlural) ? this.translate.instant('form.form-dropdown.checked-plural') : value.multiSelectTexts.checkedPlural,
      searchPlaceholder: (value.multiSelectTexts.searchPlaceholder === this.defaultTexts.searchPlaceholder) ? this.translate.instant('form.form-dropdown.search-placeholder') : value.multiSelectTexts.searchPlaceholder,
      searchEmptyResult: (value.multiSelectTexts.searchEmptyResult === this.defaultTexts.searchEmptyResult) ? this.translate.instant('form.form-dropdown.search-empty-result') : value.multiSelectTexts.searchEmptyResult,
      searchNoRenderText: (value.multiSelectTexts.searchNoRenderText === this.defaultTexts.searchNoRenderText) ? this.translate.instant('form.form-dropdown.search-no-render-text') : value.multiSelectTexts.searchNoRenderText,
      defaultTitle: (value.multiSelectTexts.defaultTitle === this.defaultTexts.defaultTitle) ? this.translate.instant('form.form-dropdown.default-title') : value.multiSelectTexts.defaultTitle,
      allSelected: (value.multiSelectTexts.allSelected === this.defaultTexts.allSelected) ? this.translate.instant('form.form-dropdown.all-selected') : value.multiSelectTexts.allSelected
    };

    value.componentRef = this;

    this._settings = value;
    this.load();
  }

  activeLazyLoadDataRequestSubscription: any = null;
  activeLazyLoadSelectedDataRequestSubscription: any = null;
  options: IMultiSelectOption[] = [];
  rawOptions: any[] = [];
  private debounceObserver: any;

  constructor(
    private translate: TranslateService,
    private cdr: ChangeDetectorRef
  ) {
    super();
  }

  ngOnInit() {
  }

  public getDropdownComponent(): NgxDropdownMultiselectComponent|undefined {
    let component: any = undefined;
    if (this.lazyLoadComponent) {
      component = this.lazyLoadComponent;
    }
    if (this.noLazyLoadComponent) {
      component = this.noLazyLoadComponent;
    }

    return component;
  }

  public toggleDropdown() {
    const component = this.getDropdownComponent();
    component!.toggleDropdown();
    const value = this.value;
    this.value = -1;
    this.cdr.detectChanges();
    this.value = value;
    this.cdr.detectChanges();
  }

  public closeDropdown() {
    const component = this.getDropdownComponent();
    component!.closeDropdown();
    const value = this.value;
    this.value = -1;
    this.cdr.detectChanges();
    this.value = value;
    this.cdr.detectChanges();
  }

  resetSearch() {
    this.settings.requestOptions.page = 1;
    this.settings.requestOptions.search = '';
    if (this.lazyLoadComponent) {
      (<any>this.lazyLoadComponent).clearSearch(null);
    }
    if (this.noLazyLoadComponent) {
      (<any>this.noLazyLoadComponent).clearSearch(null);
    }
    this.load();
  }

  public load() {
    this.options = [];
    this.rawOptions = [];
    if (this.settings.lazyLoad) {
      this.lazyLoadData({length: 0, isInitial: true});
    } else {
      this.settings.multiSelectSettings.isLazyLoad = false;
      if(this.settings.loadLambda) {
        this.settings.loadLambda().subscribe((options) => {
          this.options = options.map(x => {
            return {id: this.settings.keyGetLambda(x), name: this.settings.valueGetLambda(x), image: this.settings.imageGetLambda(x)}
          });
          this.rawOptions = options;
        });
      }
    }
  }

  lazyLoadSelectedData(): Observable<Array<IMultiSelectOption>> {
    return new Observable((observer) => {

      if (!this.settings.lazyLoad || !this.settings.selectedRequestOptions || !this.settings.selectedLazyLoadLambda || !this.value || this.value === null) {
        observer.next([]);
        return;
      }

      if (!this.value || this.value.length < 1 || this.value === null) {
        observer.next([]);
        return;
      }

      const requestOptions = this.settings.selectedRequestOptions;

      if (this.activeLazyLoadSelectedDataRequestSubscription) {
        this.activeLazyLoadSelectedDataRequestSubscription.unsubscribe();
      }

      this.activeLazyLoadSelectedDataRequestSubscription = this.settings.selectedLazyLoadLambda(requestOptions, this.value).subscribe(
        response => {
          this.activeLazyLoadSelectedDataRequestSubscription = null;
          const selectedOptions = [];
          if (response && response.items && response.items.length > 0) {
            for (const item of response.items) {
              const option = {id: this.settings.keyGetLambda(item), name: this.settings.valueGetLambda(item), image: this.settings.imageGetLambda(item)};

              if (this.settings.translateOptionValue) {
                option.name = this.translate.instant(option.name);
              }

              selectedOptions.push(option);
            }
          }

          observer.next(selectedOptions);
        },
        (err: HttpErrorResponse) => {
          this.activeLazyLoadSelectedDataRequestSubscription = null;
          observer.error(err);
        }
      );
    });
  }

  lazyLoadData(eventIn: any) {
    if (!this.debounceObserver) {

      new Observable<any>((observer) => {
        this.debounceObserver = observer;
      }).pipe(debounceTime(this.settings.debounceTime)) // wait x ms after the last event before emitting last event
        .pipe(distinctUntilChanged()) // only emit if value is different from previous value
        .subscribe((event) => {
          if (this.isLoading) {
            return;
          }

          this.lastLazyLoadEvent = event;
          this.isLoading = true;

          this.lazyLoadSelectedData().subscribe((selectedItems: Array<IMultiSelectOption>) => {

            const filter = (!event.filter) ? '' : event.filter;
            let page = this.settings.requestOptions.page;
            if (!event.isInitial) {
              page++;
            }

            if (this.activeLazyLoadDataRequestSubscription) {
              this.activeLazyLoadDataRequestSubscription.unsubscribe();
            }

            if (filter !== this.settings.requestOptions.search || event.isInitial) {
              page = 1;
              this.hasMoreLazyLoadItems = true;
              this.settings.requestOptions.search = (!filter) ? '' : filter;
              this.settings.requestOptions.page = page;
              this.options = [];
              this.rawOptions = [];

              if(this.settings.lazyLoadLambda) {
                this.activeLazyLoadDataRequestSubscription = this.settings.lazyLoadLambda(this.settings.requestOptions).subscribe(
                  response => {
                    this.activeLazyLoadDataRequestSubscription = null;
                    this.handleLazyLoadResponse(response, selectedItems);
                    this.isLoading = false;
                    this.settings.onSuccess();
                  },
                  (err: HttpErrorResponse) => {
                    this.activeLazyLoadDataRequestSubscription = null;
                    this.isLoading = false;
                    this.onLazyLoadError.emit(err);
                  }
                );
              }
            } else if (this.hasMoreLazyLoadItems) {
              this.settings.requestOptions.page = page;

              if(this.settings.lazyLoadLambda) {
                this.activeLazyLoadDataRequestSubscription = this.settings.lazyLoadLambda(this.settings.requestOptions).subscribe(
                  response => {
                    this.activeLazyLoadDataRequestSubscription = null;
                    this.handleLazyLoadResponse(response, selectedItems);
                    this.isLoading = false;
                    this.settings.onSuccess();
                  },
                  (err: HttpErrorResponse) => {
                    this.activeLazyLoadDataRequestSubscription = null;
                    this.isLoading = false;
                    this.onLazyLoadError.emit(err);
                  }
                );
              }

            } else {
              this.isLoading = false;
            }
          });
        });
    }

    this.debounceObserver.next(eventIn);
  };

  onModelChange(event: any) {
    this.value = (this.settings.multiSelectSettings.selectionLimit === 1
        ? (event[0] || undefined)
        : (event || undefined)
    );

    this.lazyLoadSelectedData().subscribe((selectedItems: Array<IMultiSelectOption>) => {
      this.selectedHandleLazyLoadResponse(selectedItems);
    });
  }


  private handleLazyLoadResponse(response: CollectionHttpResponse<any>, selectedItems: Array<IMultiSelectOption> = []) {
    const options: IMultiSelectOption[] = [];
    const rawOptions: any[] = [];
    for (const item of response.items) {
      const option = {id: this.settings.keyGetLambda(item), name: this.settings.valueGetLambda(item)};

      if (this.settings.translateOptionValue) {
        option.name = this.translate.instant(option.name);
      }

      if (!this.options.find(x => x.id === option.id)) {
        options.push(option);
        rawOptions.push(item);
      }
    }

    this.options = this.options.concat(options);
    this.rawOptions = this.rawOptions.concat(rawOptions);
    this.selectedHandleLazyLoadResponse(selectedItems);

    if (response.resultCount < response.itemsPerPage) {
      this.hasMoreLazyLoadItems = false;
    }
  }

  private selectedHandleLazyLoadResponse(selectedItems: Array<IMultiSelectOption> = []) {
    const newSelectedOptions: IMultiSelectOption[] = [];
    for (const option of selectedItems) {
      if (!this.options.find(x => x.id === option.id)) {
        newSelectedOptions.push(option);
      }
    }

    this.options = newSelectedOptions.concat(this.options);
  }
}
