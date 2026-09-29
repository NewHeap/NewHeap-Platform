import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import {
  AuthenticateModel,
  Claim,
  ClaimTypes,
  ImpersonateAuthenticateModel,
  IsAuthenticatedPipe,
  NhAuthorization,
  NhAuthService,
  NhCommonModule,
  NhAuthenticatorSetup,
  NhDivision,
  NhTwoFactorMethod,
  NhTwoFactorMethods,
  NhTwoFactorReauthentication,
  NhTwoFactorVerifyModel,
  NhUser,
  RefreshTokenLoginAccountMutateModel,
  RevertImpersonateAuthenticateModel
} from '@newheap/platform-common';
import { RouterLink } from '@angular/router';
import { TranslateModule, TranslateService } from '@ngx-translate/core';
import { firstValueFrom, Observable } from 'rxjs';
import {
  AUTHORIZATION_DEMO_ACCOUNTS,
  AuthorizationDemoAccount,
  AuthorizationSampleApiService,
  HasProjectDivisionOrApplicationPermissionPipe,
  IsOneProjectPermissionGrantedPipe,
  IsOneProjectRoleGrantedPipe,
  SAMPLE_AUTHORIZATION_IDS,
  SampleAuthService,
  SampleClaimTypes,
  TWO_FACTOR_DEMO_ACCOUNT
} from 'sample-project-management-common';

@Component({
  selector: 'app-auth-playground',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    NhCommonModule,
    RouterLink,
    TranslateModule,
    IsOneProjectPermissionGrantedPipe,
    IsOneProjectRoleGrantedPipe,
    HasProjectDivisionOrApplicationPermissionPipe
  ],
  templateUrl: './auth-playground.component.html',
  styleUrl: './auth-playground.component.scss'
})
export class AuthPlaygroundComponent {
  readonly authService = inject(SampleAuthService);
  private readonly libraryAuthService = inject(NhAuthService);
  private readonly authorizationApi = inject(AuthorizationSampleApiService);
  private readonly translate = inject(TranslateService);
  readonly username = signal('sample@example.test');
  readonly password = signal('Sample123!');
  readonly impersonateUserId = signal('');
  readonly result = signal('');
  readonly twoFactorDemoAccount = TWO_FACTOR_DEMO_ACCOUNT;
  readonly twoFactorMethods = [NhTwoFactorMethods.authenticator, NhTwoFactorMethods.recoveryCode, NhTwoFactorMethods.email];
  readonly twoFactorMethod = signal<NhTwoFactorMethod>(NhTwoFactorMethods.authenticator);
  readonly twoFactorCode = signal('');
  readonly rememberDevice = signal(false);
  readonly twoFactorAdministrationUserId = signal('');
  readonly authenticatorSetup = signal<NhAuthenticatorSetup | undefined>(undefined);
  readonly pendingChallenge = signal(this.authService.getPendingTwoFactorChallenge());
  readonly demoAccounts = AUTHORIZATION_DEMO_ACCOUNTS;
  readonly authorizationIds = SAMPLE_AUTHORIZATION_IDS;
  readonly permission = 'app.project.manage';
  readonly projectClaim = new Claim({ type: ClaimTypes.Permission, value: this.permission });
  private readonly authenticatedPipe = new IsAuthenticatedPipe(this.libraryAuthService);

  readonly authorization = signal(this.authService.getAuthorization());
  readonly sessionInformation = toSignal(
    this.authService.sessionExpirationInformationChanged,
    { initialValue: this.authService.getSessionExpirationInformation() }
  );
  readonly authenticatedByPipe = computed(() => {
    this.authorization();
    return this.authenticatedPipe.transform();
  });
  readonly state = computed(() => ({
    authenticated: this.authService.isAuthenticated(),
    impersonating: this.authService.isImpersonating(),
    activeDivisionId: this.authService.getActiveDivisionId(),
    isManagerRole: this.authService.isOneRoleGranted(['sample-project-manager']),
    isViewerRole: this.authService.isOneRoleGranted(['sample-project-viewer']),
    canView: this.authService.isOnePermissionGranted(['app.project.view']),
    canManage: this.authService.isOnePermissionGranted(['app.project.manage']),
    canViewAndManage: this.authService.isAllPermissionsGranted(['app.project.view', 'app.project.manage']),
    isDivisionEditorRole: this.authService.isOneActiveDivisionRoleGranted(['sample-division-editor']),
    canViewActiveDivision: this.authService.isOneActiveDivisionPermissionGranted(['project.view']),
    canViewAlphaConfidential: this.authService.hasProjectDivisionOrApplicationPermission(
      SAMPLE_AUTHORIZATION_IDS.alphaProject,
      'confidential.view'
    ),
    canViewBetaConfidential: this.authService.hasProjectDivisionOrApplicationPermission(
      SAMPLE_AUTHORIZATION_IDS.betaProject,
      'confidential.view'
    ),
    isAlphaProjectEditor: this.authService.isOneProjectRoleGranted(
      SAMPLE_AUTHORIZATION_IDS.alphaProject,
      ['project-editor']
    ),
    session: this.sessionInformation(),
    claims: this.authorization()?.claims ?? []
  }));

