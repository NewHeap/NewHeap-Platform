import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { NhBackgroundOperationAdministrationCollectionHttpRequestOptions } from '../models/background-operation.models';
import {
  BackgroundOperationsNhCommonModuleConfig,
  NhCommonModuleConfig
} from '../models/config.models';
import { CollectionHttpResponse, HttpRequestOptions } from '../models/http.models';
import { NhApiService } from './nh-api.service';
import { NhBackgroundOperationAdministrationService } from './nh-background-operation-administration.service';

describe('NhBackgroundOperationAdministrationService', () => {
  let service: NhBackgroundOperationAdministrationService;
  let api: jasmine.SpyObj<NhApiService>;

  beforeEach(() => {
    api = jasmine.createSpyObj<NhApiService>('api', ['getCollection', 'get', 'post']);
    api.getCollection.and.returnValue(of(new CollectionHttpResponse<unknown>()));
    api.get.and.returnValue(of({}));
    api.post.and.returnValue(of({}));
    const config = new NhCommonModuleConfig();
    config.apiBaseUrl = 'https://api.example.test/';
    config.backgroundOperations = new BackgroundOperationsNhCommonModuleConfig({
      administrationUrlSuffix: '/admin/background-operations',
      listPageSize: 25
    });

    TestBed.configureTestingModule({
      providers: [
        { provide: NhCommonModuleConfig, useValue: config },
        { provide: NhApiService, useValue: api }
      ]
    });
    service = TestBed.inject(NhBackgroundOperationAdministrationService);
  });

  it('lists operations of all owners from the configured administration endpoint', () => {
    service.list().subscribe();

    const [url, request] = api.getCollection.calls.mostRecent().args;
    expect(url).toBe('https://api.example.test/admin/background-operations');
    expect(request).toEqual(jasmine.any(NhBackgroundOperationAdministrationCollectionHttpRequestOptions));
    expect((request as NhBackgroundOperationAdministrationCollectionHttpRequestOptions).itemsPerPage).toBe(25);
  });

  it('requests operation details after an event sequence', () => {
    service.get('operation/1', 7).subscribe();

    const [url, options] = api.get.calls.mostRecent().args;
    expect(url).toBe('https://api.example.test/admin/background-operations/operation%2F1');
    expect((options as HttpRequestOptions).params?.get('eventsAfterSequence')).toBe('7');
  });

  it('posts administrator cancellation and retry to the operation', () => {
    service.cancel('operation-1').subscribe();
    service.retry('operation-1').subscribe();

    expect(api.post.calls.argsFor(0)[0]).toBe('https://api.example.test/admin/background-operations/operation-1/cancel');
    expect(api.post.calls.argsFor(1)[0]).toBe('https://api.example.test/admin/background-operations/operation-1/retry');
  });
});
