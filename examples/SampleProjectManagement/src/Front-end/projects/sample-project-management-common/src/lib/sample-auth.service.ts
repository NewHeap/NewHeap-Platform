import { Injectable } from '@angular/core';
import {
  BaseNhAuthService,
  Claim,
  NhAuthorization
} from '@newheap/platform-common';

export enum SampleClaimTypes {
  ProjectRole = 'sample.project.role',
  ProjectPermission = 'sample.project.permission'
}

@Injectable({ providedIn: 'root' })
export class SampleAuthService extends BaseNhAuthService<NhAuthorization> {
  protected initEmptyAuthorization(): NhAuthorization {
    return new NhAuthorization();
  }

  override clearAuthorization(doDispatchEvent: boolean = true): void {
    try {
      localStorage.removeItem('at');
    } finally {
      this.authorization = undefined;
      if (doDispatchEvent) {
        this.authSubject.next(undefined);
      }
      this.dispatchSessionExpirationInformationChanged();
    }
  }

  override async logout() {
    const result = await super.logout();

    // A server-side logout failure must not leave the browser session active.
    // The API result is still returned so callers can report that failure.
    this.clearAuthorization();
    return result;
  }

  isOneProjectPermissionGranted(
    projectId: string | undefined,
    permissions: readonly string[]
  ): boolean {
    if (permissions.length === 0) {
      return true;
    }
    if (!projectId) {
      return false;
    }

    return permissions.some(permission => this.isClaimGranted(new Claim({
      type: SampleClaimTypes.ProjectPermission,
      value: `${projectId}_${permission}`
    })));
  }

  isOneProjectRoleGranted(
    projectId: string | undefined,
    roles: readonly string[]
  ): boolean {
    if (roles.length === 0) {
      return true;
    }
    if (!projectId) {
      return false;
    }

    return roles.some(role => this.isClaimGranted(new Claim({
      type: SampleClaimTypes.ProjectRole,
      value: `${projectId}_${role}`
    })));
  }

  hasProjectDivisionOrApplicationPermission(
    projectId: string | undefined,
    permission: string
  ): boolean {
    return this.isOnePermissionGranted([`app.project.${permission}`]) ||
      this.isOneActiveDivisionPermissionGranted([`project.${permission}`]) ||
      this.isOneProjectPermissionGranted(projectId, [permission]);
  }
}
