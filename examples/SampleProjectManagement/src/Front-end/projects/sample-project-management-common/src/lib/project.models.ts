import { CollectionHttpRequestOptions } from '@newheap/platform-common';

export enum ProjectStatus {
  Draft = 'Draft',
  Active = 'Active',
  OnHold = 'OnHold',
  Completed = 'Completed',
  Archived = 'Archived'
}

export interface ProjectViewModel {
  id: string;
  divisionId: string;
  ownerUserId?: string | null;
  key: string;
  name: string;
  description?: string;
  status: ProjectStatus;
  deadline?: string | null;
  creationDateTime: string;
  lastModifiedDateTime: string;
}

export interface ProjectMutateModel {
  divisionId: string;
  ownerUserId?: string | null;
  key: string;
  name: string;
  description?: string;
  status: ProjectStatus;
  deadline?: string | null;
}

export class ProjectCollectionRequestOptions extends CollectionHttpRequestOptions {
  divisionId?: string;
  statuses: ProjectStatus[] = [];

  constructor(init?: Partial<ProjectCollectionRequestOptions>) {
    super(init);
    Object.assign(this, init);
  }
}

export interface ProjectBulkStatusMutateModel {
  ids: string[];
  status: ProjectStatus;
  continueOnError: boolean;
}

export interface ProjectBulkStatusResultViewModel {
  requestedCount: number;
  results: ProjectBulkStatusItemResultViewModel[];
  succeededCount: number;
  failedCount: number;
  failedIds: string[];
}

export interface ProjectBulkStatusItemResultViewModel {
  id: string;
  success: boolean;
  errorMessages: string[];
}

export interface ProjectRollbackSampleViewModel {
  projectId: string;
  eventId: string;
  verification: string;
}

export interface ProjectCreatedEventViewModel {
  eventId: string;
  projectId: string;
  projectKey: string;
  occurredAt: string;
}

export interface CollectionExpressionSampleViewModel {
  inputKey: string;
  resolvedPath: string;
  generatedExpression: string;
  matchCount: number;
  isSupported: boolean;
  limitation?: string;
}
