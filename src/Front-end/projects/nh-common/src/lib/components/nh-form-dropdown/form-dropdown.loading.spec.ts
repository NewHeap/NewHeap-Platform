import {CommonModule} from '@angular/common';
import {ChangeDetectionStrategy, ChangeDetectorRef, Component, ViewChild} from '@angular/core';
import {TestBed} from '@angular/core/testing';
import {FormsModule} from '@angular/forms';
import {TranslateModule, TranslateService} from '@ngx-translate/core';
import {NgxBootstrapMultiselectModule} from 'ngx-bootstrap-multiselect';
import {EMPTY, of, Subject} from 'rxjs';
import {
  CollectionHttpRequestOptions, CollectionHttpResponse, DefaultMultiSelectSettings, DefaultMultiSelectTexts,
  NhCommonModuleConfig, NhFormDropDownComponent, NhFormDropDownNhCommonModuleConfig,
  NhFormDropDownSettings
} from '@newheap/platform-common';

@Component({
  standalone: false,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '<nh-form-dropdown [settings]="settings" [ngModel]="value"></nh-form-dropdown>'
})
class DropdownLoadingHost {
  settings!: NhFormDropDownSettings;
  value?: string | string[];
  @ViewChild(NhFormDropDownComponent) dropdown!: NhFormDropDownComponent;
}

