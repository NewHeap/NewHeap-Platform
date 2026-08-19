import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { SampleAuthService } from './sample-auth.service';

export const sampleIsAuthenticatedGuard: CanActivateFn = (_route, state) => {
  const authService = inject(SampleAuthService);
  const router = inject(Router);

  if (authService.isAuthenticated()) {
    return true;
  }

  return router.createUrlTree(['/auth/login'], {
    queryParams: { returnUrl: state.url }
  });
};
