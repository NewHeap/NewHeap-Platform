import {
  ChangeDetectorRef,
  Component,
  ElementRef,
  EventEmitter,
  Inject,
  inject,
  Input,
  NgZone,
  OnDestroy,
  OnInit, output,
  Optional,
  Output,
  PLATFORM_ID,
  ViewChild,
  ViewEncapsulation
} from '@angular/core';
import {DOCUMENT, isPlatformBrowser} from '@angular/common';
import {TranslateService} from '@ngx-translate/core';
import {Observable, Subject, Subscription} from 'rxjs';
import {HttpErrorResponse} from '@angular/common/http';
import {debounceTime, distinctUntilChanged, takeUntil} from 'rxjs/operators';
import {
  IMultiSelectOption,
  IMultiSelectSettings,
  IMultiSelectTexts,
  NgxDropdownMultiselectComponent
} from 'ngx-bootstrap-multiselect';
import {AbstractValueAccessor, MakeProvider} from "../../accessors/abstract-value.accessor";
import {CollectionHttpRequestOptions, CollectionHttpResponse} from '../../models/http.models';
import {NhCommonModuleConfig} from '../../models/config.models';

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
  deferLazyLoadUntilOpened?: boolean;
  loadLambda?: () => Observable<Array<any>>;
  lazyLoadLambda?: (options: CollectionHttpRequestOptions) => Observable<CollectionHttpResponse<any>>;
  selectedLazyLoadLambda?: (options: CollectionHttpRequestOptions, value?: any) => Observable<CollectionHttpResponse<any>>;
  componentRef?: NhFormDropDownComponent;
  keyGetLambda = (x: any) => {
    return x.id
  };
  imageGetLambda = (x: any) => undefined;
  /**
   * Optional CSS classes for the list item of an option, for example to mark an inactive item.
   * Applies to the options of `loadLambda`, `lazyLoadLambda` and `selectedLazyLoadLambda`.
   */
  classesGetLambda?: (x: any) => string | undefined;
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
  encapsulation: ViewEncapsulation.None,
  providers: [MakeProvider(NhFormDropDownComponent)],
  standalone: false
})
export class NhFormDropDownComponent extends AbstractValueAccessor implements OnInit, OnDestroy {
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

