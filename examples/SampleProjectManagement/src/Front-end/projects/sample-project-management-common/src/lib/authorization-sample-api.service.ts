import { Injectable } from '@angular/core';
import { NhBaseApiService } from '@newheap/platform-common';
import { Observable } from 'rxjs';
import {
  AuthenticationOverrideProbeSample,
  AuthorizationProbeSample
} from './authorization-sample.models';

@Injectable({ providedIn: 'root' })
export class AuthorizationSampleApiService extends NhBaseApiService {
  constructor() {
    super('authorization-samples');
  }

  getApplicationView(): Observable<AuthorizationProbeSample> {
    return this.apiService.get<AuthorizationProbeSample>(
      `${this.baseUrl}/application/view`
    );
  }

  getApplicationManage(): Observable<AuthorizationProbeSample> {
    return this.apiService.get<AuthorizationProbeSample>(
      `${this.baseUrl}/application/manage`
    );
  }

  getDivisionView(): Observable<AuthorizationProbeSample> {
    return this.apiService.get<AuthorizationProbeSample>(
      `${this.baseUrl}/division/view`
    );
  }

  getProjectConfidential(projectId: string): Observable<AuthorizationProbeSample> {
    return this.apiService.get<AuthorizationProbeSample>(
      `${this.baseUrl}/projects/${projectId}/confidential`
    );
  }

  getRuntimeClaims(): Observable<AuthenticationOverrideProbeSample> {
    return this.apiService.get<AuthenticationOverrideProbeSample>(
      `${this.baseUrl}/overrides/runtime-claims`
    );
  }
}
