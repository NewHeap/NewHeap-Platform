import { Injectable, OnDestroy, inject } from '@angular/core';
import { Router } from '@angular/router';
import { NhCommonModuleConfig } from '@newheap/platform-common';
import { Subscription } from 'rxjs';
import { SampleAuthService } from './sample-auth.service';

@Injectable({ providedIn: 'root' })
export class SampleAuthSessionService implements OnDestroy {
  private readonly authService = inject(SampleAuthService);
  private readonly router = inject(Router);
  private readonly moduleConfig = inject(NhCommonModuleConfig);
  private subscription?: Subscription;
  private authenticatedSessionSeen = false;
  private redirecting = false;

  start(): void {
    if (this.subscription) {
      return;
    }

    this.authenticatedSessionSeen = this.authService.isAuthenticated();
    this.subscription = this.authService.sessionExpirationInformationChanged
      .subscribe(information => {
        if (information.isAuthenticated) {
          this.authenticatedSessionSeen = true;
          return;
        }

        if (!this.authenticatedSessionSeen || this.redirecting) {
          return;
        }

        this.authenticatedSessionSeen = false;
        const currentUrl = this.router.url;
        const loginPath = this.moduleConfig.authentication.loginPath || '/auth/login';
        const validTo = this.authService.getAuthorization()?.validTo;
        const sessionExpired = !!validTo && Date.parse(validTo) <= Date.now();

        // BaseNhAuthService only clears a currently valid authorization. The
        // sample override also removes an authorization that just expired.
        this.authService.clearAuthorization();

        if (currentUrl.split('?')[0] === loginPath) {
          return;
        }

        this.redirecting = true;
        void this.router.navigate([loginPath], {
          queryParams: {
            ...(sessionExpired ? { reason: 'session-expired' } : {}),
            returnUrl: this.isLocalReturnUrl(currentUrl) ? currentUrl : '/'
          }
        }).finally(() => this.redirecting = false);
      });
  }

  ngOnDestroy(): void {
    this.subscription?.unsubscribe();
  }

  private isLocalReturnUrl(url: string): boolean {
    return url.startsWith('/') && !url.startsWith('//');
  }
}
