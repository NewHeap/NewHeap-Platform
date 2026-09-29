import {ComponentFixture, TestBed} from '@angular/core/testing';
import {provideTranslateService, TranslateService} from '@ngx-translate/core';
import {
  NhAuthenticationStep,
  NhAuthenticatorSetup,
  NhAuthorization,
  NhAuthService,
  NhTwoFactorChallenge,
  NhTwoFactorChange,
  NhTwoFactorEnrollmentResult,
  NhTwoFactorFailureCodes,
  NhTwoFactorMethods,
  NhTwoFactorStatus,
  TaskResult
} from '@newheap/platform-common';
import {NhTwoFactorChallengeComponent} from './challenge/nh-two-factor-challenge.component';
import {NhTwoFactorEnrollmentComponent} from './enrollment/nh-two-factor-enrollment.component';
import {NhTwoFactorTranslationMerger} from './i18n/nh-two-factor-translations';
import {NhPasskeyLoginButtonComponent} from './passkey-login/nh-passkey-login-button.component';
import {NhTwoFactorSettingsComponent} from './settings/nh-two-factor-settings.component';

type FakeAuthService = jasmine.SpyObj<NhAuthService>;

function succeeded<T>(data: T): TaskResult<T> {
  const result = new TaskResult<T>();
  result.data = data;
  return result;
}

function failed<T>(code: string): TaskResult<T> {
  return new TaskResult<T>().withError(code, 'nh-two-factor.' + code.substring('two-factor-'.length));
}

function createAuth(passkeysSupported = false): FakeAuthService {
  const auth = jasmine.createSpyObj<NhAuthService>('NhAuthService', [
    'isPasskeySupported',
    'verifyTwoFactor',
    'verifyTwoFactorWithPasskey',
    'sendTwoFactorEmailCode',
    'beginEnrollmentAuthenticatorSetup',
    'confirmEnrollmentAuthenticator',
    'getTwoFactorStatus',
    'getPasskeys',
    'disableTwoFactor',
    'signInWithPasskey'
  ]);
  auth.isPasskeySupported.and.returnValue(passkeysSupported);
  return auth;
}

function configure(auth: FakeAuthService): void {
  TestBed.configureTestingModule({
    providers: [
      provideTranslateService({fallbackLang: 'en'}),
      {provide: NhAuthService, useValue: auth}
    ]
  });
  TestBed.inject(TranslateService).use('en');
}

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

function element<T extends HTMLElement>(fixture: ComponentFixture<unknown>, selector: string): T {
  const found = (fixture.nativeElement as HTMLElement).querySelector<T>(selector);
  if (!found) {
    throw new Error(`No element matches ${selector}.`);
  }
  return found;
}

function type(input: HTMLInputElement, value: string): void {
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

function click(fixture: ComponentFixture<unknown>, text: string): void {
  const buttons = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('button'));
  const button = buttons.find(candidate => candidate.textContent?.trim() === text);
  if (!button) {
    throw new Error(`No button reads "${text}".`);
  }
  button.click();
}

const challenge = new NhTwoFactorChallenge({
  status: 'two-factor-required',
  challengeToken: 'protected-challenge',
  expiresAt: '2099-01-01T00:00:00Z',
  methods: [NhTwoFactorMethods.authenticator, NhTwoFactorMethods.passkey, NhTwoFactorMethods.recoveryCode],
  rememberDeviceAvailable: true
});

