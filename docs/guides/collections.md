# Filter and page an Angular collection

Use `NhBaseApiService` to call a NewHeap collection endpoint, and fluent request
options to describe filters, sorting and paging.

## Configure the application once

Install `@newheap/platform-common` in an Angular 20 application. Register
`NhCommonModule.forRoot(config, AuthService)` once at the application root,
using your service derived from `BaseNhAuthService`. Feature components import
plain `NhCommonModule`.

In the root `NhCommonModuleConfig`, set the API URL and enable the two opt-ins
used by the sample:

```ts
const config = new NhCommonModuleConfig({
  apiBaseUrl: '/api',
  authApiBaseUrl: '/api',
  http: new NhHttpNhCommonModuleConfig({
    deduplicateGetRequests: true
  }),
  formDropdown: new NhFormDropDownNhCommonModuleConfig({
    deferLazyLoadUntilOpened: true
  })
});
```

All three configuration classes are exported by `@newheap/platform-common`.
Use the [complete root providers](../../examples/SampleProjectManagement/src/Front-end/projects/sample-project-management-common/src/lib/sample-project-management.providers.ts)
for an application with authentication, translations and notifications. In this
example, `/api` must route to the backend; the sample supplies that proxy setup.

## Add an API service

This small service calls `/api/projects`. `ProjectViewModel` is your frontend
interface matching the API response:

```ts
import { Injectable } from '@angular/core';
import { CollectionHttpRequestOptions, NhBaseApiService } from '@newheap/platform-common';
import { ProjectViewModel } from './project.models';

@Injectable({ providedIn: 'root' })
export class ProjectApiService extends NhBaseApiService {
  constructor() {
    super('projects');
  }

  list(options: CollectionHttpRequestOptions) {
    return this.getCollection<ProjectViewModel>(options);
  }
}
```

The [sample API service](../../examples/SampleProjectManagement/src/Front-end/projects/sample-project-management-common/src/lib/project-api.service.ts)
adds create, update, delete and project-specific request options.

## Request active projects

In a component with `projectApi` injected, construct a fresh request for each
filter change:

```ts
const options = new CollectionHttpRequestOptions({ page: 1, itemsPerPage: 20 });
options.equals('status', ProjectStatus.Active).orderAsc('name');
const projects$ = this.projectApi.list(options);
```

Import `ProjectStatus` from your model file. Subscribe through your component's
normal loading/error flow or an `async` pipe. The API filters active projects,
orders them by name and returns the first page; the browser does not fetch the
entire collection. The backend must mark these fields `[Filterable]` and
`[Orderable]`, as shown in the [project view model](../../examples/SampleProjectManagement/src/Back-end/Libraries/SampleProjectManagement.Core/Models/View/ProjectViewModels.cs).

## Combine conditions

Import `FilterRequestOptions` from `@newheap/platform-common` to group alternatives:

```ts
options.and(
  FilterRequestOptions.equals('status', ProjectStatus.Active)
    .or(FilterRequestOptions.equals('status', ProjectStatus.Completed))
);
```

Use this instead of the single-status filter above to include both statuses.
The [collection playground](../../examples/SampleProjectManagement/src/Front-end/projects/management/src/app/collection-playground/collection-playground.component.ts)
demonstrates the complete request, page controls and response handling.
See the [query reference](../consumer-guide/frontend-collection-query.md) for the
remaining operators and [root configuration reference](../consumer-guide/frontend-root-configuration.md)
for interceptor behavior.
