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

/**
 * The sample's authorization service. It keeps the NewHeap `logout()` and `clearAuthorization()`:
 * logout ends the browser session also when the server logout fails, and returns that failure so
 * the caller can report it, and clearing also removes an authorization that just expired.
 */
@Injectable({ providedIn: 'root' })
export class SampleAuthService extends BaseNhAuthService<NhAuthorization> {
  protected initEmptyAuthorization(): NhAuthorization {
    return new NhAuthorization();
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