describe('NhTwoFactorChallengeComponent', () => {
  let auth: FakeAuthService;

  beforeEach(() => {
    auth = createAuth();
    configure(auth);
  });

  function create(): ComponentFixture<NhTwoFactorChallengeComponent> {
    const fixture = TestBed.createComponent(NhTwoFactorChallengeComponent);
    fixture.componentRef.setInput('challenge', challenge);
    fixture.detectChanges();
    return fixture;
  }

  it('hides passkeys when the browser cannot use them and verifies an authenticator code', async () => {
    const fixture = create();
    const authorization = new NhAuthorization({token: 'access-token'});
    auth.verifyTwoFactor.and.resolveTo(succeeded(authorization));
    const authenticated = jasmine.createSpy('authenticated');
    fixture.componentInstance.authenticated.subscribe(authenticated);

    const methods = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('input[type=radio]'))
      .map(input => (input as HTMLInputElement).value);
    expect(methods).toEqual([NhTwoFactorMethods.authenticator, NhTwoFactorMethods.recoveryCode]);

    type(element<HTMLInputElement>(fixture, 'input[autocomplete=one-time-code]'), ' 123456 ');
    const remember = element<HTMLInputElement>(fixture, 'input[type=checkbox]');
    remember.checked = true;
    remember.dispatchEvent(new Event('change'));
    element<HTMLFormElement>(fixture, 'form').dispatchEvent(new Event('submit'));
    await settle(fixture);

    expect(auth.verifyTwoFactor).toHaveBeenCalledWith(jasmine.objectContaining({
      challengeToken: 'protected-challenge',
      method: NhTwoFactorMethods.authenticator,
      code: '123456',
      rememberDevice: true
    }));
    expect(authenticated).toHaveBeenCalledWith(authorization);
  });

  it('offers to remember the device only when the server allows it', () => {
    const fixture = TestBed.createComponent(NhTwoFactorChallengeComponent);
    fixture.componentRef.setInput('challenge', new NhTwoFactorChallenge({...challenge, rememberDeviceAvailable: false}));
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).querySelector('input[type=checkbox]')).toBeNull();
  });

  it('shows the failure and reports an ended challenge', async () => {
    const fixture = create();
    auth.verifyTwoFactor.and.resolveTo(failed(NhTwoFactorFailureCodes.ChallengeExpired));
    const expired = jasmine.createSpy('expired');
    fixture.componentInstance.expired.subscribe(expired);

    type(element<HTMLInputElement>(fixture, 'input[autocomplete=one-time-code]'), '123456');
    element<HTMLFormElement>(fixture, 'form').dispatchEvent(new Event('submit'));
    await settle(fixture);

    expect(element(fixture, '[role=alert]').textContent).toContain('The sign-in expired. Sign in again.');
    expect(element<HTMLInputElement>(fixture, 'input[autocomplete=one-time-code]').getAttribute('aria-invalid')).toBe('true');
    expect(expired).toHaveBeenCalled();
  });

  it('completes the challenge with a passkey when the browser supports it', async () => {
    TestBed.resetTestingModule();
    auth = createAuth(true);
    configure(auth);
    auth.verifyTwoFactorWithPasskey.and.resolveTo(succeeded(new NhAuthorization({token: 'access-token'})));

    const fixture = create();
    const passkey = element<HTMLInputElement>(fixture, `input[value=${NhTwoFactorMethods.passkey}]`);
    passkey.click();
    passkey.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).querySelector('input[autocomplete=one-time-code]')).toBeNull();
    click(fixture, 'Use a passkey');
    await settle(fixture);

    expect(auth.verifyTwoFactorWithPasskey).toHaveBeenCalledWith('protected-challenge', false);
  });
});

describe('NhTwoFactorEnrollmentComponent', () => {
  it('starts the authenticator, confirms it and emits after the recovery codes were saved', async () => {
    const auth = createAuth();
    configure(auth);
    auth.beginEnrollmentAuthenticatorSetup.and.resolveTo(succeeded(new NhAuthenticatorSetup({
      sharedKey: 'ABCD EFGH',
      authenticatorUri: 'otpauth://totp/Sample',
      qrCodeDataUri: 'data:image/png;base64,AAAA'
    })));
    const enrollmentResult = new NhTwoFactorEnrollmentResult({
      authorization: new NhAuthorization({token: 'access-token'}),
      recoveryCodes: ['AAAAA-BBBBB', 'CCCCC-DDDDD']
    });
    auth.confirmEnrollmentAuthenticator.and.resolveTo(succeeded(enrollmentResult));

    const fixture = TestBed.createComponent(NhTwoFactorEnrollmentComponent);
    fixture.componentRef.setInput('enrollment', new NhTwoFactorChallenge({
      status: 'enrollment-required',
      challengeToken: 'protected-enrollment',
      methods: [NhTwoFactorMethods.authenticator, NhTwoFactorMethods.passkey]
    }));
    const completed = jasmine.createSpy('completed');
    fixture.componentInstance.completed.subscribe(completed);
    await settle(fixture);

    expect(auth.beginEnrollmentAuthenticatorSetup).toHaveBeenCalledWith('protected-enrollment');
    expect(element(fixture, '.nh-two-factor__key').textContent).toContain('ABCD EFGH');
    expect(element<HTMLImageElement>(fixture, 'img').alt).toBe('QR code for your authenticator app');

    type(element<HTMLInputElement>(fixture, 'input[autocomplete=one-time-code]'), '654321');
    element<HTMLFormElement>(fixture, 'form').dispatchEvent(new Event('submit'));
    await settle(fixture);

    expect(auth.confirmEnrollmentAuthenticator).toHaveBeenCalledWith('protected-enrollment', '654321');
    expect(completed).not.toHaveBeenCalled();
    expect(element(fixture, '.nh-two-factor__codes').textContent).toContain('CCCCC-DDDDD');

    const continueButton = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('button'))
      .find(button => button.textContent?.trim() === 'Continue')!;
    expect(continueButton.disabled).toBeTrue();

    const saved = element<HTMLInputElement>(fixture, 'input[type=checkbox]');
    saved.checked = true;
    saved.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    continueButton.click();

    expect(completed).toHaveBeenCalledWith(enrollmentResult);
  });
});

