import {
  HttpHeaders,
  HttpParams,
  provideHttpClient
} from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting
} from '@angular/common/http/testing';
import {TestBed} from '@angular/core/testing';
import {BehaviorSubject, of} from 'rxjs';

import {NhAuthorization} from '../models/auth.models';
import {NhCommonModuleConfig} from '../models/config.models';
import {HttpRequestOptions} from '../models/http.models';
import {NhApiService} from './nh-api.service';
import {NhAuthService} from './nh-auth.service';
import {NhBaseApiService} from './nh-base-api.service';

describe('NhApiService PATCH requests', () => {
  let apiService: NhApiService;
  let httpTesting: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: NhCommonModuleConfig,
          useValue: new NhCommonModuleConfig({
            apiBaseUrl: '/api',
            language: 'en',
            culture: 'en-US'
          })
        },
        {
          provide: NhAuthService,
          useValue: {
            authSubject: new BehaviorSubject<NhAuthorization | undefined>(undefined)
          }
        }
      ]
    });

    apiService = TestBed.inject(NhApiService);
    httpTesting = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpTesting.verify());

  it('sends the supplied partial object with PATCH and regular request options', () => {
    const partialUpdate = {status: 'Active', description: null};
    const options = new HttpRequestOptions({
      headers: new HttpHeaders({'X-Sample': 'partial-update'}),
      params: new HttpParams().set('language', 'nl')
    });

    apiService.patch<void>('/api/projects/project-id', partialUpdate, options).subscribe();

    const request = httpTesting.expectOne('/api/projects/project-id?language=nl&culture=en-US');
    expect(request.request.method).toBe('PATCH');
    expect(request.request.body).toEqual(partialUpdate);
    expect(request.request.headers.get('Content-Type')).toBe('application/json; charset=utf-8');
    expect(request.request.headers.get('X-Sample')).toBe('partial-update');
    expect(request.request.withCredentials).toBeTrue();

    request.flush(null, {status: 204, statusText: 'No Content'});
  });

  it('exposes the TaskResult convenience wrapper for PATCH', async () => {
    const resultPromise = apiService.patchResult<{id: string}>(
      '/api/projects/project-id',
      {description: 'Updated'}
    );

    const request = httpTesting.expectOne('/api/projects/project-id?language=en&culture=en-US');
    expect(request.request.method).toBe('PATCH');
    request.flush({id: 'project-id'});

    const result = await resultPromise;
    expect(result.data).toEqual({id: 'project-id'});
  });
});

describe('NhBaseApiService partial updates', () => {
  it('targets the entity route through NhApiService.patch', () => {
    const apiService = jasmine.createSpyObj<NhApiService>('NhApiService', ['patch']);
    apiService.patch.and.returnValue(of(undefined));
    const requestOptions = new HttpRequestOptions();
    const partialUpdate = {status: 'Active'};

    TestBed.configureTestingModule({
      providers: [
        {provide: NhAuthService, useValue: {}},
        {
          provide: NhCommonModuleConfig,
          useValue: new NhCommonModuleConfig({apiBaseUrl: '/api'})
        },
        {provide: NhApiService, useValue: apiService}
      ]
    });

    const service = TestBed.runInInjectionContext(() => new TestProjectApiService());
    service.updatePartial<void>('project-id', partialUpdate, requestOptions).subscribe();

    expect(apiService.patch).toHaveBeenCalledOnceWith(
      '/api/projects/project-id',
      partialUpdate,
      requestOptions
    );
  });
});

class TestProjectApiService extends NhBaseApiService {
  constructor() {
    super('projects');
  }
}
