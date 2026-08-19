import {inject, PLATFORM_ID} from '@angular/core';
import {isPlatformServer} from '@angular/common';
import {ActivatedRoute, ActivatedRouteSnapshot, Router, RouterStateSnapshot} from '@angular/router';
import {NhAuthService} from "../services/nh-auth.service";
import {Claim} from "../models/auth.models";
import {NhCommonModuleConfig} from "../models/config.models";


export const NhIsAuthenticatedGuard = (route: ActivatedRouteSnapshot, state: RouterStateSnapshot) => {
  const authService = inject(NhAuthService);
  const router = inject(Router);
  const platformId = inject(PLATFORM_ID);
  const platformConfig = inject(NhCommonModuleConfig);

  const canActivate = authService.isAuthenticated();

  if (!canActivate && !isPlatformServer(platformId)) {
    const redirectUrl = route.data['isAuthenticatedGuardRedirectPath'] as string|undefined|null;

    let url = '/';
    if(platformConfig?.authentication?.loginPath && platformConfig.authentication.loginPath.length > 0) {
      url = platformConfig.authentication.loginPath;
    }

    if (redirectUrl) {
      router.navigate([redirectUrl]);
    } else {
      router.navigate([url]);
    }
  }

  return canActivate;
}

export const NhIsOneClaimGrantedGuard = (route: ActivatedRouteSnapshot, state: RouterStateSnapshot) => {
  const authService = inject(NhAuthService);
  const router = inject(Router);
  const platformId = inject(PLATFORM_ID);

  const claims = route.data['oneMatchClaims'] as Array<Claim>;
  if (!claims || claims.length < 1) {
    return true;
  }

  const canActivate = authService.isAuthenticated() && authService.isOneClaimGranted(claims);

  if (!canActivate && !isPlatformServer(platformId)) {
    const redirectUrl = route.data['isAuthenticatedGuardRedirectPath'] as string|undefined|null ?? '/';
    router.navigate([redirectUrl]);
  }

  return canActivate;
}


export const NhIsOnePermissionGrantedGuard = (route: ActivatedRouteSnapshot, state: RouterStateSnapshot) => {
  const authService = inject(NhAuthService);
  const router = inject(Router);
  const platformId = inject(PLATFORM_ID);

  const permissions = route.data['oneMatchPermissions'] as Array<string>;

  if (!permissions || permissions.length < 1) {
    return true;
  }

  const canActivate = authService.isAuthenticated() && authService.isOnePermissionGranted(permissions);

  if (!canActivate && !isPlatformServer(platformId)) {
    const redirectUrl = route.data['isAuthenticatedGuardRedirectPath'] as string|undefined|null ?? '/';
    router.navigate([redirectUrl]);
  }

  return canActivate;
}

export const NhIsAllPermissionsGrantedGuard = (route: ActivatedRouteSnapshot, state: RouterStateSnapshot) => {
  const authService = inject(NhAuthService);
  const router = inject(Router);
  const platformId = inject(PLATFORM_ID);

  const permissions = route.data["allMatchPermissions"] as Array<string>;
  if (!permissions || permissions.length < 1) {
    return true;
  }

  const canActivate = authService.isAuthenticated() && authService.isAllPermissionsGranted(permissions);

  if (!canActivate && !isPlatformServer(platformId)) {
    const redirectUrl = route.data["isAuthenticatedGuardRedirectPath"] as string|undefined|null ?? "/";
    router.navigate([redirectUrl]);
  }

  return canActivate;
}


export const NhIsOneRoleGrantedGuard = (route: ActivatedRouteSnapshot, state: RouterStateSnapshot) => {
  const authService = inject(NhAuthService);
  const router = inject(Router);
  const platformId = inject(PLATFORM_ID);

  const roles = route.data['oneMatchRoles'] as Array<string>;

  if (!roles || roles.length < 1) {
    return true;
  }

  const canActivate = authService.isAuthenticated() && authService.isOneRoleGranted(roles);

  if (!canActivate && !isPlatformServer(platformId)) {
    const redirectUrl = route.data['isAuthenticatedGuardRedirectPath'] as string|undefined|null ?? '/';
    router.navigate([redirectUrl]);
  }

  return canActivate;
}

export const IsOneActiveDivisionPermissionGrantedGuard = () => {
  const route = inject(ActivatedRoute);
  const authService = inject(NhAuthService);
  const router = inject(Router);

  const permissions = (<any>route)?.data['activeDivisionOneMatchPermissions'] as Array<string>;

  if (!permissions || permissions.length < 1) {
    return true;
  }

  const canActivate = authService.isAuthenticated() && authService.isOneActiveDivisionPermissionGranted(permissions);

  if (!canActivate) {
    const redirectUrl = (<any>route)?.data['isAuthenticatedGuardRedirectPath'] as string|undefined|null ?? '/';
    router.navigate([redirectUrl]);
  }

  return canActivate;
}

export const IsOneActiveDivisionRoleGrantedGuard = () => {
  const route = inject(ActivatedRoute);
  const authService = inject(NhAuthService);
  const router = inject(Router);

  const roles = (<any>route)?.data['activeDivisionOneMatchRoles'] as Array<string>;

  if (!roles || roles.length < 1) {
    return true;
  }

  const canActivate = authService.isAuthenticated() && authService.isOneActiveDivisionRoleGranted(roles);

  if (!canActivate) {
    const redirectUrl = (<any>route)?.data['isAuthenticatedGuardRedirectPath'] as string|undefined|null ?? '/';
    router.navigate([redirectUrl]);
  }

  return canActivate;
}

