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
  NhTwoFactorReauthentication,
  NhTwoFactorVerifyModel
} from '../models/auth.models';
import {
  AuthenticationNhCommonModuleConfig,
  EndpointsAuthenticationNhCommonModuleConfig,
  NhCommonModuleConfig
} from '../models/config.models';
import {BaseNhAuthService} from './nh-auth.service';
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

@Injectable()
class TestAuthService extends BaseNhAuthService<NhAuthorization> {
  protected initEmptyAuthorization(): NhAuthorization {
    return new NhAuthorization();
  }
}
