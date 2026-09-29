import {provideHttpClient} from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting
} from '@angular/common/http/testing';
import {Injectable, PLATFORM_ID} from '@angular/core';
import {TestBed} from '@angular/core/testing';

import {Base64} from 'js-base64';

import {
  AuthenticateModel,
  Claim,
  NhAuthorization,
  NhTwoFactorFailureCodes,
  NhTwoFactorMethods,
  NhTwoFactorVerifyModel
} from '../models/auth.models';
import {
  AuthenticationNhCommonModuleConfig,
  EndpointsAuthenticationNhCommonModuleConfig,
  NhCommonModuleConfig
} from '../models/config.models';
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

describe('BaseNhAuthService two-factor authentication', () => {
  let authService: TestAuthService;
  let httpTesting: HttpTestingController;

  const challengeResponse = {
    token: null,
    validTo: null,
    refreshToken: null,
    refreshValidTo: null,
    issuer: null,
    twoFactor: {
      status: 'two-factor-required',
      challengeToken: 'protected-challenge',
      expiresAt: '2099-01-01T00:00:00Z',
      methods: [NhTwoFactorMethods.authenticator, NhTwoFactorMethods.recoveryCode]
    }
  };

  const sessionResponse = {
    token: 'access-token',
    validTo: '2099-01-01T00:00:00Z',
    refreshToken: 'refresh-token',
    refreshValidTo: '2099-02-01T00:00:00Z',
    issuer: 'https://auth.example.test'
  };

  function configure(config: NhCommonModuleConfig) {
    localStorage.clear();

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        TestAuthService,
        {provide: PLATFORM_ID, useValue: 'server'},
        {provide: NhCommonModuleConfig, useValue: config}
      ]
    });

    authService = TestBed.inject(TestAuthService);
    httpTesting = TestBed.inject(HttpTestingController);
  }

  async function signInWithChallenge() {
    const signIn = authService.authenticateInteractive(new AuthenticateModel({username: 'user@example.test', password: 'secret'}));
    httpTesting.expectOne('/auth-api/authentication/login?language=en').flush(challengeResponse);
    await signIn;
  }

  beforeEach(() => {
    configure(new NhCommonModuleConfig({
      authApiBaseUrl: '/auth-api',
      language: 'en',
      authenticationRealm: 'sample'
    }));
  });

  afterEach(() => {
    httpTesting.verify();
    localStorage.clear();
  });

  it('never stores a second-factor challenge as the authorization', async () => {
    const resultPromise = authService.authenticate(new AuthenticateModel({username: 'user@example.test', password: 'secret'}));
    httpTesting.expectOne('/auth-api/authentication/login?language=en').flush(challengeResponse);

    const result = await resultPromise;

    expect(result.isSuccess).toBeFalse();
    expect(result.items.map(item => item.name)).toEqual([NhTwoFactorFailureCodes.Required]);
    expect(authService.isAuthenticated()).toBeFalse();
    expect(authService.getAuthorization(false)?.token ?? '').toBe('');
    expect(authService.getPendingTwoFactorChallenge()?.challengeToken).toBe('protected-challenge');
  });

  it('returns the challenge from an interactive sign-in', async () => {
    const resultPromise = authService.authenticateInteractive(new AuthenticateModel({username: 'user@example.test', password: 'secret'}));
    const request = httpTesting.expectOne('/auth-api/authentication/login?language=en');

    expect(request.request.body.realm).toBe('sample');
    request.flush(challengeResponse);

    const result = await resultPromise;

    expect(result.isSuccess).toBeTrue();
    expect(result.data?.status).toBe('two-factor-required');
    expect(result.data?.challenge?.methods).toEqual([NhTwoFactorMethods.authenticator, NhTwoFactorMethods.recoveryCode]);
    expect(result.data?.authorization).toBeUndefined();
  });

  it('stores a complete session from an interactive sign-in', async () => {
    const resultPromise = authService.authenticateInteractive(new AuthenticateModel({username: 'user@example.test', password: 'secret'}));
    httpTesting.expectOne('/auth-api/authentication/login?language=en').flush(sessionResponse);

    const result = await resultPromise;

    expect(result.data?.status).toBe('authenticated');
    expect(authService.getAuthorization()?.token).toBe('access-token');
    expect(authService.getAuthorization()?.realm).toBe('sample');
    expect(authService.getPendingTwoFactorChallenge()).toBeUndefined();
  });

  it('completes the challenge and stores the session', async () => {
    await signInWithChallenge();

    const resultPromise = authService.verifyTwoFactor(new NhTwoFactorVerifyModel({
      challengeToken: 'protected-challenge',
      method: NhTwoFactorMethods.authenticator,
      code: '123456'
    }));
    const request = httpTesting.expectOne('/auth-api/authentication/two-factor/verify?language=en');

    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual(jasmine.objectContaining({challengeToken: 'protected-challenge', code: '123456'}));
    expect(request.request.withCredentials).toBeTrue();
    request.flush(sessionResponse);

    const result = await resultPromise;

    expect(result.isSuccess).toBeTrue();
    expect(authService.getAuthorization()?.token).toBe('access-token');
    expect(authService.getPendingTwoFactorChallenge()).toBeUndefined();
  });

  it('keeps the challenge after a wrong code and drops it once it expired', async () => {
    await signInWithChallenge();

    const wrongCode = authService.verifyTwoFactor(new NhTwoFactorVerifyModel({challengeToken: 'protected-challenge', code: '000000'}));
    httpTesting.expectOne('/auth-api/authentication/two-factor/verify?language=en').flush(
      {[NhTwoFactorFailureCodes.InvalidCode]: ['nh-two-factor.invalid-code']},
      {status: 400, statusText: 'Bad Request', headers: {'Content-Type': 'application/json'}});

    expect((await wrongCode).items.map(item => item.name)).toEqual([NhTwoFactorFailureCodes.InvalidCode]);
    expect(authService.getPendingTwoFactorChallenge()).toBeDefined();

    const expired = authService.verifyTwoFactor(new NhTwoFactorVerifyModel({challengeToken: 'protected-challenge', code: '111111'}));
    httpTesting.expectOne('/auth-api/authentication/two-factor/verify?language=en').flush(
      {[NhTwoFactorFailureCodes.ChallengeExpired]: ['nh-two-factor.challenge-expired']},
      {status: 400, statusText: 'Bad Request', headers: {'Content-Type': 'application/json'}});

    expect((await expired).isSuccess).toBeFalse();
    expect(authService.getPendingTwoFactorChallenge()).toBeUndefined();
  });

  it('reads a challenge that an external sign-in passed in the URL fragment', () => {
    const fragment = '#nh-two-factor=' + Base64.encodeURI(JSON.stringify(challengeResponse.twoFactor));

    const challenge = authService.consumeExternalTwoFactorChallenge(fragment);

    expect(challenge?.challengeToken).toBe('protected-challenge');
    expect(authService.getPendingTwoFactorChallenge()?.challengeToken).toBe('protected-challenge');
    expect(authService.consumeExternalTwoFactorChallenge('#other=value')).toBeUndefined();
  });

  it('stores the renewed session after enabling two-factor authentication', async () => {
    TestBed.resetTestingModule();
    configure(new NhCommonModuleConfig({authApiBaseUrl: '/auth-api', language: 'en', authType: 'header'}));

    authService.setAuthorization(new NhAuthorization({
      token: 'old-access-token',
      refreshToken: 'old-refresh-token',
      validTo: '2099-01-01T00:00:00Z',
      claims: [new Claim({type: 'test', value: 'authenticated'})]
    }));

    const resultPromise = authService.confirmAuthenticator('123456');
    const request = httpTesting.expectOne('/auth-api/account/two-factor/authenticator/confirm?language=en');

    expect(request.request.headers.get('Authorization')).toBe('Bearer old-access-token');
    expect(request.request.body).toEqual({code: '123456'});
    request.flush({recoveryCodes: ['AAAAA-BBBBB'], session: sessionResponse});

    const result = await resultPromise;

    expect(result.data?.recoveryCodes).toEqual(['AAAAA-BBBBB']);
    expect(result.data?.sessionRenewed).toBeTrue();
    expect(authService.getAuthorization()?.token).toBe('access-token');
    expect(authService.getAuthorization()?.refreshToken).toBe('refresh-token');
    expect(authService.getAuthorization()?.claims.length).toBe(1);
  });

  it('uses the default two-factor routes when the consumer passed a partial endpoints object', async () => {
    TestBed.resetTestingModule();
    configure(new NhCommonModuleConfig({
      authApiBaseUrl: '/auth-api',
      language: 'en',
      authentication: new AuthenticationNhCommonModuleConfig({
        endpoints: {login: '/custom/login'} as EndpointsAuthenticationNhCommonModuleConfig
      })
    }));

    const resultPromise = authService.getTwoFactorStatus();
    httpTesting.expectOne('/auth-api/account/two-factor?language=en').flush({enabled: true, methods: ['authenticator']});

    const result = await resultPromise;
    expect(result.data?.enabled).toBeTrue();
  });
});

@Injectable()
class TestAuthService extends BaseNhAuthService<NhAuthorization> {
  protected initEmptyAuthorization(): NhAuthorization {
    return new NhAuthorization();
  }
}
