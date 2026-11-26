import {fakeAsync, flushMicrotasks, tick} from '@angular/core/testing';
import {CollectionHttpRequestOptions, CollectionHttpResponse, NhCollectionBaseComponent} from '@newheap/platform-common';
import {EMPTY, of, Subject, throwError} from 'rxjs';

describe('Shared collection loading lifecycle', () => {
  let component: NhCollectionBaseComponent<any>;
  let load: jasmine.Spy;
  let update: jasmine.Spy;
  let after: jasmine.Spy;

  beforeEach(() => {
    component = Object.create(NhCollectionBaseComponent.prototype);
    load = jasmine.createSpy().and.resolveTo(of(new CollectionHttpResponse({items: []})));
    update = jasmine.createSpy();
    after = jasmine.createSpy().and.resolveTo();
    Object.assign(component, {requestOptions: new CollectionHttpRequestOptions(),
      collectionResponse: new CollectionHttpResponse({items: []}),
      beforeLoad: () => Promise.resolve(), onLoad: load, afterLoad: after, updateRequestOptions: update});
  });

  it('does not restart loading or navigate after destruction during beforeLoad', fakeAsync(() => {
    let settled = false;
    component.load().then(() => settled = true);
    component.ngOnDestroy();
    flushMicrotasks();
    expect(update).not.toHaveBeenCalled();
    expect(load).not.toHaveBeenCalled();
    expect(settled).toBeTrue();
  }));

  it('cancels the active response and settles its caller when the view is destroyed', fakeAsync(() => {
    const response = new Subject<CollectionHttpResponse<any>>();
    load.and.resolveTo(response);
    let settled = false;
    component.load().then(() => settled = true);
    flushMicrotasks();
    expect(response.observed).toBeTrue();
    component.ngOnDestroy();
    flushMicrotasks();
    expect(response.observed).toBeFalse();
    expect(settled).toBeTrue();
  }));

  it('only subscribes to the latest of simultaneous loads', fakeAsync(() => {
    const response = new Subject<CollectionHttpResponse<any>>();
    load.and.resolveTo(response);
    let settled = 0;
    component.load().then(() => settled++);
    component.load().then(() => settled++);
    flushMicrotasks();
    expect(response.observers.length).toBe(1);
    component.ngOnDestroy();
    flushMicrotasks();
    expect(response.observed).toBeFalse();
    expect(settled).toBe(2);
  }));

  it('ignores an async onLoad result that arrives after navigation', fakeAsync(() => {
    const response = new Subject<CollectionHttpResponse<any>>();
    let finish!: (value: any) => void;
    load.and.returnValue(new Promise(resolve => finish = resolve));
    component.load();
    flushMicrotasks();
    component.ngOnDestroy();
    finish(response);
    flushMicrotasks();
    expect(response.observed).toBeFalse();
    expect(after).not.toHaveBeenCalled();
  }));

  it('settles an empty result and clears loading', fakeAsync(() => {
    load.and.resolveTo(EMPTY);
    let settled = false;
    component.load().then(() => settled = true);
    flushMicrotasks();
    expect(settled).toBeTrue();
    expect((component as any).isLoading).toBeFalse();
  }));

  it('publishes a synchronous success once and runs afterLoad', fakeAsync(() => {
    const response = new CollectionHttpResponse({items: [{id: 'latest'}]});
    load.and.resolveTo(of(response));
    let result: any;
    component.load().then(value => result = value);
    flushMicrotasks();
    expect(result).toBe(response);
    expect((component as any).collectionResponse).toBe(response);
    expect(after).toHaveBeenCalledTimes(1);
    expect((component as any).isLoading).toBeFalse();
  }));

  it('ignores an older async load finishing after the latest response', fakeAsync(() => {
    let finish!: (value: any) => void;
    load.and.returnValue(new Promise(resolve => finish = resolve));
    let cancelled = false;
    component.load().then(() => cancelled = true);
    flushMicrotasks();
    const latest = new CollectionHttpResponse({items: [{id: 'latest'}]});
    load.and.resolveTo(of(latest));
    component.load();
    flushMicrotasks();
    const stale = new Subject<CollectionHttpResponse<any>>();
    finish(stale);
    flushMicrotasks();
    expect(cancelled).toBeTrue();
    expect(stale.observed).toBeFalse();
    expect((component as any).collectionResponse).toBe(latest);
    expect(after).toHaveBeenCalledTimes(1);
  }));

  it('rejects failures in each loading phase and permits retry', fakeAsync(() => {
    const failure = new Error('load failed');
    const rejected = jasmine.createSpy('rejected');
    component.beforeLoad = () => Promise.reject(failure);
    component.load().catch(rejected);
    flushMicrotasks();
    expect((component as any).isLoading).toBeFalse();
    component.beforeLoad = () => Promise.resolve();
    load.and.rejectWith(failure);
    component.load().catch(rejected);
    flushMicrotasks();
    expect((component as any).isLoading).toBeFalse();
    load.and.resolveTo(throwError(() => failure));
    component.load().catch(rejected);
    flushMicrotasks();
    expect(rejected).toHaveBeenCalledTimes(3);
    expect(rejected).toHaveBeenCalledWith(failure);
    expect((component as any).isLoading).toBeFalse();
    load.and.resolveTo(of(new CollectionHttpResponse({items: []})));
    component.load();
    flushMicrotasks();
    expect(after).toHaveBeenCalledTimes(1);
  }));
});
