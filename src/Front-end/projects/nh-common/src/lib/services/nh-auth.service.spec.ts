import {provideHttpClient} from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting
} from '@angular/common/http/testing';
import {Injectable, PLATFORM_ID} from '@angular/core';
import {TestBed} from '@angular/core/testing';

import {Claim, NhAuthorization} from '../models/auth.models';
import {NhCommonModuleConfig} from '../models/config.models';
import {BaseNhAuthService} from './nh-auth.service';

describe('BaseNhAuthService logout', () => {
  let authService: TestAuthService;
  let httpTesting: HttpTestingController;

  beforeEach(() => {
    localStorage.clear();

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        TestAuthService,
        {provide: PLATFORM_ID, useValue: 'server'},
        {
          provide: NhCommonModuleConfig,
          useValue: new NhCommonModuleConfig({
            authApiBaseUrl: '/auth-api',
            language: 'en'
          })
        }
      ]
    });

    authService = TestBed.inject(TestAuthService);
    httpTesting = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpTesting.verify();
    localStorage.clear();
  });

  it('sends the current login refresh token when logging out', async () => {
    authService.setAuthorization(new NhAuthorization({
      token: 'access-token',
      refreshToken: 'device-refresh-token',
      validTo: '2099-01-01T00:00:00Z',
      claims: [new Claim({type: 'test', value: 'authenticated'})]
    }));

    const resultPromise = authService.logout();
    const request = httpTesting.expectOne('/auth-api/authentication/logout?language=en');

    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({refreshToken: 'device-refresh-token'});
    expect(request.request.withCredentials).toBeTrue();

    request.flush(null, {status: 204, statusText: 'No Content'});

    const result = await resultPromise;
    expect(result.isSuccess).toBeTrue();
    expect(authService.getAuthorization()).toEqual(new NhAuthorization());
  });
});

@Injectable()
class TestAuthService extends BaseNhAuthService<NhAuthorization> {
  protected initEmptyAuthorization(): NhAuthorization {
    return new NhAuthorization();
  }
}