  activateDemoAuthorization(validForMilliseconds = 30 * 60_000): void {
    const division = new NhDivision({
      id: SAMPLE_AUTHORIZATION_IDS.northDivision,
      name: 'Sample North',
      description: 'Local auth playground division',
      userSelectAllowed: true,
      timeZoneId: 'Europe/Amsterdam'
    });
    const authorization = new NhAuthorization({
      realm: 'sample-project-management',
      provider: 'playground',
      token: 'local-demo-token',
      validTo: new Date(Date.now() + validForMilliseconds).toISOString(),
      refreshToken: 'local-demo-refresh-token',
      refreshTokenExpires: new Date(Date.now() + 24 * 60 * 60_000).toISOString(),
      user: new NhUser({
        id: crypto.randomUUID(),
        email: this.username(),
        emailConfirmed: true,
        activeDivisionId: division.id,
        activeDivision: division,
        roles: ['sample-project-manager']
      }),
      divisions: [division],
      activeDivision: division,
      claims: [
        new Claim({ type: ClaimTypes.Permission, value: 'app.project.view' }),
        new Claim({ type: ClaimTypes.Permission, value: 'app.project.manage' }),
        new Claim({ type: ClaimTypes.Role, value: 'sample-project-manager' }),
        new Claim({ type: ClaimTypes.DivisionPermission, value: `${division.id}_project.view` }),
        new Claim({ type: ClaimTypes.DivisionRole, value: `${division.id}_sample-division-editor` }),
        new Claim({
          type: SampleClaimTypes.ProjectPermission,
          value: `${SAMPLE_AUTHORIZATION_IDS.alphaProject}_confidential.view`
        }),
        new Claim({
          type: SampleClaimTypes.ProjectRole,
          value: `${SAMPLE_AUTHORIZATION_IDS.alphaProject}_project-editor`
        })
      ]
    });

    this.authService.setAuthorization(authorization);
    this.authorization.set(authorization);
    this.result.set('Local authorization enabled; guards and pipes now use real claims.');
  }

  activateExpiringDemoAuthorization(): void {
    this.activateDemoAuthorization(10_000);
    this.result.set(this.translate.instant('project.expiring-session-active'));
  }

  clearAuthorization(): void {
    this.authService.clearAuthorization();
    this.authorization.set(this.authService.getAuthorization(false));
    this.result.set('Authorization and local token state have been cleared.');
  }

  async detectAuthenticationFlow(): Promise<void> {
    const flow = await this.authService.getAuthenticationFlow(this.username());
    this.result.set(JSON.stringify(flow, null, 2));
  }

  async requestMicrosoftRedirect(): Promise<void> {
    const redirect = await this.authService.getMicrosoftRedirectUrl(
      `${window.location.origin}/auth/microsoft/callback`,
      this.username()
    );
    this.result.set(JSON.stringify(redirect, null, 2));
  }

  async login(): Promise<void> {
    const response = await this.authService.authenticate(new AuthenticateModel({
      realm: 'sample-project-management',
      username: this.username(),
      password: this.password()
    }));
    this.authorization.set(this.authService.getAuthorization());
    this.result.set(JSON.stringify({ isSuccess: response.isSuccess, items: response.items }, null, 2));
  }

  async refresh(): Promise<void> {
    const current = this.authService.getAuthorization();
    const response = await this.authService.authenticateRefreshToken(
      new RefreshTokenLoginAccountMutateModel({
        token: current?.token ?? '',
        refreshToken: current?.refreshToken ?? ''
      })
    );
    this.authorization.set(this.authService.getAuthorization());
    this.result.set(JSON.stringify({ isSuccess: response.isSuccess, items: response.items }, null, 2));
  }

  async impersonate(): Promise<void> {
    const response = await this.authService.impersonate(
      new ImpersonateAuthenticateModel({ userId: this.impersonateUserId() })
    );
    this.authorization.set(this.authService.getAuthorization());
    this.result.set(JSON.stringify({ isSuccess: response.isSuccess, items: response.items }, null, 2));
  }

  async revertImpersonation(): Promise<void> {
    const response = await this.authService.impersonateRevert(new RevertImpersonateAuthenticateModel());
    this.authorization.set(this.authService.getAuthorization());
    this.result.set(JSON.stringify({ isSuccess: response.isSuccess, items: response.items }, null, 2));
  }

