import { NEVER } from 'rxjs';

import '../prototype-extensions/observable.extensions';
import { NhCollectionTypeBaseComponent } from './nh-collection-base-component/component';
import { NhModalComponentImpl } from './nh-modal/component';
import { NhMutateBaseTypeComponent } from './nh-mutate-base-component/component';
import { NhPageTypeBaseComponent } from './nh-page-base-component/nh-page-base.component';

describe('NewHeap component lifecycle extension points', () => {
  async function flushLifecycle(): Promise<void> {
    for (let index = 0; index < 10; index++) {
      await Promise.resolve();
    }
  }

  it('keeps the legacy synchronous collection load path without an appOnInit override', () => {
    const events: string[] = [];
    const component = {
      initCollectionRequestModel: () => events.push('request-options'),
      appOnInit: NhCollectionTypeBaseComponent.prototype.appOnInit,
      load: () => {
        events.push('load');
        return Promise.resolve();
      }
    };

    NhCollectionTypeBaseComponent.prototype.ngOnInit.call(component as never);

    expect(events).toEqual(['request-options', 'load']);
  });

  it('runs collection appOnInit before the initial load', async () => {
    const events: string[] = [];
    const component = {
      initCollectionRequestModel: () => events.push('request-options'),
      appOnInit: async () => { events.push('appOnInit'); },
      load: async () => { events.push('load'); }
    };

    NhCollectionTypeBaseComponent.prototype.ngOnInit.call(component as never);
    await flushLifecycle();

    expect(events).toEqual(['request-options', 'appOnInit', 'load']);
  });

  it('runs collection appOnDestroy before disposing its active request', async () => {
    const events: string[] = [];
    const component = {
      appOnDestroy: async () => { events.push('appOnDestroy'); },
      activeRequestSubscription: {
        unsubscribe: () => events.push('unsubscribe')
      }
    };

    NhCollectionTypeBaseComponent.prototype.ngOnDestroy.call(component as never);
    await flushLifecycle();

    expect(events).toEqual(['appOnDestroy', 'unsubscribe']);
  });

  it('forwards modal content and mutate lifecycle through appOn hooks', async () => {
    const modal = new NhModalComponentImpl<unknown>();
    const modalEvents: string[] = [];
    spyOn(modal, 'appOnInit').and.callFake(async () => { modalEvents.push('appOnInit'); });
    spyOn(modal, 'appAfterViewInit').and.callFake(async () => { modalEvents.push('appAfterViewInit'); });
    spyOn(modal, 'appOnDestroy').and.callFake(async () => { modalEvents.push('appOnDestroy'); });

    modal.ngOnInit();
    await flushLifecycle();
    modal.ngAfterViewInit();
    await flushLifecycle();
    modal.ngOnDestroy();
    await flushLifecycle();

    expect(modalEvents).toEqual(['appOnInit', 'appAfterViewInit', 'appOnDestroy']);

    const mutateEvents: string[] = [];
    const mutate = {
      appOnInit: async () => { mutateEvents.push('appOnInit'); },
      appAfterViewInit: async () => { mutateEvents.push('appAfterViewInit'); },
      appOnDestroy: async () => { mutateEvents.push('appOnDestroy'); }
    };

    NhMutateBaseTypeComponent.prototype.ngOnInit.call(mutate as never);
    await flushLifecycle();
    NhMutateBaseTypeComponent.prototype.ngAfterViewInit.call(mutate as never);
    await flushLifecycle();
    NhMutateBaseTypeComponent.prototype.ngOnDestroy.call(mutate as never);
    await flushLifecycle();

    expect(mutateEvents).toEqual(['appOnInit', 'appAfterViewInit', 'appOnDestroy']);
  });

  it('preserves the existing page lifecycle chain', async () => {
    const events: string[] = [];
    const component = {
      $routeChanged: undefined,
      router: { events: NEVER },
      appOnInit: async () => { events.push('appOnInit'); },
      appOnInitAndLoad: async () => { events.push('appOnInitAndLoad'); },
      _appOnInitAndLoadWithSkipBrowserInitial: async () => { events.push('skip-browser-initial'); },
      flushMeta: async () => { events.push('flushMeta'); },
      pageService: {
        requestBreadcrumbUpdate: () => events.push('breadcrumb')
      },
      onInitDidRunOnce: false
    };

    await NhPageTypeBaseComponent.prototype.ngOnInit.call(component as never);
    await flushLifecycle();

    expect(events).toEqual([
      'appOnInit',
      'appOnInitAndLoad',
      'skip-browser-initial',
      'flushMeta',
      'breadcrumb'
    ]);
    expect(component.onInitDidRunOnce).toBeTrue();

    const unsubscribe = jasmine.createSpy('unsubscribe');
    const destroyComponent = {
      appOnDestroy: async () => { events.push('appOnDestroy'); },
      $routeChanged: { unsubscribe },
      $activeRouteParams: { unsubscribe },
      $activeRouteParamMap: { unsubscribe },
      $activeQueryParams: { unsubscribe },
      $activeQueryParamMap: { unsubscribe },
      $activeUrlFragment: { unsubscribe },
      $config: { unsubscribe },
      $auth: { unsubscribe }
    };

    await NhPageTypeBaseComponent.prototype.ngOnDestroy.call(destroyComponent as never);
    await flushLifecycle();

    expect(unsubscribe).toHaveBeenCalledTimes(7);
  });
});
