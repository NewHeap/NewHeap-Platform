import {Pipe, PipeTransform} from '@angular/core';
import {Claim} from '../models/auth.models';
import {NhAuthService} from '../services/nh-auth.service';

@Pipe({
    name: 'isOnePermissionGranted',
    standalone: false
})
export class IsOnePermissionGrantedPipe implements PipeTransform {

  constructor(private authService: NhAuthService) {
  }

  transform(permissions: Array<string>): boolean {
    return this.authService.isOnePermissionGranted(permissions);
  }
}

@Pipe({
    name: 'isGrantedRole',
    standalone: false
})
export class IsGrantedRolePipe implements PipeTransform {

  constructor(private authService: NhAuthService) {
  }

  transform(roles: string | string[]): boolean {
    return this.authService.isGrantedRole(roles);
  }
}

@Pipe({
    name: 'isAuthenticated',
    standalone: false
})
export class IsAuthenticatedPipe implements PipeTransform {

  constructor(private authService: NhAuthService) {
  }

  transform(): boolean {
    return this.authService.isAuthenticated();
  }
}

@Pipe({
    name: 'isClaimGranted',
    standalone: false
})
export class IsClaimGrantedPipe implements PipeTransform {

  constructor(private authService: NhAuthService) {
  }

  transform(claim: Claim): boolean {
    return this.authService.isClaimGranted(claim);
  }
}

@Pipe({
    name: 'isOneClaimGranted',
    standalone: false
})
export class IsOneClaimGrantedPipe implements PipeTransform {

  constructor(private authService: NhAuthService) {
  }

  transform(claims: Array<Claim>): boolean {
    return this.authService.isOneClaimGranted(claims);
  }
}

@Pipe({
    name: 'isOneRoleGranted',
    standalone: false
})
export class IsOneRoleGrantedPipe implements PipeTransform {

  constructor(private authService: NhAuthService) {
  }

  transform(roles: Array<string>): boolean {
    return this.authService.isOneRoleGranted(roles);
  }
}