describe('NH dropdown loading feedback', () => {
  it('does not emit a user change when the forms model writes a selection', () => {
    const dropdown: any = Object.create(NhFormDropDownComponent.prototype);
    const onChange = jasmine.createSpy();
    Object.assign(dropdown, {onChange, cdr: {markForCheck() {}}, _settings: {lazyLoad: false}});
    const selection = ['Upcoming', 'Deliver'];
    for (let i = 0; i < 1000; i++) {
      dropdown.writeValue(selection);
    }
    expect(onChange).not.toHaveBeenCalled();
    expect(dropdown.value).toEqual(selection);
    expect(dropdown.value).not.toBe(selection);
    dropdown.value = ['Complete'];
    expect(onChange).toHaveBeenCalledOnceWith(['Complete']);
  });
  beforeEach(() => {
    TestBed.configureTestingModule({
      declarations: [NhFormDropDownComponent, DropdownLoadingHost],
      imports: [CommonModule, FormsModule, NgxBootstrapMultiselectModule, TranslateModule.forRoot()],
      providers: [{provide: NhCommonModuleConfig, useValue: new NhCommonModuleConfig({
        formDropdown: new NhFormDropDownNhCommonModuleConfig({deferLazyLoadUntilOpened: true})
      })}]
    }).overrideComponent(NhFormDropDownComponent, {
      // Keep the host OnPush while exercising asynchronous notifications.
      set: {changeDetection: ChangeDetectionStrategy.Default}
    });
    const translate = TestBed.inject(TranslateService);
    translate.setTranslation('en', {form: {'form-dropdown': {
      loading: 'Loading…', 'search-empty-result': 'No results found.'
    }}});
    translate.use('en');
  });

  async function open(settings: NhFormDropDownSettings) {
    const fixture = TestBed.createComponent(DropdownLoadingHost);
    fixture.componentInstance.settings = settings;
    fixture.autoDetectChanges();
    await fixture.whenStable();
    fixture.nativeElement.querySelector('.dropdown-toggle').click();
    await fixture.whenStable();
    return fixture;
  }

  for (const selectionLimit of [1, 0]) {
    for (const fails of [false, true]) {
      it(`keeps the selected label while opening and searching (selection limit ${selectionLimit}, failure ${fails})`, async () => {
        const response = new Subject<CollectionHttpResponse<any>>();
        const selectedResponse = new Subject<CollectionHttpResponse<any>>();
        const selected = {id: 'newheap', name: 'NewHeap'};
        const fixture = TestBed.createComponent(DropdownLoadingHost);
        fixture.componentInstance.value = selectionLimit === 1 ? selected.id : [selected.id];
        fixture.componentInstance.settings = new NhFormDropDownSettings({
          lazyLoadLambda: () => response,
          selectedRequestOptions: new CollectionHttpRequestOptions(),
          selectedLazyLoadLambda: () => selectedResponse,
          multiSelectSettings: new DefaultMultiSelectSettings({selectionLimit, enableSearch: true})
        });
        fixture.autoDetectChanges();
        await fixture.whenStable();
        selectedResponse.next(new CollectionHttpResponse({items: [selected]}));
        selectedResponse.complete();
        fixture.debugElement.children[0].injector.get(ChangeDetectorRef).markForCheck();
        await fixture.whenStable();
        const button: HTMLButtonElement = fixture.nativeElement.querySelector('.dropdown-toggle');
        expect(button.textContent).toContain('NewHeap');
        button.click();
        await fixture.whenStable();
        expect(fixture.componentInstance.dropdown.isLoading).toBeTrue();
        expect(button.textContent).toContain('NewHeap');

        response.next(new CollectionHttpResponse({items: [selected, {id: 'other', name: 'Andere verkoper'}]}));
        await fixture.whenStable();
        expect(button.textContent).toContain('NewHeap');
        expect(fixture.componentInstance.dropdown.rawOptions).toContain(selected);

        fixture.componentInstance.dropdown.lazyLoadData({length: 0, filter: 'onbekend', isInitial: true});
        await fixture.whenStable();
        expect(button.textContent).toContain('NewHeap');
        if (fails) {
          response.error(new Error('Failed'));
        } else {
          response.next(new CollectionHttpResponse({items: [], resultCount: 0, itemsPerPage: 10}));
          response.complete();
        }
        await fixture.whenStable();
        expect(button.textContent).toContain('NewHeap');
        expect(fixture.componentInstance.dropdown.value).toEqual(selectionLimit === 1 ? selected.id : [selected.id]);
        expect(fixture.componentInstance.dropdown.isLoading).toBeFalse();
        if (!fails) {
          expect(fixture.componentInstance.dropdown.options.map(option => option.id)).toEqual([selected.id]);
          expect(fixture.componentInstance.dropdown.rawOptions).toEqual([]);
        }
        fixture.destroy();
      });
    }

    it(`shows loading before an empty response (selection limit ${selectionLimit})`, async () => {
      const response = new Subject<CollectionHttpResponse<any>>();
      const load = jasmine.createSpy().and.returnValue(response);
      const fixture = await open(new NhFormDropDownSettings({
        lazyLoadLambda: load,
        multiSelectSettings: new DefaultMultiSelectSettings({selectionLimit})
      }));
      expect(load).toHaveBeenCalledTimes(1);
      const status: HTMLElement = fixture.nativeElement.querySelector('.dropdown-item.empty');
      expect(status.textContent?.trim()).toBe('Loading…');
      expect(fixture.nativeElement.querySelector('[aria-busy="true"]')).not.toBeNull();
      response.next(new CollectionHttpResponse({items: [], resultCount: 0, itemsPerPage: 10}));
      response.complete();
      await fixture.whenStable();
      expect(status.textContent?.trim()).toBe('No results found.');
      expect(fixture.nativeElement.querySelector('.nh-dropdown-loading')).toBeNull();
      fixture.destroy();
    });
  }

  it('replaces loading with selectable results', async () => {
    const response = new Subject<CollectionHttpResponse<any>>();
    const fixture = await open(new NhFormDropDownSettings({lazyLoadLambda: () => response}));
    response.next(new CollectionHttpResponse({items: [{id: 'a', name: 'First option'}]}));
    response.complete();
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('.dropdown-item.empty')).toBeNull();
    expect(fixture.nativeElement.querySelector('.dropdown-menu').textContent).toContain('First option');
    expect(fixture.componentInstance.dropdown.isLoading).toBeFalse();
    fixture.destroy();
  });

  it('clears loading on failure and preserves a custom empty message', async () => {
    const response = new Subject<CollectionHttpResponse<any>>();
    const fixture = await open(new NhFormDropDownSettings({
      lazyLoadLambda: () => response,
      multiSelectTexts: new DefaultMultiSelectTexts({searchEmptyResult: 'Geen titels beschikbaar'})
    }));
    const onError = jasmine.createSpy();
    fixture.componentInstance.dropdown.onLazyLoadError.subscribe(onError);
    response.error(new Error('Failed'));
    await fixture.whenStable();
    expect(onError).toHaveBeenCalled();
    expect(fixture.nativeElement.querySelector('.dropdown-item.empty').textContent.trim()).toBe('Geen titels beschikbaar');
    expect(fixture.nativeElement.querySelector('.nh-dropdown-loading')).toBeNull();
    fixture.destroy();
  });

  it('shows loading for non-lazy options and clears it when they arrive', async () => {
    const response = new Subject<any[]>();
    const fixture = await open(new NhFormDropDownSettings({lazyLoad: false, loadLambda: () => response}));
    expect(fixture.nativeElement.querySelector('.dropdown-item.empty').textContent.trim()).toBe('Loading…');
    response.next([]);
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('.dropdown-item.empty').textContent.trim()).toBe('No results found.');
    expect(fixture.componentInstance.dropdown.isLoading).toBeFalse();
    response.complete();
    fixture.destroy();
  });

  it('clears non-lazy loading on errors and completion without values', async () => {
    const response = new Subject<any[]>();
    const fixture = await open(new NhFormDropDownSettings({lazyLoad: false, loadLambda: () => response}));
    response.error(new Error('Failed'));
    await fixture.whenStable();
    expect(fixture.componentInstance.dropdown.isLoading).toBeFalse();
    fixture.componentInstance.settings.loadLambda = () => EMPTY;
    fixture.componentInstance.dropdown.load();
    await fixture.whenStable();
    expect(fixture.componentInstance.dropdown.isLoading).toBeFalse();
    fixture.destroy();
  });

  it('clears loading if resolving selected items fails', async () => {
    const selected = new Subject<any[]>();
    const fixture = TestBed.createComponent(DropdownLoadingHost);
    fixture.componentInstance.settings = new NhFormDropDownSettings({lazyLoadLambda: () => of(new CollectionHttpResponse({items: []}))});
    fixture.autoDetectChanges();
    await fixture.whenStable();
    spyOn(fixture.componentInstance.dropdown, 'lazyLoadSelectedData').and.returnValue(selected);
    const onError = jasmine.createSpy();
    fixture.componentInstance.dropdown.onLazyLoadError.subscribe(onError);
    fixture.nativeElement.querySelector('.dropdown-toggle').click();
    await fixture.whenStable();
    selected.error(new Error('Failed'));
    await fixture.whenStable();
    expect(onError).toHaveBeenCalled();
    expect(fixture.componentInstance.dropdown.isLoading).toBeFalse();
    fixture.destroy();
  });
});
