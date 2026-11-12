import { Injectable } from '@angular/core';
import { NhBaseApiService } from '@newheap/platform-common';
import { Observable } from 'rxjs';
import { ProjectTaskMutateModel, ProjectTaskViewModel } from './project-task.models';

/** The tasks of projects (`project-tasks`); creating one needs the `app.project.manage` permission. */
@Injectable({ providedIn: 'root' })
export class ProjectTaskApiService extends NhBaseApiService {
  constructor() {
    super('project-tasks');
  }

  createTask(model: ProjectTaskMutateModel): Observable<ProjectTaskViewModel> {
    return this.create<ProjectTaskViewModel>(model);
  }
}
