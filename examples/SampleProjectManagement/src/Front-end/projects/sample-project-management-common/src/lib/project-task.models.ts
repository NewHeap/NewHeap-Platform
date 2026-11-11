/** A task of a project, as `GET project-tasks/{id}` returns it. */
export interface ProjectTaskViewModel {
  id: string;
  projectId: string;
  title: string;
  isCompleted: boolean;
  completedAt?: string | null;
  completedByUserId?: string | null;
  creationDateTime: string;
  lastModifiedDateTime: string;
}

/** The fields of a task that `POST project-tasks` and `PUT project-tasks/{id}` take. */
export interface ProjectTaskMutateModel {
  projectId: string;
  title: string;
  isCompleted: boolean;
}
