import { CollectionHttpRequestOptions } from '@newheap/platform-common';

/**
 * Project roles, ordered so that each role includes the ones below it. The role also decides
 * what a person may do with the project's agent threads.
 */
export enum ProjectMemberRole {
  Reader = 'Reader',
  Participant = 'Participant',
  Approver = 'Approver'
}

export interface ProjectMemberViewModel {
  id: string;
  projectId: string;
  userId: string;
  userName: string;
  role: ProjectMemberRole;
  creationDateTime: string;
  lastModifiedDateTime: string;
}

export interface ProjectMemberMutateModel {
  projectId: string;
  userId: string;
  role: ProjectMemberRole;
}

export class ProjectMemberCollectionRequestOptions extends CollectionHttpRequestOptions {
  projectId?: string;

  constructor(init?: Partial<ProjectMemberCollectionRequestOptions>) {
    super(init);
    Object.assign(this, init);
  }
}

export const PROJECT_MEMBER_ROLES: readonly ProjectMemberRole[] = [
  ProjectMemberRole.Reader,
  ProjectMemberRole.Participant,
  ProjectMemberRole.Approver
];
