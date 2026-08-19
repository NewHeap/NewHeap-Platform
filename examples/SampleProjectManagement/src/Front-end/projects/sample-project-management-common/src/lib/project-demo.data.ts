import { ProjectStatus, ProjectViewModel } from './project.models';

export const PROJECT_DEMO_DATA: ProjectViewModel[] = [
  {
    id: 'e63da790-7b5c-4f88-a65a-981865ee94b4',
    divisionId: 'b14a1178-8bd7-4e87-845f-e0d89b63f099',
    key: 'NHP',
    name: 'NewHeap Platform examples',
    description: 'Executable examples for the shared platform libraries.',
    status: ProjectStatus.Active,
    deadline: '2026-09-30T16:00:00Z',
    creationDateTime: '2026-07-03T08:00:00Z',
    lastModifiedDateTime: '2026-07-23T13:20:00Z'
  },
  {
    id: '94533222-4459-451a-a84a-2cc04a01cd2d',
    divisionId: 'b14a1178-8bd7-4e87-845f-e0d89b63f099',
    key: 'PORTAL',
    name: 'Customer portal',
    description: 'A new project workspace for customers and internal teams.',
    status: ProjectStatus.OnHold,
    deadline: '2026-11-14T16:00:00Z',
    creationDateTime: '2026-06-18T09:30:00Z',
    lastModifiedDateTime: '2026-07-19T15:45:00Z'
  },
  {
    id: '61e0fd68-0f1f-498c-846e-34eb3a60e62f',
    divisionId: 'fc426589-4533-4b76-bc19-612fb8205648',
    key: 'OPS',
    name: 'Operations dashboard',
    description: 'Bring lead times and operational signals together.',
    status: ProjectStatus.Draft,
    deadline: null,
    creationDateTime: '2026-07-21T10:15:00Z',
    lastModifiedDateTime: '2026-07-22T16:10:00Z'
  },
  {
    id: 'aeb797d4-6abf-43b0-a152-d2bd822496f2',
    divisionId: 'fc426589-4533-4b76-bc19-612fb8205648',
    key: 'ARCH',
    name: 'Architecture inventory',
    description: 'Document conventions and reference implementations.',
    status: ProjectStatus.Completed,
    deadline: '2026-07-10T16:00:00Z',
    creationDateTime: '2026-05-04T07:45:00Z',
    lastModifiedDateTime: '2026-07-14T12:00:00Z'
  }
];