  /**
   * Every new settings object reloads the options, so keep one settings object per dropdown
   * instead of creating it in a template expression or a getter.
   */
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
    this.resetSelectedLazyLoadCache();
    this.load();
  }

  activeLazyLoadDataRequestSubscription: any = null;
  activeLazyLoadSelectedDataRequestSubscription: any = null;
  activeLoadRequestSubscription: Subscription|null = null;
  options: IMultiSelectOption[] = [];
  rawOptions: any[] = [];
  private debounceObserver: any;
  private debounceSubscription: Subscription|null = null;
  private readonly destroy$ = new Subject<void>();
  private hasOpenedDropdown = false;
  private hasLoadedDeferredLazyLoadData = false;
  private selectedLazyLoadValueKey?: string;
  private selectedLazyLoadOptions: IMultiSelectOption[] = [];
  private activeLazyLoadSelectedDataValueKey?: string;
  private activeLazyLoadSelectedDataObservers: Array<any> = [];
  private valueKey = '';
  private readonly hostElement = inject<ElementRef<HTMLElement>>(ElementRef, {optional: true})?.nativeElement;
  private readonly document = inject(DOCUMENT, {optional: true});
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID, {optional: true}) ?? 'server');
  private readonly ngZone = inject(NgZone, {optional: true});
  private userSelectionPending = false;
  private closedPending = false;
  private pendingOpenTimer?: ReturnType<typeof setTimeout>;
  private pointerEventInDispatch?: Event;
  private destroyed = false;
  private readonly trackPointerEvent = (event: Event) => {
    this.pointerEventInDispatch = event;
  };

  constructor(
    private translate: TranslateService,
    private cdr: ChangeDetectorRef,
    @Optional() @Inject(NhCommonModuleConfig) private commonModuleConfig?: NhCommonModuleConfig
  ) {
    super();
    this.startPointerTracking();
  }

  ngOnInit() {
  }

  ngOnDestroy(): void {
    this.destroyed = true;
    this.stopPointerTracking();
    this.cancelPendingOpen();
    this.closedPending = false;
    this.destroy$.next();
    this.destroy$.complete();

    this.activeLoadRequestSubscription?.unsubscribe();
    this.activeLoadRequestSubscription = null;
    this.activeLazyLoadDataRequestSubscription?.unsubscribe();
    this.activeLazyLoadDataRequestSubscription = null;
    this.activeLazyLoadSelectedDataRequestSubscription?.unsubscribe();
    this.activeLazyLoadSelectedDataRequestSubscription = null;
    this.debounceSubscription?.unsubscribe();
    this.debounceSubscription = null;
    this.debounceObserver = null;
    this.activeLazyLoadSelectedDataObservers = [];
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

  /**
   * Opens the list when it is closed and closes it when it is open.
   *
   * When this is called while a click or touch outside the dropdown is still being dispatched, for
   * example from the click handler of a separate edit button, the list opens once that event has
   * finished (after a `setTimeout` of 0), and `opened` is emitted then. Opening at once would let the
   * list treat the same click as a click outside and close again immediately. A second call before
   * the deferred open cancels it. Called from code outside such an event, or to close the list, it
   * takes effect immediately.
   */
  public toggleDropdown() {
    const component = this.getDropdownComponent();

    if (this.pendingOpenTimer !== undefined) {
      this.cancelPendingOpen();
      return;
    }

    if (!component!.isVisible && this.isOutsidePointerEventInDispatch()) {
      this.pendingOpenTimer = setTimeout(() => {
        this.pendingOpenTimer = undefined;
        this.openDeferredDropdown();
      });
      return;
    }

    component!.toggleDropdown();
    this.cdr.markForCheck();
  }

  public closeDropdown() {
    this.cancelPendingOpen();
    const component = this.getDropdownComponent();
    component!.closeDropdown();
    this.cdr.markForCheck();
  }

  public override writeValue(value: any) {
    const nextValue = this.cloneValue(value);
    const valueKey = this.getValueKey(nextValue);
    const currentValueKey = this.valueKey;
    super.writeValue(nextValue);
    this.valueKey = valueKey;
    this.cdr.markForCheck();

    if (this.settings.lazyLoad && this.deferLazyLoadUntilOpened && valueKey !== currentValueKey) {
      this.loadSelectedOptions();
    }
  }

  /**
   * Marks the next model of the inner multiselect as the user's choice. The inner list reports
   * `added` and `removed` only for a user selection, check all and uncheck all, and reports the
   * resulting model right after them.
   */
  onUserSelection() {
    if (this.userSelectionPending) {
      return;
    }

    this.userSelectionPending = true;
    queueMicrotask(() => {
      this.userSelectionPending = false;
    });
  }

  /**
   * Handles a model from the inner multiselect. The inner list also reports the model the form wrote
   * into it whenever its options change. That model is not a choice and can be older than the form's
   * current value, so only a model that follows a user selection becomes the new value.
   */
  onInnerModelChange(event: any) {
    if (!this.userSelectionPending) {
      return;
    }

    this.userSelectionPending = false;
    this.onModelChange(event);
    this.emitPendingClosed();
  }

  onDropdownClosed() {
    if (this.userSelectionPending) {
      // With closeOnSelect the inner list closes before it reports the choice; report the choice first.
      this.closedPending = true;
      queueMicrotask(() => this.emitPendingClosed());
      return;
    }

    this.closed.emit();
  }

  /**
   * Escape closes an open list and returns focus to the toggle button. The key is marked as handled,
   * so a surrounding modal stays open.
   */
  onEscapeKey(event: Event) {
    const component = this.getDropdownComponent();
    if (!component?.isVisible) {
      return;
    }

    event.preventDefault();
    this.closeDropdown();
    this.hostElement?.querySelector<HTMLButtonElement>('button.dropdown-toggle')?.focus();
  }

  public onDropdownOpened() {
    this.opened.emit();
    this.hasOpenedDropdown = true;

    if (this.settings.lazyLoad && this.deferLazyLoadUntilOpened && !this.hasLoadedDeferredLazyLoadData) {
      this.hasLoadedDeferredLazyLoadData = true;
      this.lazyLoadData({length: 0, isInitial: true});
    }
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
    this.load(false);
  }

  public load(resetDeferredState: boolean = true) {
    if (resetDeferredState) {
      this.hasOpenedDropdown = false;
      this.hasLoadedDeferredLazyLoadData = false;
    }

    this.options = [];
    this.rawOptions = [];
    if (this.settings.lazyLoad) {
      if (this.deferLazyLoadUntilOpened && !this.hasOpenedDropdown) {
        this.loadSelectedOptions();
      } else {
        if (this.deferLazyLoadUntilOpened) {
          this.hasLoadedDeferredLazyLoadData = true;
        }

        this.lazyLoadData({length: 0, isInitial: true});
      }
    } else {
      this.settings.multiSelectSettings.isLazyLoad = false;
      if(this.settings.loadLambda) {
        this.activeLoadRequestSubscription?.unsubscribe();
        this.activeLoadRequestSubscription = this.settings.loadLambda().pipe(
          takeUntil(this.destroy$)
        ).subscribe((options) => {
          this.options = options.map(x => this.createOption(x));
          this.rawOptions = options;
          this.activeLoadRequestSubscription = null;
          this.cdr.markForCheck();
        }, () => {
          this.activeLoadRequestSubscription = null;
        });
      }
    }
  }

  private get deferLazyLoadUntilOpened(): boolean {
    return this.settings.deferLazyLoadUntilOpened ?? this.commonModuleConfig?.formDropdown?.deferLazyLoadUntilOpened ?? false;
  }

  private emitPendingClosed() {
    if (!this.closedPending || this.destroyed) {
      return;
    }

    this.closedPending = false;
    this.closed.emit();
  }

  private openDeferredDropdown() {
    if (this.destroyed) {
      return;
    }

    const component = this.getDropdownComponent();
    if (!component || component.isVisible) {
      return;
    }

    component.toggleDropdown();
    this.cdr.markForCheck();
  }

  private cancelPendingOpen() {
    if (this.pendingOpenTimer === undefined) {
      return;
    }

    clearTimeout(this.pendingOpenTimer);
    this.pendingOpenTimer = undefined;
  }

  /**
   * The inner list closes on a document click or touch that did not start inside it. While such an
   * event is still being dispatched, the list must not open yet.
   */
  private isOutsidePointerEventInDispatch(): boolean {
    const event = this.pointerEventInDispatch;
    if (!event || event.eventPhase === Event.NONE) {
      return false;
    }

    const target = event.target;
    const dropdownElement = this.hostElement?.querySelector('ngx-bootstrap-multiselect > .dropdown') ?? this.hostElement;
    if (!dropdownElement || !(target instanceof Node)) {
      return true;
    }

    return !dropdownElement.contains(target);
  }

  private startPointerTracking() {
    const document = this.document;
    if (!this.browser || !document) {
      return;
    }

    const listen = () => {
      document.addEventListener('click', this.trackPointerEvent, true);
      document.addEventListener('touchstart', this.trackPointerEvent, {capture: true, passive: true});
    };

    if (this.ngZone) {
      this.ngZone.runOutsideAngular(listen);
    } else {
      listen();
    }
  }

  private stopPointerTracking() {
    const document = this.document;
    if (!this.browser || !document) {
      return;
    }

    document.removeEventListener('click', this.trackPointerEvent, true);
    document.removeEventListener('touchstart', this.trackPointerEvent, true);
    this.pointerEventInDispatch = undefined;
  }

  private createOption(item: any): IMultiSelectOption {
    const option: IMultiSelectOption = {
      id: this.settings.keyGetLambda(item),
      name: this.settings.valueGetLambda(item),
      image: this.settings.imageGetLambda(item)
    };

    if (this.settings.classesGetLambda) {
      option.classes = this.settings.classesGetLambda(item);
    }

    return option;
  }

  private cloneValue(value: any) {
    return Array.isArray(value) ? [...value] : value;
  }

  private getValueKey(value: any): string {
    if (value === undefined || value === null || value === '') {
      return '';
    }

    if (Array.isArray(value)) {
      const arrayKey = value
        .map(x => this.getValueKey(x))
        .filter(x => !!x)
        .sort()
        .join('|');

      return arrayKey ? 'array:' + arrayKey : '';
    }

    if (typeof value === 'object') {
      return 'object:' + JSON.stringify(value);
    }

    return 'value:' + value;
  }

  private resetSelectedLazyLoadCache() {
    if (this.activeLazyLoadSelectedDataRequestSubscription) {
      this.activeLazyLoadSelectedDataRequestSubscription.unsubscribe();
      this.activeLazyLoadSelectedDataRequestSubscription = null;
    }

    this.selectedLazyLoadValueKey = undefined;
    this.selectedLazyLoadOptions = [];
    this.activeLazyLoadSelectedDataValueKey = undefined;
    this.activeLazyLoadSelectedDataObservers = [];
  }

  private loadSelectedOptions() {
    this.lazyLoadSelectedData().subscribe((selectedItems: Array<IMultiSelectOption>) => {
      this.selectedHandleLazyLoadResponse(selectedItems);
      this.cdr.markForCheck();
    });
  }

  lazyLoadSelectedData(): Observable<Array<IMultiSelectOption>> {
    return new Observable((observer) => {
      const valueKey = this.getValueKey(this.value);

      if (!this.settings.lazyLoad || !this.settings.selectedRequestOptions || !this.settings.selectedLazyLoadLambda || !valueKey) {
        this.selectedLazyLoadValueKey = undefined;
        this.selectedLazyLoadOptions = [];
        observer.next([]);
        observer.complete();
        return undefined;
      }

      if (this.selectedLazyLoadValueKey === valueKey) {
        observer.next([...this.selectedLazyLoadOptions]);
        observer.complete();
        return undefined;
      }

      const requestOptions = this.settings.selectedRequestOptions;

      if (this.activeLazyLoadSelectedDataValueKey === valueKey) {
        this.activeLazyLoadSelectedDataObservers.push(observer);
        return () => {
          this.activeLazyLoadSelectedDataObservers = this.activeLazyLoadSelectedDataObservers.filter(x => x !== observer);
        };
      }

      if (this.activeLazyLoadSelectedDataRequestSubscription) {
        this.activeLazyLoadSelectedDataRequestSubscription.unsubscribe();
        this.activeLazyLoadSelectedDataObservers.forEach(x => {
          x.next([]);
          x.complete();
        });
        this.activeLazyLoadSelectedDataObservers = [];
      }

      this.activeLazyLoadSelectedDataValueKey = valueKey;
      this.activeLazyLoadSelectedDataObservers = [observer];
      this.activeLazyLoadSelectedDataRequestSubscription = this.settings.selectedLazyLoadLambda(requestOptions, this.value).subscribe(
        response => {
          this.activeLazyLoadSelectedDataRequestSubscription = null;
          this.activeLazyLoadSelectedDataValueKey = undefined;
          const selectedOptions: IMultiSelectOption[] = [];
          if (response && response.items && response.items.length > 0) {
            for (const item of response.items) {
              const option = this.createOption(item);

              if (this.settings.translateOptionValue) {
                option.name = this.translate.instant(option.name);
              }

              selectedOptions.push(option);
            }
          }

          this.selectedLazyLoadValueKey = valueKey;
          this.selectedLazyLoadOptions = selectedOptions;
          this.activeLazyLoadSelectedDataObservers.forEach(x => {
            x.next([...selectedOptions]);
            x.complete();
          });
          this.activeLazyLoadSelectedDataObservers = [];
        },
        (err: HttpErrorResponse) => {
          this.activeLazyLoadSelectedDataRequestSubscription = null;
          this.activeLazyLoadSelectedDataValueKey = undefined;
          this.activeLazyLoadSelectedDataObservers.forEach(x => x.error(err));
          this.activeLazyLoadSelectedDataObservers = [];
        }
      );
      return undefined;
    });
  }

  lazyLoadData(eventIn: any) {
    if (this.settings.lazyLoad && this.deferLazyLoadUntilOpened && !this.hasOpenedDropdown) {
      return;
    }

    if (eventIn.isInitial) {
      this.processLazyLoadEvent(eventIn);
      return;
    }

    if (!this.debounceObserver) {

      this.debounceSubscription = new Observable<any>((observer) => {
        this.debounceObserver = observer;
      }).pipe(debounceTime(this.settings.debounceTime)) // wait x ms after the last event before emitting last event
        .pipe(distinctUntilChanged()) // only emit if value is different from previous value
        .pipe(takeUntil(this.destroy$))
        .subscribe((event) => {
          this.processLazyLoadEvent(event);
        });
    }

    this.debounceObserver.next(eventIn);
  };

  private processLazyLoadEvent(event: any) {
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
              this.cdr.markForCheck();
              this.onLazyLoadError.emit(err);
            }
          );
        } else {
          this.isLoading = false;
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
              this.cdr.markForCheck();
              this.onLazyLoadError.emit(err);
            }
          );
        } else {
          this.isLoading = false;
        }

      } else {
        this.isLoading = false;
      }
    });
  }

  /**
   * Takes a model of the inner multiselect as the user's choice. In single-select mode the first
   * item becomes the value, including `0` and `false`; an empty model clears it.
   */
  onModelChange(event: any) {
    const value = this.settings.multiSelectSettings.selectionLimit === 1
      ? this.firstSelectedValue(event)
      : (this.cloneValue(event) || undefined);
    const valueKey = this.getValueKey(value);

    if (valueKey === this.valueKey) {
      return;
    }

    this.valueKey = valueKey;
    this.value = value;
    this.loadSelectedOptions();
  }

  private firstSelectedValue(event: any) {
    if (!Array.isArray(event) || event.length === 0) {
      return undefined;
    }

    return event[0];
  }

  private handleLazyLoadResponse(response: CollectionHttpResponse<any>, selectedItems: Array<IMultiSelectOption> = []) {
    const options: IMultiSelectOption[] = [];
    const rawOptions: any[] = [];
    for (const item of response.items) {
      const option = this.createOption(item);

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

    this.cdr.markForCheck();
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
