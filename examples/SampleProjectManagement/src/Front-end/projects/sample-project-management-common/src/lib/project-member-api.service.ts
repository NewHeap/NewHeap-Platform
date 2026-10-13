import { Injectable } from '@angular/core';
import { CollectionHttpResponse, NhBaseApiService } from '@newheap/platform-common';
import { Observable } from 'rxjs';
import {
  ProjectMemberCollectionRequestOptions,
  ProjectMemberMutateModel,
  ProjectMemberViewModel
} from './project-member.models';

/**
 * The people on a project. Every change also refreshes the project's agent threads on the server,
 * so a removed member loses access to them at once.
 */
@Injectable({ providedIn: 'root' })
export class ProjectMemberApiService extends NhBaseApiService {
  constructor() {
    super('project-members');
  }

  listForProject(projectId: string): Observable<CollectionHttpResponse<ProjectMemberViewModel>> {
    return this.getCollection<ProjectMemberViewModel>(
      new ProjectMemberCollectionRequestOptions({ projectId, itemsPerPage: 100 })
    );
  }

  addMember(model: ProjectMemberMutateModel): Observable<ProjectMemberViewModel> {
    return this.create<ProjectMemberViewModel>(model);
  }

  changeMember(id: string, model: ProjectMemberMutateModel): Observable<void> {
    return this.update<void>(id, model);
  }

  removeMember(id: string): Observable<void> {
    return this.delete(id);
  }
}
