import { Pipe, PipeTransform } from '@angular/core';
import { SampleAuthService } from './sample-auth.service';

@Pipe({
  name: 'isOneProjectPermissionGranted',
  standalone: true
})
export class IsOneProjectPermissionGrantedPipe implements PipeTransform {
  constructor(private readonly authService: SampleAuthService) {}

  transform(projectId: string | undefined, permissions: readonly string[]): boolean {
    return this.authService.isOneProjectPermissionGranted(projectId, permissions);
  }
}

@Pipe({
  name: 'isOneProjectRoleGranted',
  standalone: true
})
export class IsOneProjectRoleGrantedPipe implements PipeTransform {
  constructor(private readonly authService: SampleAuthService) {}

  transform(projectId: string | undefined, roles: readonly string[]): boolean {
    return this.authService.isOneProjectRoleGranted(projectId, roles);
  }
}

@Pipe({
  name: 'hasProjectDivisionOrApplicationPermission',
  standalone: true
})
export class HasProjectDivisionOrApplicationPermissionPipe implements PipeTransform {
  constructor(private readonly authService: SampleAuthService) {}

  transform(projectId: string | undefined, permission: string): boolean {
    return this.authService.hasProjectDivisionOrApplicationPermission(
      projectId,
      permission
    );
  }
}