describe('NhTwoFactorSettingsComponent', () => {
  it('asks for the password before turning two-factor authentication off', async () => {
    const auth = createAuth();
    configure(auth);
    auth.getTwoFactorStatus.and.resolveTo(succeeded(new NhTwoFactorStatus({
      enabled: true,
      methods: [NhTwoFactorMethods.authenticator, NhTwoFactorMethods.recoveryCode],
      availableMethods: [NhTwoFactorMethods.authenticator, NhTwoFactorMethods.recoveryCode],
      recoveryCodesLeft: 8
    })));
    auth.disableTwoFactor.and.resolveTo(succeeded(new NhTwoFactorChange({sessionRenewed: true})));

    const fixture = TestBed.createComponent(NhTwoFactorSettingsComponent);
    await settle(fixture);

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('8 recovery codes left');

    click(fixture, 'Turn off two-step verification');
    fixture.detectChanges();
    expect(auth.disableTwoFactor).not.toHaveBeenCalled();

    type(element<HTMLInputElement>(fixture, 'input[type=password]'), 'secret');
    element<HTMLFormElement>(fixture, 'form').dispatchEvent(new Event('submit'));
    await settle(fixture);

    expect(auth.disableTwoFactor).toHaveBeenCalledWith(jasmine.objectContaining({password: 'secret'}));
    expect(auth.getTwoFactorStatus).toHaveBeenCalledTimes(2);
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Your other sessions were signed out.');
  });
});

describe('NhPasskeyLoginButtonComponent', () => {
  it('renders nothing without passkey support', () => {
    configure(createAuth(false));

    const fixture = TestBed.createComponent(NhPasskeyLoginButtonComponent);
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).querySelector('button')).toBeNull();
  });

  it('reports a pending step from a passkey sign-in', async () => {
    const auth = createAuth(true);
    configure(auth);
    const step = new NhAuthenticationStep({status: 'two-factor-required', challenge: challenge});
    auth.signInWithPasskey.and.resolveTo(succeeded(step));

    const fixture = TestBed.createComponent(NhPasskeyLoginButtonComponent);
    const pending = jasmine.createSpy('pending');
    fixture.componentInstance.pending.subscribe(pending);
    fixture.detectChanges();

    click(fixture, 'Sign in with a passkey');
    await settle(fixture);

    expect(pending).toHaveBeenCalledWith(step);
  });
});

describe('NhTwoFactorTranslationMerger', () => {
  it('adds the missing texts and keeps the host overrides', () => {
    TestBed.configureTestingModule({
      providers: [provideTranslateService({fallbackLang: 'nl'})]
    });

    const translate = TestBed.inject(TranslateService);
    translate.setTranslation('nl', {'nh-two-factor': {'invalid-code': 'Eigen tekst'}});
    translate.use('nl');

    TestBed.inject(NhTwoFactorTranslationMerger).start();

    expect(translate.instant('nh-two-factor.invalid-code')).toBe('Eigen tekst');
    expect(translate.instant('nh-two-factor.ui.verify')).toBe('Controleren');
  });
});