  async logout(): Promise<void> {
    const response = await this.authService.logout();
    this.authorization.set(this.authService.getAuthorization(false));
    this.result.set(JSON.stringify({ isSuccess: response.isSuccess, items: response.items }, null, 2));
  }

  selectTwoFactorDemoAccount(): void {
    this.username.set(this.twoFactorDemoAccount.email);
    this.password.set('Sample123!');
    this.result.set(this.translate.instant('project.two-factor-demo-account-selected', {
      key: this.twoFactorDemoAccount.authenticatorKey
    }));
  }

  /**
   * Preferred sign-in for applications with two-factor authentication: the result is either
   * a stored session or a challenge. A challenge is never stored as the authorization.
   */
  async interactiveLogin(): Promise<void> {
    const response = await this.authService.authenticateInteractive(new AuthenticateModel({
      realm: 'sample-project-management',
      username: this.username(),
      password: this.password()
    }));
    this.authorization.set(this.authService.getAuthorization());
    this.pendingChallenge.set(this.authService.getPendingTwoFactorChallenge());
    this.result.set(JSON.stringify({
      isSuccess: response.isSuccess,
      status: response.data?.status,
      challenge: response.data?.challenge,
      items: response.items
    }, null, 2));
  }

  async verifyTwoFactor(): Promise<void> {
    const challenge = this.authService.getPendingTwoFactorChallenge();
    const response = await this.authService.verifyTwoFactor(new NhTwoFactorVerifyModel({
      challengeToken: challenge?.challengeToken ?? '',
      method: this.twoFactorMethod(),
      code: this.twoFactorCode(),
      rememberDevice: this.rememberDevice()
    }));
    await this.afterSignInStep(response);
  }

  async sendTwoFactorEmailCode(): Promise<void> {
    const challenge = this.authService.getPendingTwoFactorChallenge();
    const response = await this.authService.sendTwoFactorEmailCode(challenge?.challengeToken ?? '');
    this.result.set(JSON.stringify(response, null, 2));
  }

  async verifyTwoFactorWithPasskey(): Promise<void> {
    const challenge = this.authService.getPendingTwoFactorChallenge();
    const response = await this.authService.verifyTwoFactorWithPasskey(
      challenge?.challengeToken ?? '',
      this.rememberDevice());
    await this.afterSignInStep(response);
  }

  /** Passwordless sign-in with a discoverable passkey; the policy may still ask for more. */
  async signInWithPasskey(): Promise<void> {
    const response = await this.authService.signInWithPasskey();
    await this.afterSignInStep(response, response.data?.status === 'authenticated');
  }

  /** Starts the authenticator enrollment that the policy requires during sign-in. */
  async beginEnrollmentSetup(): Promise<void> {
    const enrollment = this.authService.getPendingTwoFactorChallenge();
    const response = await this.authService.beginEnrollmentAuthenticatorSetup(enrollment?.challengeToken ?? '');
    this.authenticatorSetup.set(response.data);
    this.result.set(JSON.stringify({ isSuccess: response.isSuccess, sharedKey: response.data?.sharedKey, items: response.items }, null, 2));
  }

  async confirmEnrollment(): Promise<void> {
    const enrollment = this.authService.getPendingTwoFactorChallenge();
    const response = await this.authService.confirmEnrollmentAuthenticator(
      enrollment?.challengeToken ?? '',
      this.twoFactorCode());
    if (response.isSuccess) {
      this.authenticatorSetup.set(undefined);
    }

    await this.afterSignInStep(response);
  }

  async forgetTwoFactorDevices(): Promise<void> {
    const response = await this.authService.forgetTwoFactorDevices(this.passwordReauthentication());
    this.authorization.set(this.authService.getAuthorization());
    this.result.set(JSON.stringify(response, null, 2));
  }

  async listPasskeys(): Promise<void> {
    const response = await this.authService.getPasskeys();
    this.result.set(JSON.stringify(response, null, 2));
  }

  async registerPasskey(): Promise<void> {
    const response = await this.authService.registerPasskey(
      this.translate.instant('project.two-factor-playground-passkey-name'),
      this.passwordReauthentication());
    this.authorization.set(this.authService.getAuthorization());
    this.result.set(JSON.stringify(response, null, 2));
  }

  /** Administration: removes every second factor of another user who lost access to them. */
  async resetUserTwoFactor(): Promise<void> {
    const response = await this.authService.resetUserTwoFactor(this.twoFactorAdministrationUserId().trim());
    this.result.set(JSON.stringify(response, null, 2));
  }

  /** Administration: reminds every user whom the policy requires to enroll. */
  async startTwoFactorEnrollmentReminders(): Promise<void> {
    const response = await this.authService.startTwoFactorEnrollmentReminders(crypto.randomUUID());
    this.result.set(JSON.stringify(response, null, 2));
  }

