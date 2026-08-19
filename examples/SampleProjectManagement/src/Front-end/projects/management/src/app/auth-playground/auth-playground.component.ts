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
  NhDivision,
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
  SampleClaimTypes
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
}
