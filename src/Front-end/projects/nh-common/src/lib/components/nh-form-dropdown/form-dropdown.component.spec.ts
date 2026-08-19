import {Subject, of} from 'rxjs';
import {TestBed} from '@angular/core/testing';

import {CollectionHttpResponse} from '../../models/http.models';
import {
  NhCommonModuleConfig,
  NhFormDropDownNhCommonModuleConfig
} from '../../models/config.models';
import {
  NhFormDropDownComponent,
  NhFormDropDownSettings
} from './form-dropdown.component';

describe('NhFormDropDownComponent preferred lazy loading', () => {
  const createComponent = (config?: NhCommonModuleConfig) =>
    TestBed.runInInjectionContext(() =>
      new NhFormDropDownComponent(
        {instant: (value: string) => value} as never,
        {detectChanges: () => undefined, markForCheck: () => undefined} as never,
        config
      )
    );

  it('defers the option collection when the recommended global opt-in is enabled', () => {
    const component = createComponent(new NhCommonModuleConfig({
      formDropdown: new NhFormDropDownNhCommonModuleConfig({deferLazyLoadUntilOpened: true})
    }));
    let collectionRequests = 0;
    component.settings = new NhFormDropDownSettings({
      lazyLoad: true,
      lazyLoadLambda: () => {
        collectionRequests++;
        return of(new CollectionHttpResponse({items: []}));
      }
    });

    expect(collectionRequests).toBe(0);

    component.onDropdownOpened();
    component.onDropdownOpened();

    expect(collectionRequests).toBe(1);
    component.ngOnDestroy();
  });

  it('supports a local eager-loading opt-out', () => {
    const component = createComponent(new NhCommonModuleConfig({
      formDropdown: new NhFormDropDownNhCommonModuleConfig({deferLazyLoadUntilOpened: true})
    }));
    let collectionRequests = 0;
    component.settings = new NhFormDropDownSettings({
      lazyLoad: true,
      deferLazyLoadUntilOpened: false,
      lazyLoadLambda: () => {
        collectionRequests++;
        return of(new CollectionHttpResponse({items: []}));
      }
    });

    expect(collectionRequests).toBe(1);
    component.ngOnDestroy();
  });

  it('reuses the selected-option request while the same value is in flight', () => {
    const component = createComponent(new NhCommonModuleConfig({
      formDropdown: new NhFormDropDownNhCommonModuleConfig({deferLazyLoadUntilOpened: true})
    }));
    const selectedResponse = new Subject<CollectionHttpResponse<{id: string; name: string}>>();
    let selectedRequests = 0;
    component.settings = new NhFormDropDownSettings({
      lazyLoad: true,
      selectedLazyLoadLambda: () => {
        selectedRequests++;
        return selectedResponse;
      }
    });

    component.writeValue(['project-a']);
    component.writeValue(['project-a']);
    component.lazyLoadSelectedData().subscribe();

    expect(selectedRequests).toBe(1);

    selectedResponse.next(new CollectionHttpResponse({
      items: [{id: 'project-a', name: 'Project A'}]
    }));
    selectedResponse.complete();

    expect(component.options).toEqual([{id: 'project-a', name: 'Project A', image: undefined}]);
    component.ngOnDestroy();
  });

});