  /** Administration: ends the sessions of users who must enroll, so they enroll at their next sign-in. */
  async startTwoFactorSessionRevocation(): Promise<void> {
    const response = await this.authService.startTwoFactorSessionRevocation(crypto.randomUUID());
    this.result.set(JSON.stringify(response, null, 2));
  }

  private async afterSignInStep(
    response: { isSuccess: boolean, items: unknown[] },
    authenticated: boolean = response.isSuccess): Promise<void> {
    if (response.isSuccess && authenticated) {
      await this.authService.reloadAuthorizationProfile();
    }

    this.authorization.set(this.authService.getAuthorization());
    this.pendingChallenge.set(this.authService.getPendingTwoFactorChallenge());
    this.result.set(JSON.stringify(response, null, 2));
  }

  async getTwoFactorStatus(): Promise<void> {
    const response = await this.authService.getTwoFactorStatus();
    this.result.set(JSON.stringify(response, null, 2));
  }

  async beginAuthenticatorSetup(): Promise<void> {
    const response = await this.authService.beginAuthenticatorSetup(this.passwordReauthentication());
    this.authenticatorSetup.set(response.data);
    this.result.set(JSON.stringify({
      isSuccess: response.isSuccess,
      sharedKey: response.data?.sharedKey,
      authenticatorUri: response.data?.authenticatorUri,
      items: response.items
    }, null, 2));
  }

  async confirmAuthenticator(): Promise<void> {
    const response = await this.authService.confirmAuthenticator(this.twoFactorCode());
    if (response.isSuccess) {
      this.authenticatorSetup.set(undefined);
    }

    this.authorization.set(this.authService.getAuthorization());
    this.result.set(JSON.stringify(response, null, 2));
  }

  async regenerateRecoveryCodes(): Promise<void> {
    const response = await this.authService.regenerateRecoveryCodes(this.passwordReauthentication());
    this.result.set(JSON.stringify(response, null, 2));
  }

  async disableTwoFactor(): Promise<void> {
    const response = await this.authService.disableTwoFactor(this.passwordReauthentication());
    this.authorization.set(this.authService.getAuthorization());
    this.result.set(JSON.stringify(response, null, 2));
  }

  private passwordReauthentication(): NhTwoFactorReauthentication {
    return new NhTwoFactorReauthentication({ password: this.password() });
  }

  selectDemoAccount(account: AuthorizationDemoAccount): void {
    this.username.set(account.email);
    this.password.set('Sample123!');
    this.result.set(
      `${this.translate.instant(account.labelKey)}: ` +
      this.translate.instant(account.expectedAccessKey)
    );
  }

  probeApplicationView(): Promise<void> {
    return this.runAuthorizationProbe(
      'application/view',
      this.authorizationApi.getApplicationView()
    );
  }

  probeApplicationManage(): Promise<void> {
    return this.runAuthorizationProbe(
      'application/manage',
      this.authorizationApi.getApplicationManage()
    );
  }

  probeDivisionView(): Promise<void> {
    return this.runAuthorizationProbe(
      'division/view',
      this.authorizationApi.getDivisionView()
    );
  }

  probeProject(projectId: string): Promise<void> {
    return this.runAuthorizationProbe(
      `projects/${projectId}/confidential`,
      this.authorizationApi.getProjectConfidential(projectId)
    );
  }

  probeAuthenticationOverrides(): Promise<void> {
    return this.runAuthorizationProbe(
      'overrides/runtime-claims',
      this.authorizationApi.getRuntimeClaims()
    );
  }

  private async runAuthorizationProbe(
    endpoint: string,
    request: Observable<unknown>
  ): Promise<void> {
    try {
      const response = await firstValueFrom(request);
      this.result.set(JSON.stringify({ endpoint, status: 200, response }, null, 2));
    } catch (error) {
      const httpError = error as HttpErrorResponse;
      this.result.set(JSON.stringify({
        endpoint,
        status: httpError.status,
        message: httpError.message
      }, null, 2));
    }
  }

  updateUsername(event: Event): void { this.username.set((event.target as HTMLInputElement).value); }
  updatePassword(event: Event): void { this.password.set((event.target as HTMLInputElement).value); }
  updateImpersonateId(event: Event): void { this.impersonateUserId.set((event.target as HTMLInputElement).value); }
  updateTwoFactorCode(event: Event): void { this.twoFactorCode.set((event.target as HTMLInputElement).value); }
  updateTwoFactorMethod(event: Event): void { this.twoFactorMethod.set((event.target as HTMLSelectElement).value); }
  updateRememberDevice(event: Event): void { this.rememberDevice.set((event.target as HTMLInputElement).checked); }
  updateTwoFactorAdministrationUserId(event: Event): void { this.twoFactorAdministrationUserId.set((event.target as HTMLInputElement).value); }
}
