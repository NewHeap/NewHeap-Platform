import { CommonModule } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { AuthenticateModel } from '@newheap/platform-common';
import { TranslateModule, TranslateService } from '@ngx-translate/core';
import {
  AUTHORIZATION_DEMO_ACCOUNTS,
  AuthorizationDemoAccount
} from './authorization-sample.models';
import { SampleAuthService } from './sample-auth.service';

@Component({
  selector: 'app-sample-login',
  standalone: true,
  imports: [CommonModule, FormsModule, TranslateModule],
  templateUrl: './sample-login.component.html',
  styleUrl: './sample-login.component.scss'
})
export class SampleLoginComponent {
  private readonly authService = inject(SampleAuthService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly translate = inject(TranslateService);

  username = 'sample@example.test';
  password = 'Sample123!';
  readonly demoAccounts = AUTHORIZATION_DEMO_ACCOUNTS;
  readonly errorMessage = signal('');
  readonly isSubmitting = signal(false);
  readonly sessionExpired = this.route.snapshot.queryParamMap.get('reason') === 'session-expired';

  selectAccount(account: AuthorizationDemoAccount): void {
    this.username = account.email;
    this.password = 'Sample123!';
  }

  async login(): Promise<void> {
    if (this.isSubmitting()) {
      return;
    }

    this.isSubmitting.set(true);
    this.errorMessage.set('');

    try {
      const loginResult = await this.authService.authenticate(new AuthenticateModel({
        username: this.username,
        password: this.password
      }));

      if (!loginResult.isSuccess) {
        this.errorMessage.set(this.getErrorMessage(loginResult));
        return;
      }

      const profileResult = await this.authService.reloadAuthorizationProfile();
      if (!profileResult.isSuccess) {
        this.errorMessage.set(this.getErrorMessage(profileResult));
        return;
      }

      const returnUrl = this.route.snapshot.queryParamMap.get('returnUrl');
      await this.router.navigateByUrl(this.isLocalReturnUrl(returnUrl) ? returnUrl : '/');
    } catch {
      this.errorMessage.set(this.loginFailedMessage());
    } finally {
      this.isSubmitting.set(false);
    }
  }

  private getErrorMessage(result: { items?: Array<{ errorMessages?: string[] }> }): string {
    const message = result.items
      ?.flatMap(item => item.errorMessages ?? [])
      .map(item => item.trim())
      .find(item => item.length > 0 && item.length <= 240 && !/[<>]/.test(item));

    return message ?? this.loginFailedMessage();
  }

  private loginFailedMessage(): string {
    return this.translate.instant('project.login-failed');
  }

  private isLocalReturnUrl(url: string | null): url is string {
    return url?.startsWith('/') === true && !url.startsWith('//');
  }
}
