import {provideHttpClient} from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
  TestRequest
} from '@angular/common/http/testing';
import {Injectable, PLATFORM_ID} from '@angular/core';
import {TestBed} from '@angular/core/testing';

import {Base64} from 'js-base64';

import {
  AuthenticateModel,
  Claim,
  NhAuthorization,
  NhBrowserAccount,
  NhBrowserAccountFailureCodes,
  NhTwoFactorFailureCodes,
  NhTwoFactorMethods,
  NhTwoFactorReauthentication,
  NhTwoFactorVerifyModel,
  RefreshTokenLoginAccountMutateModel
} from '../models/auth.models';
import {
  AuthenticationNhCommonModuleConfig,
  EndpointsAuthenticationNhCommonModuleConfig,
  MultipleAccountsAuthenticationNhCommonModuleConfig,
  NhCommonModuleConfig
} from '../models/config.models';
import {BaseNhAuthService, NH_SKIP_SERVER_SESSION_CHECK} from './nh-auth.service';
import {NhPasskeyClient} from './nh-passkey-client.service';

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

  for (const failure of ['network error', 'server error'] as const) {
    it(`ends the local session and reports the failure when logout meets a ${failure}`, async () => {
      authService.setAuthorization(new NhAuthorization({
        token: 'access-token',
        refreshToken: 'device-refresh-token',
        validTo: '2099-01-01T00:00:00Z',
        claims: [new Claim({type: 'test', value: 'authenticated'})]
      }));
      const emitted: Array<NhAuthorization | undefined> = [];
      const subscription = authService.authSubject.subscribe(value => emitted.push(value));

      const resultPromise = authService.logout();
      const request = httpTesting.expectOne('/auth-api/authentication/logout?language=en');
      expect(request.request.body).toEqual({refreshToken: 'device-refresh-token'});
      if (failure === 'network error') {
        request.error(new ProgressEvent('error'));
      } else {
        request.flush(null, {status: 500, statusText: 'Internal Server Error'});
      }

      const result = await resultPromise;
      subscription.unsubscribe();

      // The caller learns that the server did not end its session, while the browser keeps
      // nothing a next person on the device could use, also after a reload.
      expect(result.isSuccess).toBeFalse();
      expect(authService.isAuthenticated()).toBeFalse();
      expect(localStorage.getItem('at')).toBeNull();
      expect(authService.getAuthorization(false)).toEqual(new NhAuthorization());
      expect(emitted[emitted.length - 1]).toBeUndefined();
    });
  }

  it('removes a stored authorization whose access token already expired', () => {
    authService.setAuthorization(new NhAuthorization({
      token: 'expired-access-token',
      refreshToken: 'device-refresh-token',
      validTo: '2000-01-01T00:00:00Z',
      claims: [new Claim({type: 'test', value: 'authenticated'})]
    }));
    expect(authService.isAuthenticated()).toBeFalse();
    const emitted: Array<NhAuthorization | undefined> = [];
    const subscription = authService.authSubject.subscribe(value => emitted.push(value));

    authService.clearAuthorization();
    subscription.unsubscribe();

    expect(localStorage.getItem('at')).toBeNull();
    expect(authService.getAuthorization(false)?.refreshToken).toBeUndefined();
    expect(emitted).toEqual([jasmine.any(NhAuthorization), undefined]);
  });

  it('sends the user name that the refresh endpoint requires with the refresh token', async () => {
    const resultPromise = authService.authenticateRefreshToken(new RefreshTokenLoginAccountMutateModel({
      userName: 'user@example.test',
      refreshToken: 'device-refresh-token'
    }));
    const request = httpTesting.expectOne('/auth-api/authentication/refresh?language=en');

    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual(jasmine.objectContaining({
      userName: 'user@example.test',
      refreshToken: 'device-refresh-token'
    }));
    request.flush({token: 'access-token', validTo: '2099-01-01T00:00:00Z', refreshToken: 'next-refresh-token'});

    expect((await resultPromise).isSuccess).toBeTrue();
    expect(authService.getAuthorization()?.refreshToken).toBe('next-refresh-token');
  });

  it('changes nothing and emits nothing when there is no authorization to clear', () => {
    const emitted: Array<NhAuthorization | undefined> = [];
    const subscription = authService.authSubject.subscribe(value => emitted.push(value));

    authService.clearAuthorization();
    subscription.unsubscribe();

    expect(localStorage.getItem('at')).toBeNull();
    expect(emitted.length).toBe(1);
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

  const enrollmentResponse = {
    twoFactor: {
      status: 'enrollment-required',
      challengeToken: 'protected-enrollment',
      expiresAt: '2099-01-01T00:00:00Z',
      methods: [NhTwoFactorMethods.authenticator, NhTwoFactorMethods.passkey]
    }
  };

  const passkeyOptionsResponse = {
    options: {challenge: 'AQID'},
    ceremonyToken: 'protected-ceremony',
    expiresAt: '2099-01-01T00:00:00Z'
  };

  const badRequest = {status: 400, statusText: 'Bad Request', headers: {'Content-Type': 'application/json'}};

  let passkeyClient: jasmine.SpyObj<NhPasskeyClient>;

  function configure(config: NhCommonModuleConfig, platform: string = 'server') {
    localStorage.clear();
    passkeyClient = jasmine.createSpyObj<NhPasskeyClient>('NhPasskeyClient', ['isSupported', 'create', 'get']);

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        TestAuthService,
        {provide: PLATFORM_ID, useValue: platform},
        {provide: NhCommonModuleConfig, useValue: config},
        {provide: NhPasskeyClient, useValue: passkeyClient}
      ]
    });

    authService = TestBed.inject(TestAuthService);
    httpTesting = TestBed.inject(HttpTestingController);
  }

  /** Lets the service continue after an awaited passkey prompt. */
  function settle(): Promise<void> {
    return new Promise(resolve => setTimeout(resolve));
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

  it('keeps a required enrollment as the pending step', async () => {
    const resultPromise = authService.authenticate(new AuthenticateModel({username: 'officer@example.test', password: 'secret'}));
    httpTesting.expectOne('/auth-api/authentication/login?language=en').flush(enrollmentResponse);

    const result = await resultPromise;

    expect(result.items.map(item => item.name)).toEqual([NhTwoFactorFailureCodes.EnrollmentRequired]);
    expect(authService.isAuthenticated()).toBeFalse();
    expect(authService.getPendingTwoFactorChallenge()?.status).toBe('enrollment-required');
  });

  it('completes a required enrollment and stores the session', async () => {
    const signIn = authService.authenticateInteractive(new AuthenticateModel({username: 'officer@example.test', password: 'secret'}));
    httpTesting.expectOne('/auth-api/authentication/login?language=en').flush(enrollmentResponse);
    expect((await signIn).data?.status).toBe('enrollment-required');

    const resultPromise = authService.confirmEnrollmentAuthenticator('protected-enrollment', '123456');
    const request = httpTesting.expectOne('/auth-api/authentication/two-factor/enrollment/authenticator/confirm?language=en');

    expect(request.request.body).toEqual({enrollmentToken: 'protected-enrollment', code: '123456'});
    request.flush({recoveryCodes: ['AAAAA-BBBBB'], session: sessionResponse});

    const result = await resultPromise;

    expect(result.data?.recoveryCodes).toEqual(['AAAAA-BBBBB']);
    expect(result.data?.authorization?.token).toBe('access-token');
    expect(authService.getAuthorization()?.token).toBe('access-token');
    expect(authService.getPendingTwoFactorChallenge()).toBeUndefined();
  });

  it('remembers the device for header clients and forgets it again', async () => {
    TestBed.resetTestingModule();
    configure(new NhCommonModuleConfig({
      authApiBaseUrl: '/auth-api',
      language: 'en',
      authType: 'header',
      authenticationRealm: 'sample'
    }), 'browser');

    await signInWithChallenge();

    const verify = authService.verifyTwoFactor(new NhTwoFactorVerifyModel({
      challengeToken: 'protected-challenge',
      code: '123456',
      rememberDevice: true
    }));
    const verifyRequest = httpTesting.expectOne('/auth-api/authentication/two-factor/verify?language=en');
    expect(verifyRequest.request.body.rememberDevice).toBeTrue();
    verifyRequest.flush({...sessionResponse, rememberDeviceToken: 'device-token'});

    expect((await verify).isSuccess).toBeTrue();
    expect('rememberDeviceToken' in authService.getAuthorization()!).toBeFalse();

    const nextSignIn = authService.authenticateInteractive(new AuthenticateModel({username: 'user@example.test', password: 'secret'}));
    const nextRequest = httpTesting.expectOne('/auth-api/authentication/login?language=en');
    expect(nextRequest.request.body.rememberDeviceToken).toBe('device-token');
    nextRequest.flush(sessionResponse);
    await nextSignIn;

    const forget = authService.forgetTwoFactorDevices(new NhTwoFactorReauthentication({password: 'secret'}));
    httpTesting.expectOne('/auth-api/account/two-factor/forget-devices?language=en').flush({session: sessionResponse});
    expect((await forget).isSuccess).toBeTrue();

    const afterForget = authService.authenticateInteractive(new AuthenticateModel({username: 'user@example.test', password: 'secret'}));
    const afterForgetRequest = httpTesting.expectOne('/auth-api/authentication/login?language=en');
    expect(afterForgetRequest.request.body.rememberDeviceToken).toBeUndefined();
    afterForgetRequest.flush(sessionResponse);
    await afterForget;
  });

  it('never stores a remember-device token for cookie clients', async () => {
    TestBed.resetTestingModule();
    configure(new NhCommonModuleConfig({authApiBaseUrl: '/auth-api', language: 'en', authType: 'cookie'}), 'browser');

    await signInWithChallenge();
    const verify = authService.verifyTwoFactor(new NhTwoFactorVerifyModel({challengeToken: 'protected-challenge', code: '123456', rememberDevice: true}));
    httpTesting.expectOne('/auth-api/authentication/two-factor/verify?language=en').flush({...sessionResponse, rememberDeviceToken: 'device-token'});
    await verify;

    expect(Object.keys(localStorage).some(key => key.startsWith('nh-two-factor-device'))).toBeFalse();
  });

  it('e-mails a sign-in code for the pending challenge', async () => {
    const resultPromise = authService.sendTwoFactorEmailCode('protected-challenge');
    const request = httpTesting.expectOne('/auth-api/authentication/two-factor/email?language=en');

    expect(request.request.body).toEqual({challengeToken: 'protected-challenge'});
    request.flush({expiresAt: '2099-01-01T00:10:00Z', resendAvailableAt: '2099-01-01T00:01:00Z'});

    expect((await resultPromise).data?.resendAvailableAt).toBe('2099-01-01T00:01:00Z');
  });

  it('completes a challenge with a passkey', async () => {
    await signInWithChallenge();
    passkeyClient.get.and.resolveTo({id: 'credential'});

    const resultPromise = authService.verifyTwoFactorWithPasskey('protected-challenge');
    const optionsRequest = httpTesting.expectOne('/auth-api/authentication/two-factor/passkey/options?language=en');
    expect(optionsRequest.request.body).toEqual({challengeToken: 'protected-challenge'});
    optionsRequest.flush(passkeyOptionsResponse);
    await settle();

    expect(passkeyClient.get).toHaveBeenCalledWith({challenge: 'AQID'});
    const verifyRequest = httpTesting.expectOne('/auth-api/authentication/two-factor/passkey?language=en');
    expect(verifyRequest.request.body).toEqual({
      challengeToken: 'protected-challenge',
      ceremonyToken: 'protected-ceremony',
      credential: {id: 'credential'},
      rememberDevice: false
    });
    verifyRequest.flush(sessionResponse);

    expect((await resultPromise).data?.token).toBe('access-token');
    expect(authService.getPendingTwoFactorChallenge()).toBeUndefined();
  });

  it('signs in with a passkey without a username', async () => {
    passkeyClient.get.and.resolveTo({id: 'credential'});

    const resultPromise = authService.signInWithPasskey();
    httpTesting.expectOne('/auth-api/authentication/passkey/options?language=en').flush(passkeyOptionsResponse);
    await settle();

    const signInRequest = httpTesting.expectOne('/auth-api/authentication/passkey/login?language=en');
    expect(signInRequest.request.body.ceremonyToken).toBe('protected-ceremony');
    expect(signInRequest.request.body.credential).toEqual({id: 'credential'});
    signInRequest.flush(sessionResponse);

    const result = await resultPromise;

    expect(result.data?.status).toBe('authenticated');
    expect(authService.getAuthorization()?.token).toBe('access-token');
  });

  it('reports a cancelled passkey prompt without registering a passkey', async () => {
    passkeyClient.create.and.rejectWith(new DOMException('The operation was cancelled.', 'NotAllowedError'));

    const resultPromise = authService.registerPasskey('Laptop');
    httpTesting.expectOne('/auth-api/account/passkeys/options?language=en').flush(passkeyOptionsResponse);

    const result = await resultPromise;

    expect(result.items.map(item => item.name)).toEqual([NhTwoFactorFailureCodes.PasskeyUnavailable]);
  });

  it('registers, renames and removes passkeys', async () => {
    passkeyClient.create.and.resolveTo({id: 'new-credential'});

    const registration = authService.registerPasskey('Laptop');
    httpTesting.expectOne('/auth-api/account/passkeys/options?language=en').flush(passkeyOptionsResponse);
    await settle();

    const registrationRequest = httpTesting.expectOne(request =>
      request.method === 'POST' && request.url === '/auth-api/account/passkeys');
    expect(registrationRequest.request.body).toEqual({
      ceremonyToken: 'protected-ceremony',
      credential: {id: 'new-credential'},
      name: 'Laptop'
    });
    registrationRequest.flush({recoveryCodes: ['AAAAA-BBBBB']});
    expect((await registration).data?.recoveryCodes).toEqual(['AAAAA-BBBBB']);

    const passkeys = authService.getPasskeys();
    httpTesting.expectOne(request => request.method === 'GET' && request.url === '/auth-api/account/passkeys')
      .flush([{id: 'AQID', name: 'Laptop', createdAt: '2099-01-01T00:00:00Z', isBackedUp: true, transports: ['internal']}]);
    expect((await passkeys).data?.[0].name).toBe('Laptop');

    const rename = authService.renamePasskey('AQID', 'Work laptop');
    const renameRequest = httpTesting.expectOne('/auth-api/account/passkeys/AQID?language=en');
    expect(renameRequest.request.method).toBe('PUT');
    renameRequest.flush(null, {status: 204, statusText: 'No Content'});
    expect((await rename).isSuccess).toBeTrue();

    const removal = authService.removePasskey('AQID', new NhTwoFactorReauthentication({password: 'secret'}));
    const removalRequest = httpTesting.expectOne('/auth-api/account/passkeys/AQID/remove?language=en');
    expect(removalRequest.request.body).toEqual({reauthentication: jasmine.objectContaining({password: 'secret'})});
    removalRequest.flush({}, {status: 200, statusText: 'OK'});
    expect((await removal).isSuccess).toBeTrue();
  });

  it('starts the two-factor administration actions', async () => {
    const reset = authService.resetUserTwoFactor('7b9f0c4e-0000-4000-8000-000000000001');
    httpTesting.expectOne('/auth-api/authentication/two-factor/users/7b9f0c4e-0000-4000-8000-000000000001/reset?language=en')
      .flush(null, {status: 204, statusText: 'No Content'});
    expect((await reset).isSuccess).toBeTrue();

    const reminders = authService.startTwoFactorEnrollmentReminders('reminders-2099-01-01');
    const remindersRequest = httpTesting.expectOne('/auth-api/authentication/two-factor/operations/enrollment-reminders?language=en');
    expect(remindersRequest.request.body).toEqual({idempotencyKey: 'reminders-2099-01-01'});
    remindersRequest.flush({id: 'operation-1', operationType: 'nh-two-factor-enrollment-reminders'}, {status: 202, statusText: 'Accepted'});
    expect((await reminders).data?.id).toBe('operation-1');

    const revocation = authService.startTwoFactorSessionRevocation('revocation-2099-01-01');
    httpTesting.expectOne('/auth-api/authentication/two-factor/operations/session-revocation?language=en')
      .flush({[NhTwoFactorFailureCodes.ConfigurationInvalid]: ['nh-two-factor.configuration-invalid']}, badRequest);
    expect((await revocation).items.map(item => item.name)).toEqual([NhTwoFactorFailureCodes.ConfigurationInvalid]);
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

describe('BaseNhAuthService server session check', () => {
  let authService: TestAuthService;
  let httpTesting: HttpTestingController;
  const accountUrl = '/auth-api/account?language=en';

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

  function signIn(token: string): void {
    authService.setAuthorization(new NhAuthorization({
      token,
      validTo: '2099-01-01T00:00:00Z',
      claims: [new Claim({type: 'test', value: 'authenticated'})]
    }));
  }

  it('ends a session the server rejected and reports why', async () => {
    signIn('rejected-token');

    const check = authService.verifyServerSession();
    const request = httpTesting.expectOne(accountUrl);
    expect(request.request.headers.get('Authorization')).toBe('Bearer rejected-token');
    expect(request.request.context.get(NH_SKIP_SERVER_SESSION_CHECK)).toBeTrue();
    request.flush(null, {status: 401, statusText: 'Unauthorized'});

    expect(await check).toBeFalse();
    expect(authService.isAuthenticated()).toBeFalse();
    expect(authService.getSessionExpirationInformation().rejectedByServer).toBeTrue();

    signIn('next-token');
    expect(authService.getSessionExpirationInformation().rejectedByServer).toBeFalse();
  });

  it('shares one request and keeps a confirmed session without asking again', async () => {
    signIn('valid-token');

    const first = authService.verifyServerSession();
    const second = authService.verifyServerSession();
    httpTesting.expectOne(accountUrl).flush({});

    expect(await first).toBeTrue();
    expect(await second).toBeTrue();
    expect(await authService.verifyServerSession()).toBeTrue();
    httpTesting.expectNone(accountUrl);
    expect(authService.isAuthenticated()).toBeTrue();
  });

  it('keeps the session when the check fails for another reason', async () => {
    signIn('valid-token');

    const check = authService.verifyServerSession();
    httpTesting.expectOne(accountUrl).flush(null, {status: 503, statusText: 'Service Unavailable'});

    expect(await check).toBeTrue();
    expect(authService.isAuthenticated()).toBeTrue();
  });

  it('keeps a session that replaced the checked one during the check', async () => {
    signIn('old-token');

    const check = authService.verifyServerSession();
    signIn('new-token');
    httpTesting.expectOne(accountUrl).flush(null, {status: 401, statusText: 'Unauthorized'});

    expect(await check).toBeTrue();
    expect(authService.getAuthorization()?.token).toBe('new-token');
    expect(authService.getSessionExpirationInformation().rejectedByServer).toBeFalse();
  });

  it('does not ask the server without a session', async () => {
    expect(await authService.verifyServerSession()).toBeFalse();
    httpTesting.expectNone(accountUrl);
  });
});

describe('BaseNhAuthService multiple accounts', () => {
  const loginUrl = '/auth-api/authentication/login?language=en';
  const refreshUrl = '/auth-api/authentication/refresh?language=en';
  const logoutUrl = '/auth-api/authentication/logout?language=en';
  const accountUrl = '/auth-api/account?language=en';
  const hintCookieName = 'nh_test_account_count';
  const alice = {userName: 'alice.sign-in', userId: '7b9f0c4e-0000-4000-8000-00000000000a'};
  const bob = {userName: 'bob@example.test', userId: '7b9f0c4e-0000-4000-8000-00000000000b'};

  let authService: TestAuthService;
  let httpTesting: HttpTestingController;
  const tabs: BaseNhAuthService<NhAuthorization>[] = [];

  function configure(multipleAccounts: Partial<MultipleAccountsAuthenticationNhCommonModuleConfig> | undefined) {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        TestAuthService,
        {provide: PLATFORM_ID, useValue: 'browser'},
        {
          provide: NhCommonModuleConfig,
          useValue: new NhCommonModuleConfig({
            authApiBaseUrl: '/auth-api',
            language: 'en',
            authType: 'header',
            authentication: new AuthenticationNhCommonModuleConfig({
              multipleAccounts: multipleAccounts === undefined
                ? new MultipleAccountsAuthenticationNhCommonModuleConfig()
                : new MultipleAccountsAuthenticationNhCommonModuleConfig(multipleAccounts)
            })
          })
        }
      ]
    });

    authService = TestBed.inject(TestAuthService);
    httpTesting = TestBed.inject(HttpTestingController);
  }

  /** Another tab of the same browser: its own service instance on the same storage and locks. */
  function openTab<TService extends BaseNhAuthService<NhAuthorization>>(create: () => TService): TService {
    const tab = TestBed.runInInjectionContext(create);
    tabs.push(tab);
    return tab;
  }

  function accessToken(userId: string): string {
    return `eyJhbGciOiJIUzI1NiJ9.${Base64.encodeURI(JSON.stringify({nameid: userId}))}.signature`;
  }

  function sessionResponse(userId: string, refreshToken: string, validTo: string = '2099-01-01T00:00:00Z') {
    return {
      token: accessToken(userId),
      validTo: validTo,
      refreshToken: refreshToken,
      refreshValidTo: '2099-02-01T00:00:00Z',
      issuer: 'https://auth.example.test'
    };
  }

  /** An access token that expires within the renewal margin, so a renewal has to rotate. */
  function expiringSoon(): string {
    return new Date(Date.now() + 5_000).toISOString();
  }

  async function signIn(
    service: BaseNhAuthService<NhAuthorization>,
    account: {userName: string, userId: string},
    refreshToken: string,
    validTo?: string): Promise<void> {
    const result = service.authenticateInteractive(new AuthenticateModel({username: account.userName, password: 'secret'}));
    httpTesting.expectOne(loginUrl).flush(sessionResponse(account.userId, refreshToken, validTo));
    expect((await result).isSuccess).toBeTrue();
  }

  /** Waits until the request arrives; the session lock grants it asynchronously. */
  async function nextRequest(url: string): Promise<TestRequest> {
    for (let attempt = 0; attempt < 500; attempt++) {
      const requests = httpTesting.match(url);
      if (requests.length > 0) {
        expect(requests.length).withContext(`concurrent requests to ${url}`).toBe(1);
        return requests[0];
      }

      await new Promise(resolve => setTimeout(resolve));
    }

    throw new Error(`No request to ${url} arrived.`);
  }

  function storedRefreshToken(userId: string): string | undefined {
    const directory = JSON.parse(Base64.decode(localStorage.getItem('nh-accounts') ?? ''));
    return directory.accounts.find((account: {userId: string}) => account.userId === userId)?.authorization?.refreshToken;
  }

  function hintCookie(): string | undefined {
    return document.cookie
      .split(';')
      .map(cookie => cookie.trim())
      .find(cookie => cookie.startsWith(`${hintCookieName}=`))
      ?.substring(hintCookieName.length + 1);
  }

  beforeEach(() => {
    localStorage.clear();
    document.cookie = `${hintCookieName}=; Max-Age=0; Path=/`;
  });

  afterEach(() => {
    for (const tab of tabs.splice(0)) {
      tab.ngOnDestroy();
    }

    httpTesting.verify();
    localStorage.clear();
    document.cookie = `${hintCookieName}=; Max-Age=0; Path=/`;
  });

  describe('when enabled', () => {
    beforeEach(() => configure({enabled: true, hintCookieName: hintCookieName}));

    it('keeps both accounts with their own refresh tokens and switches without consuming the other one', async () => {
      await signIn(authService, alice, 'refresh-alice');
      await signIn(authService, bob, 'refresh-bob');

      expect(authService.accounts.map(account => [account.userId, account.isActive])).toEqual([
        [bob.userId, true],
        [alice.userId, false]
      ]);
      expect(authService.accounts.every(account => account instanceof NhBrowserAccount)).toBeTrue();
      expect(JSON.stringify(authService.accounts)).not.toContain('refresh-');
      expect(authService.getAuthorization()?.refreshToken).toBe('refresh-bob');

      const toAlice = authService.switchAccount(alice.userId);
      const aliceRefresh = await nextRequest(refreshUrl);
      expect(aliceRefresh.request.body).toEqual({userName: alice.userName, refreshToken: 'refresh-alice'});
      aliceRefresh.flush(sessionResponse(alice.userId, 'refresh-alice-2'));

      expect((await toAlice).data?.refreshToken).toBe('refresh-alice-2');
      expect(authService.getAuthorization()?.refreshToken).toBe('refresh-alice-2');
      expect(storedRefreshToken(bob.userId)).toBe('refresh-bob');
      expect(authService.accounts[0].userId).toBe(alice.userId);
      expect(authService.accounts[0].isActive).toBeTrue();

      const toBob = authService.switchAccount(bob.userId);
      const bobRefresh = await nextRequest(refreshUrl);
      expect(bobRefresh.request.body).toEqual({userName: bob.userName, refreshToken: 'refresh-bob'});
      bobRefresh.flush(sessionResponse(bob.userId, 'refresh-bob-2'));

      expect((await toBob).isSuccess).toBeTrue();
      expect(storedRefreshToken(alice.userId)).toBe('refresh-alice-2');
      expect(storedRefreshToken(bob.userId)).toBe('refresh-bob-2');
    });

    it('lets two tabs renew the same session with exactly one request to the refresh endpoint', async () => {
      await signIn(authService, alice, 'refresh-alice', expiringSoon());
      const otherTab = openTab(() => new TestAuthService());

      const first = authService.renewSession();
      const sameTab = authService.renewSession();
      const second = otherTab.renewSession();
      expect(sameTab).toBe(first);

      const refresh = await nextRequest(refreshUrl);
      expect(refresh.request.body).toEqual({userName: alice.userName, refreshToken: 'refresh-alice'});
      refresh.flush(sessionResponse(alice.userId, 'refresh-alice-2'));

      const [firstResult, secondResult] = await Promise.all([first, second]);
      httpTesting.expectNone(refreshUrl);

      expect(firstResult.data?.refreshToken).toBe('refresh-alice-2');
      expect(secondResult.data?.refreshToken).toBe('refresh-alice-2');
      expect(otherTab.getAuthorization()?.refreshToken).toBe('refresh-alice-2');
      expect(storedRefreshToken(alice.userId)).toBe('refresh-alice-2');
    });

    it('serializes renewals with a storage lease when the browser has no Web Locks', async () => {
      await signIn(authService, alice, 'refresh-alice', expiringSoon());
      const firstTab = openTab(() => new LeaseTestAuthService());
      const secondTab = openTab(() => new LeaseTestAuthService());

      const first = firstTab.renewSession();
      const second = secondTab.renewSession();

      (await nextRequest(refreshUrl)).flush(sessionResponse(alice.userId, 'refresh-alice-2'));

      const results = await Promise.all([first, second]);
      httpTesting.expectNone(refreshUrl);
      expect(results.map(result => result.data?.refreshToken)).toEqual(['refresh-alice-2', 'refresh-alice-2']);
      expect(Object.keys(localStorage).some(key => key.startsWith('nh-lock:'))).toBeFalse();
    });

    it('uses the stored session when another tab rotated the token outside the lock', async () => {
      await signIn(authService, alice, 'refresh-alice', expiringSoon());
      const olderTab = openTab(() => new TestAuthService());

      const renewal = authService.renewSession();
      const refresh = await nextRequest(refreshUrl);

      // A tab that rotates without the lock stores its new token while this request is on its way.
      olderTab.setAuthorization(new NhAuthorization({
        ...olderTab.getAuthorization(false),
        token: accessToken(alice.userId),
        validTo: '2099-01-01T00:00:00Z',
        refreshToken: 'refresh-alice-sibling'
      }));
      refresh.flush({'Invalid refresh token': ['Invalid refresh token']}, {status: 400, statusText: 'Bad Request'});

      const result = await renewal;
      expect(result.isSuccess).toBeTrue();
      expect(result.data?.refreshToken).toBe('refresh-alice-sibling');
      expect(authService.getAuthorization()?.refreshToken).toBe('refresh-alice-sibling');
      expect(authService.accounts.map(account => account.userId)).toEqual([alice.userId]);
    });

    it('keeps a session whose access token stays valid and rotates it when forced', async () => {
      await signIn(authService, alice, 'refresh-alice');

      const kept = await authService.renewSession(alice.userId);
      httpTesting.expectNone(refreshUrl);
      expect(kept.data?.refreshToken).toBe('refresh-alice');

      const forced = authService.renewSession(alice.userId, {force: true});
      (await nextRequest(refreshUrl)).flush(sessionResponse(alice.userId, 'refresh-alice-2'));
      expect((await forced).data?.refreshToken).toBe('refresh-alice-2');
    });

    it('refuses to renew a session that is no longer the active one', async () => {
      await signIn(authService, alice, 'refresh-alice');
      await signIn(authService, bob, 'refresh-bob');

      const result = await authService.renewSession(alice.userId);

      httpTesting.expectNone(refreshUrl);
      expect(result.items.map(item => item.name)).toEqual([NhBrowserAccountFailureCodes.NotActive]);
    });

    it('revokes exactly the signed-out account and keeps the active session\'s cookies', async () => {
      await signIn(authService, alice, 'refresh-alice');
      await signIn(authService, bob, 'refresh-bob');

      const signOut = authService.logoutAccount(alice.userId);
      const logout = await nextRequest(logoutUrl);
      expect(logout.request.body).toEqual({refreshToken: 'refresh-alice', keepAuthenticationCookies: true});
      logout.flush(null, {status: 204, statusText: 'No Content', headers: {'X-NH-Authentication-Cookies': 'kept'}});

      expect((await signOut).isSuccess).toBeTrue();
      httpTesting.expectNone(refreshUrl);
      expect(authService.accounts.map(account => account.userId)).toEqual([bob.userId]);
      expect(authService.getAuthorization()?.refreshToken).toBe('refresh-bob');
    });

    it('writes the active session again when the server does not confirm it kept the cookies', async () => {
      await signIn(authService, alice, 'refresh-alice');
      await signIn(authService, bob, 'refresh-bob');

      const signOut = authService.logoutAccount(alice.userId);
      (await nextRequest(logoutUrl)).flush(null, {status: 204, statusText: 'No Content'});

      // An older server expired the browser's cookies, so the active session is renewed.
      const restore = await nextRequest(refreshUrl);
      expect(restore.request.body).toEqual({userName: bob.userName, refreshToken: 'refresh-bob'});
      restore.flush(sessionResponse(bob.userId, 'refresh-bob-2'));

      expect((await signOut).isSuccess).toBeTrue();
      expect(authService.accounts.map(account => account.userId)).toEqual([bob.userId]);
      expect(authService.getAuthorization()?.refreshToken).toBe('refresh-bob-2');
    });

    it('signs out every account and keeps nothing when one revocation fails', async () => {
      await signIn(authService, alice, 'refresh-alice');
      await signIn(authService, bob, 'refresh-bob');

      const signOut = authService.logoutAllAccounts();
      const aliceLogout = await nextRequest(logoutUrl);
      expect(aliceLogout.request.body).toEqual({refreshToken: 'refresh-alice'});
      aliceLogout.error(new ProgressEvent('error'));
      const bobLogout = await nextRequest(logoutUrl);
      expect(bobLogout.request.body).toEqual({refreshToken: 'refresh-bob'});
      bobLogout.flush(null, {status: 204, statusText: 'No Content'});

      expect((await signOut).isSuccess).toBeFalse();
      expect(authService.accounts).toEqual([]);
      expect(authService.isAuthenticated()).toBeFalse();
      expect(localStorage.getItem('at')).toBeNull();
      expect(localStorage.getItem('nh-accounts')).toBeNull();
      expect(hintCookie()).toBeUndefined();
    });

    it('signs out the active account with logout() and keeps the others', async () => {
      await signIn(authService, alice, 'refresh-alice');
      await signIn(authService, bob, 'refresh-bob');

      const signOut = authService.logout();
      const logout = await nextRequest(logoutUrl);
      expect(logout.request.body).toEqual({refreshToken: 'refresh-bob'});
      logout.flush(null, {status: 204, statusText: 'No Content'});

      expect((await signOut).isSuccess).toBeTrue();
      expect(localStorage.getItem('at')).toBeNull();
      expect(authService.accounts.map(account => [account.userId, account.isActive])).toEqual([[alice.userId, false]]);
    });

    it('drops the whole directory once the server rejects a session', async () => {
      await signIn(authService, alice, 'refresh-alice');
      await signIn(authService, bob, 'refresh-bob');
      authService.setAuthorization(new NhAuthorization({
        ...authService.getAuthorization(),
        claims: [new Claim({type: 'test', value: 'authenticated'})]
      }));

      const check = authService.verifyServerSession();
      httpTesting.expectOne(accountUrl).flush(null, {status: 401, statusText: 'Unauthorized'});

      expect(await check).toBeFalse();
      expect(authService.getSessionExpirationInformation().rejectedByServer).toBeTrue();
      expect(authService.accounts).toEqual([]);
      expect(localStorage.getItem('nh-accounts')).toBeNull();
      expect(hintCookie()).toBeUndefined();
    });

    it('ends only the active account when its authorization is cleared', async () => {
      await signIn(authService, alice, 'refresh-alice');
      await signIn(authService, bob, 'refresh-bob');

      authService.clearAuthorization();

      expect(localStorage.getItem('at')).toBeNull();
      expect(authService.accounts.map(account => account.userId)).toEqual([alice.userId]);
      expect(storedRefreshToken(alice.userId)).toBe('refresh-alice');
    });

    it('forgets another account without revoking it and never forgets the active one', async () => {
      await signIn(authService, alice, 'refresh-alice');
      await signIn(authService, bob, 'refresh-bob');

      expect(await authService.forgetAccount(bob.userId)).toBeFalse();
      expect(await authService.forgetAccount(alice.userId.toUpperCase())).toBeTrue();
      expect(await authService.forgetAccount(alice.userId)).toBeFalse();

      httpTesting.expectNone(logoutUrl);
      expect(authService.accounts.map(account => account.userId)).toEqual([bob.userId]);
    });

    it('keeps the active account when signing in to another account fails', async () => {
      await signIn(authService, alice, 'refresh-alice');
      authService.setAuthorization(new NhAuthorization({
        ...authService.getAuthorization(),
        claims: [new Claim({type: 'test', value: 'authenticated'})]
      }));
      expect(authService.isAuthenticated()).toBeTrue();

      const failed = authService.authenticateInteractive(new AuthenticateModel({username: bob.userName, password: 'test-wrong'}));
      httpTesting.expectOne(loginUrl).flush({'Invalid credentials': ['Invalid credentials']}, {status: 400, statusText: 'Bad Request'});

      expect((await failed).isSuccess).toBeFalse();
      expect(authService.getAuthorization()?.refreshToken).toBe('refresh-alice');
      expect(authService.accounts.map(account => account.userId)).toEqual([alice.userId]);
    });

    it('reports an unknown account and a session that cannot be renewed', async () => {
      const unknown = await authService.switchAccount(bob.userId);
      expect(unknown.items.map(item => item.name)).toEqual([NhBrowserAccountFailureCodes.UnknownAccount]);

      const withoutSession = await authService.renewSession();
      expect(withoutSession.items.map(item => item.name)).toEqual([NhBrowserAccountFailureCodes.NotRenewable]);
    });

    it('never adds a session without a refresh token, such as an impersonation', () => {
      authService.setAuthorization(new NhAuthorization({
        token: accessToken(bob.userId),
        validTo: '2099-01-01T00:00:00Z',
        claims: [new Claim({type: 'test', value: 'authenticated'})]
      }));

      expect(authService.accounts).toEqual([]);
      expect(localStorage.getItem('nh-accounts')).toBeNull();
    });

    it('takes the user id and sign-in name from the profile once it is loaded', async () => {
      const passkeyLike = new NhAuthorization({
        token: '',
        validTo: '2099-01-01T00:00:00Z',
        refreshToken: 'refresh-bob',
        user: {id: bob.userId.toUpperCase(), email: bob.userName} as NhAuthorization['user'],
        claims: [new Claim({type: 'test', value: 'authenticated'})]
      });

      authService.setAuthorization(passkeyLike);

      expect(authService.accounts.map(account => [account.userId, account.userName, account.displayName])).toEqual([
        [bob.userId, bob.userName, bob.userName]
      ]);
    });

    it('forgets the least recently used account above the maximum', async () => {
      TestBed.resetTestingModule();
      configure({enabled: true, maximum: 2});

      await signIn(authService, alice, 'refresh-alice');
      await signIn(authService, bob, 'refresh-bob');
      await signIn(authService, {userName: 'carol@example.test', userId: '7b9f0c4e-0000-4000-8000-00000000000c'}, 'refresh-carol');

      expect(authService.accounts.map(account => account.userName)).toEqual(['carol@example.test', bob.userName]);
    });

    it('drops accounts whose refresh token expired', () => {
      const session = (refreshTokenExpires: string) => new NhAuthorization({
        token: accessToken(bob.userId),
        validTo: '2099-01-01T00:00:00Z',
        refreshToken: 'refresh-bob',
        refreshTokenExpires: refreshTokenExpires,
        user: {id: bob.userId, email: bob.userName} as NhAuthorization['user']
      });

      authService.setAuthorization(session('2099-02-01T00:00:00Z'));
      expect(authService.accounts.length).toBe(1);

      authService.setAuthorization(session('2000-01-01T00:00:00Z'));

      expect(authService.accounts).toEqual([]);
      expect(localStorage.getItem('nh-accounts')).toBeNull();
    });

    it('writes a hint cookie with only the number of accounts', async () => {
      expect(hintCookie()).toBeUndefined();

      await signIn(authService, alice, 'refresh-alice');
      expect(hintCookie()).toBe('1');

      await signIn(authService, bob, 'refresh-bob');
      expect(hintCookie()).toBe('2');
      expect(document.cookie).not.toContain(alice.userId);
      expect(document.cookie).not.toContain('refresh-');
    });

    it('follows a switch that another tab stored', async () => {
      await signIn(authService, alice, 'refresh-alice');
      await signIn(authService, bob, 'refresh-bob');
      const otherTab = openTab(() => new TestAuthService());
      const seen: Array<string | undefined> = [];
      const subscription = otherTab.authSubject.subscribe(value => seen.push(value?.refreshToken));
      const accountLists: string[][] = [];
      const accountsSubscription = otherTab.accountsChanged.subscribe(accounts =>
        accountLists.push(accounts.filter(account => account.isActive).map(account => account.userId)));

      const toAlice = authService.switchAccount(alice.userId);
      (await nextRequest(refreshUrl)).flush(sessionResponse(alice.userId, 'refresh-alice-2'));
      await toAlice;
      window.dispatchEvent(new StorageEvent('storage', {key: 'at', storageArea: localStorage}));
      window.dispatchEvent(new StorageEvent('storage', {key: 'nh-accounts', storageArea: localStorage}));
      subscription.unsubscribe();
      accountsSubscription.unsubscribe();

      expect(seen[seen.length - 1]).toBe('refresh-alice-2');
      expect(otherTab.getAuthorization()?.refreshToken).toBe('refresh-alice-2');
      expect(accountLists[accountLists.length - 1]).toEqual([alice.userId]);
    });

    it('never stores the access token of an account in the directory', async () => {
      await signIn(authService, alice, 'refresh-alice');
      await signIn(authService, bob, 'refresh-bob');

      const directory = Base64.decode(localStorage.getItem('nh-accounts') ?? '');
      expect(directory).not.toContain(accessToken(alice.userId));
      expect(directory).not.toContain(accessToken(bob.userId));
      expect(authService.getAuthorization()?.token).toBe(accessToken(bob.userId));
    });

    it('drops a profile that loaded while another tab switched to another account', async () => {
      await signIn(authService, alice, 'refresh-alice');
      await signIn(authService, bob, 'refresh-bob');
      const otherTab = openTab(() => new TestAuthService());

      const profile = authService.reloadAuthorizationProfile();
      const accountRequest = httpTesting.expectOne(accountUrl);

      const toAlice = otherTab.switchAccount(alice.userId);
      (await nextRequest(refreshUrl)).flush(sessionResponse(alice.userId, 'refresh-alice-2'));
      expect((await toAlice).isSuccess).toBeTrue();

      accountRequest.flush({user: {id: bob.userId, email: bob.userName}, claims: [{type: 'test', value: 'bob'}]});
      const result = await profile;

      expect(result.items.map(item => item.name)).toEqual([NhBrowserAccountFailureCodes.NotActive]);
      expect(authService.readActiveRefreshTokenForTest()).toBe('refresh-alice-2');
      expect(storedRefreshToken(bob.userId)).toBe('refresh-bob');
    });

    it('drops a profile of another account than the one it was requested for', async () => {
      authService.setAuthorization(new NhAuthorization({
        token: accessToken(alice.userId),
        validTo: '2099-01-01T00:00:00Z',
        refreshToken: 'refresh-alice',
        user: {id: alice.userId, email: 'alice@example.test'} as NhAuthorization['user'],
        claims: [new Claim({type: 'test', value: 'alice'})]
      }));

      const profile = authService.reloadAuthorizationProfile();
      httpTesting.expectOne(accountUrl).flush({user: {id: bob.userId, email: bob.userName}, claims: [{type: 'test', value: 'bob'}]});

      expect((await profile).isSuccess).toBeFalse();
      expect(authService.getAuthorization()?.user?.id).toBe(alice.userId);
      expect(authService.accounts.map(account => account.userId)).toEqual([alice.userId]);
    });

    it('never moves one account\'s refresh token onto another account', async () => {
      await signIn(authService, alice, 'refresh-alice');
      await signIn(authService, bob, 'refresh-bob');

      // An authorization that mixes Bob's profile with Alice's token.
      authService.setAuthorization(new NhAuthorization({
        token: '',
        validTo: '2099-01-01T00:00:00Z',
        refreshToken: 'refresh-alice',
        user: {id: bob.userId, email: bob.userName} as NhAuthorization['user']
      }));

      expect(storedRefreshToken(bob.userId)).toBe('refresh-bob');
      expect(storedRefreshToken(alice.userId)).toBe('refresh-alice');
    });

    it('keeps every account when a request fails while another account is signed out', async () => {
      await signIn(authService, alice, 'refresh-alice');
      await signIn(authService, bob, 'refresh-bob');
      authService.setAuthorization(new NhAuthorization({
        ...authService.getAuthorization(),
        claims: [new Claim({type: 'test', value: 'authenticated'})]
      }));
      await signIn(authService, {userName: 'carol@example.test', userId: '7b9f0c4e-0000-4000-8000-00000000000c'}, 'refresh-carol');
      const switchBack = authService.switchAccount(bob.userId);
      (await nextRequest(refreshUrl)).flush(sessionResponse(bob.userId, 'refresh-bob-2'));
      expect((await switchBack).isSuccess).toBeTrue();

      const signOut = authService.logoutAccount(alice.userId);
      (await nextRequest(logoutUrl)).flush(null, {status: 204, statusText: 'No Content'});
      const restore = await nextRequest(refreshUrl);

      // An older server expired the cookies; until the restore completes an API call answers 401.
      const check = authService.verifyServerSession();
      httpTesting.expectOne(accountUrl).flush(null, {status: 401, statusText: 'Unauthorized'});
      restore.flush(sessionResponse(bob.userId, 'refresh-bob-3'));

      expect((await signOut).isSuccess).toBeTrue();
      expect(await check).toBeTrue();
      expect(authService.getSessionExpirationInformation().rejectedByServer).toBeFalse();
      expect(authService.accounts.map(account => account.userId).sort()).toEqual([bob.userId, '7b9f0c4e-0000-4000-8000-00000000000c'].sort());
    });

    it('starts with a directory that was changed outside the library', () => {
      localStorage.setItem('nh-accounts', Base64.encode(JSON.stringify({
        version: 1,
        accounts: [
          {userId: 'a', userName: 'a', signedInAt: 1, lastUsedAt: 1, authorization: {refreshToken: 'x'}},
          {userId: 'b', userName: 'b', signedInAt: 2, lastUsedAt: 2, authorization: {refreshToken: 'y'}},
          {userId: 'c', userName: 'c', signedInAt: 'now', lastUsedAt: 'now', authorization: {refreshToken: 7}}
        ]
      })));
      localStorage.setItem('at', Base64.encode(JSON.stringify({token: 42, refreshToken: 'z'})));

      const tab = openTab(() => new TestAuthService());

      expect(tab.accounts).toEqual([]);
      expect(localStorage.getItem('nh-accounts')).toBeNull();
    });

    it('ignores a storage lease that claims to be held far into the future', async () => {
      await signIn(authService, alice, 'refresh-alice', expiringSoon());
      localStorage.setItem('nh-lock:nh-authentication-session', JSON.stringify({owner: 'elsewhere', expiresAt: Date.now() + 864_000_000}));
      const tab = openTab(() => new LeaseTestAuthService());

      const renewal = tab.renewSession();
      (await nextRequest(refreshUrl)).flush(sessionResponse(alice.userId, 'refresh-alice-2'));

      expect((await renewal).data?.refreshToken).toBe('refresh-alice-2');
    });

    it('refuses an invalid maximum or hint cookie name at startup', () => {
      for (const multipleAccounts of [{enabled: true, maximum: 0}, {enabled: true, hintCookieName: 'bad name;'}]) {
        TestBed.resetTestingModule();
        expect(() => configure(multipleAccounts)).toThrowError(/authentication\.multipleAccounts/);
        TestBed.resetTestingModule();
      }

      configure({enabled: true});
    });
  });

  describe('when off, as by default', () => {
    beforeEach(() => configure(undefined));

    it('keeps one authorization, stores no directory and writes no hint cookie', async () => {
      await signIn(authService, alice, 'refresh-alice');
      await signIn(authService, bob, 'refresh-bob');

      expect(authService.getAuthorization()?.refreshToken).toBe('refresh-bob');
      expect(authService.accounts).toEqual([]);
      expect(localStorage.getItem('nh-accounts')).toBeNull();
      expect(hintCookie()).toBeUndefined();
      expect(() => authService.switchAccount(alice.userId)).toThrowError(/multipleAccounts/);
      expect(() => authService.forgetAccount(alice.userId)).toThrowError(/multipleAccounts/);
      expect(() => authService.logoutAccount(alice.userId)).toThrowError(/multipleAccounts/);
      expect(() => authService.logoutAllAccounts()).toThrowError(/multipleAccounts/);
    });

    it('removes accounts and a hint cookie that an earlier opt-in left behind', () => {
      localStorage.setItem('nh-accounts', 'left-behind');
      document.cookie = `${hintCookieName}=2; Path=/`;
      TestBed.resetTestingModule();

      configure({enabled: false, hintCookieName: hintCookieName});

      expect(localStorage.getItem('nh-accounts')).toBeNull();
      expect(hintCookie()).toBeUndefined();
    });

    it('still ends the current session when a sign-in fails', async () => {
      authService.setAuthorization(new NhAuthorization({
        token: accessToken(alice.userId),
        refreshToken: 'refresh-alice',
        validTo: '2099-01-01T00:00:00Z',
        claims: [new Claim({type: 'test', value: 'authenticated'})]
      }));

      const failed = authService.authenticateInteractive(new AuthenticateModel({username: bob.userName, password: 'test-wrong'}));
      httpTesting.expectOne(loginUrl).flush({'Invalid credentials': ['Invalid credentials']}, {status: 400, statusText: 'Bad Request'});
      await failed;

      expect(localStorage.getItem('at')).toBeNull();
    });

    it('renews the single session once for concurrent callers', async () => {
      await signIn(authService, alice, 'refresh-alice', expiringSoon());
      const otherTab = openTab(() => new TestAuthService());

      const first = authService.renewSession();
      const second = otherTab.renewSession();
      const refresh = await nextRequest(refreshUrl);
      expect(refresh.request.body).toEqual({userName: alice.userName, refreshToken: 'refresh-alice'});
      refresh.flush(sessionResponse(alice.userId, 'refresh-alice-2'));

      const results = await Promise.all([first, second]);
      httpTesting.expectNone(refreshUrl);
      expect(results.map(result => result.data?.refreshToken)).toEqual(['refresh-alice-2', 'refresh-alice-2']);
      expect(localStorage.getItem('nh-accounts')).toBeNull();
    });
  });
});

@Injectable()
class TestAuthService extends BaseNhAuthService<NhAuthorization> {
  protected initEmptyAuthorization(): NhAuthorization {
    return new NhAuthorization();
  }

  /** The refresh token of the active session as every tab reads it. */
  readActiveRefreshTokenForTest(): string | undefined {
    return this.getAuthorization(false)?.refreshToken;
  }
}

/** A browser without the Web Locks API. */
@Injectable()
class LeaseTestAuthService extends TestAuthService {
  protected override sessionLockManager(): LockManager | undefined {
    return undefined;
  }
}
