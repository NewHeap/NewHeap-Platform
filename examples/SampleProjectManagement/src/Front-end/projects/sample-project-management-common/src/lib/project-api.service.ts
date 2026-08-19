import { Injectable } from '@angular/core';
import {
  CollectionHttpResponse,
  NhBaseApiService
} from '@newheap/platform-common';
import { Observable } from 'rxjs';
import {
  CollectionExpressionSampleViewModel,
  ProjectBulkStatusMutateModel,
  ProjectBulkStatusResultViewModel,
  ProjectCollectionRequestOptions,
  ProjectCreatedEventViewModel,
  ProjectMutateModel,
  ProjectRollbackSampleViewModel,
  ProjectStatus,
  ProjectViewModel
} from './project.models';

@Injectable({ providedIn: 'root' })
export class ProjectApiService extends NhBaseApiService {
  constructor() {
    super('projects');
  }

  list(
    options = new ProjectCollectionRequestOptions({ itemsPerPage: 50 })
  ): Observable<CollectionHttpResponse<ProjectViewModel>> {
    return this.getCollection<ProjectViewModel>(options);
  }

  getById(id: string): Observable<ProjectViewModel> {
    return this.get<ProjectViewModel>(id);
  }

  resolveCollectionExpression(
    taskTitle: string
  ): Observable<CollectionExpressionSampleViewModel> {
    return this.apiService.get<CollectionExpressionSampleViewModel>(
      this.baseUrl + '/expression-resolver?taskTitle=' + encodeURIComponent(taskTitle)
    );
  }

  getNestedValidationSample(): Observable<never> {
    return this.apiService.get<never>(
      this.moduleConfig.apiBaseUrl + '/library-samples/validation/model-state'
    );
  }

  createProject(model: ProjectMutateModel): Observable<ProjectViewModel> {
    return this.create<ProjectViewModel>(model);
  }

  createRolledBackSample(
    model: ProjectMutateModel
  ): Observable<ProjectRollbackSampleViewModel> {
    return this.apiService.post<ProjectRollbackSampleViewModel>(
      `${this.baseUrl}/transaction-rollback-sample`,
      model
    );
  }

  getConsumedEvents(): Observable<ProjectCreatedEventViewModel[]> {
    return this.apiService.get<ProjectCreatedEventViewModel[]>(
      this.moduleConfig.apiBaseUrl + '/library-samples/events'
    );
  }

  updateProject(
    id: string,
    model: ProjectMutateModel
  ): Observable<ProjectViewModel> {
    return this.update<ProjectViewModel>(id, model);
  }

  deleteProject(id: string): Observable<void> {
    return this.delete(id);
  }

  updateStatus(id: string, status: ProjectStatus): Observable<void> {
    return this.updatePartial<void>(id, { status });
  }

  bulkUpdateStatus(
    model: ProjectBulkStatusMutateModel
  ): Observable<ProjectBulkStatusResultViewModel> {
    return this.apiService.put<ProjectBulkStatusResultViewModel>(
      `${this.baseUrl}/bulk/status`,
      model
    );
  }
}
