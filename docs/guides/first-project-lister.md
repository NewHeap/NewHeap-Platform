# Step 1: get an Angular lister working with the API

Build a project list with search, sorting and paging performed by the API.
Complete [Step 0](platform-setup.md) first. This walkthrough adds a lister to
SampleProjectManagement and links to the backend files needed in your application.

## 1. Connect the entity to the endpoint

The complete feature uses these files:

| File | What to implement in your application |
| --- | --- |
| [Project entity](../../examples/SampleProjectManagement/src/Back-end/Libraries/SampleProjectManagement.DAL/Entities/Project.cs) | Persisted fields and relationships |
| [DbContext](../../examples/SampleProjectManagement/src/Back-end/Libraries/SampleProjectManagement.DAL/SampleProjectManagementDbContext.cs) | `DbSet<Project>` and relationship configuration |
| [View and request models](../../examples/SampleProjectManagement/src/Back-end/Libraries/SampleProjectManagement.Core/Models/View/ProjectViewModels.cs) | Returned fields and permitted collection operations |
| [Mapping profile](../../examples/SampleProjectManagement/src/Back-end/Libraries/SampleProjectManagement.Core/Utilities/AutomapperProfileConfiguration.cs) | Entity-to-view-model mapping |
| [Service registration](../../examples/SampleProjectManagement/src/Back-end/Libraries/SampleProjectManagement.Core/ServiceCollectionExtensions.cs) | Repository and concrete service registration |
| [Project service](../../examples/SampleProjectManagement/src/Back-end/Libraries/SampleProjectManagement.Core/Services/ProjectService.cs) | The database query supplied to collection processing |
| [Project controller](../../examples/SampleProjectManagement/src/Back-end/Applications/SampleProjectManagement.Api/Controllers/ProjectController.cs) | The collection route, access policy and response contract |

Start with the fields the lister needs. Attributes on the view model allow the
API to accept the corresponding query operations:

```csharp
[Filterable]
public Guid Id { get; set; }

[Searchable, Orderable, Filterable]
public string Name { get; set; } = "";
```

The profile includes `CreateMap<Project, ProjectViewModel>()`. Service
registration includes `AddScopedNhDbRepository<Project>()` and
`AddScoped<ProjectService>()`. The service returns an `IQueryable<Project>` so
NewHeap can apply filtering, ordering and paging in the database.

Inside `ProjectController`, the GET action passes the request and query to the
base controller:

```csharp
return DoGet(
    requestModel,
    _projectService.GetCollectionQuery(requestModel),
    cancellationToken);
```

Keep the sample action's `[HttpGet]`, `app.project.view` authorization policy,
parameter binding and OpenAPI response metadata. The result is the collection
contract consumed by Angular.

## 2. Connect Angular to the endpoint

With the proxy configured in Step 0, Angular `/api/projects` reaches backend
`/projects`.

The [ProjectApiService](../../examples/SampleProjectManagement/src/Front-end/projects/sample-project-management-common/src/lib/project-api.service.ts)
extends `NhBaseApiService`. Its constructor supplies `projects`, and its list
method calls `getCollection`:

```ts
constructor() {
  super('projects');
}

list(options = new ProjectCollectionRequestOptions({ itemsPerPage: 20 })) {
  return this.getCollection<ProjectViewModel>(options);
}
```

The frontend [models](../../examples/SampleProjectManagement/src/Front-end/projects/sample-project-management-common/src/lib/project.models.ts)
match the API's JSON. Keep the property names and enum values aligned.

## 3. Add the lister component

The [collection lifecycle component](../../examples/SampleProjectManagement/src/Front-end/projects/management/src/app/interaction-playground/interaction-playground.component.ts)
demonstrates `NhCollectionBaseComponent<ProjectViewModel>`. To make a focused
lister against the same API, create `project-list/project-list.component.ts`
under the management app's `src/app`. This complete component uses the sample's
API service and models; in your application, use your local imports instead.

```ts
import { Component, inject } from '@angular/core';
import { TranslateModule } from '@ngx-translate/core';
import {
  CollectionHttpRequestOptions,
  CollectionHttpResponse,
  NhCollectionBaseComponent
} from '@newheap/platform-common';
import { Observable, catchError, of } from 'rxjs';
import {
  ProjectApiService,
  ProjectCollectionRequestOptions,
  ProjectViewModel
} from 'sample-project-management-common';

@Component({
  selector: 'app-project-list',
  standalone: true,
  imports: [TranslateModule],
  templateUrl: './project-list.component.html'
})
export class ProjectListComponent extends NhCollectionBaseComponent<ProjectViewModel> {
  private readonly projectApi = inject(ProjectApiService);
  loadFailed = false;

  override getInitialRequestOptions(): CollectionHttpRequestOptions {
    return new ProjectCollectionRequestOptions({ page: 1, itemsPerPage: 2 })
      .orderAsc('name');
  }

  override getLocalStoragePartialKey(): string {
    return 'project-list-tutorial';
  }

  override async beforeLoad(): Promise<void> {
    this.loadFailed = false;
  }

  override async onLoad(
    options: CollectionHttpRequestOptions
  ): Promise<Observable<CollectionHttpResponse<ProjectViewModel>>> {
    const request = new ProjectCollectionRequestOptions({
      page: options.page,
      itemsPerPage: options.itemsPerPage,
      search: options.search,
      orderBy: options.orderBy,
      filter: options.filter
    });

    return this.projectApi.list(request).pipe(
      catchError(() => {
        this.loadFailed = true;
        return of(new CollectionHttpResponse<ProjectViewModel>({
          page: options.page,
          itemsPerPage: options.itemsPerPage,
          items: [],
          totalCount: 0,
          resultCount: 0
        }));
      })
    );
  }

  changePage(delta: number): Promise<void> {
    return this.setPage({
      page: this.collectionResponse.page + delta,
      itemsPerPage: this.collectionResponse.itemsPerPage
    });
  }
}
```

The base performs the first load. Use `appOnInit` for additional initialization.
The `loadFailed` flag selects the error state in the template.

Create `project-list.component.html` alongside it:

```html
<section [attr.aria-busy]="isLoading">
  <label>
    {{ 'project.search-projects' | translate }}
    <input #term type="search">
  </label>
  <button type="button" [disabled]="isLoading" (click)="search(term.value)">
    {{ 'project.apply' | translate }}
  </button>
  <button type="button" [disabled]="isLoading"
          (click)="sort({ sorts: [{ prop: 'name', dir: 'asc' }] })">A–Z</button>
  <button type="button" [disabled]="isLoading"
          (click)="sort({ sorts: [{ prop: 'name', dir: 'desc' }] })">Z–A</button>

  @if (isLoading) {
    <p role="status">{{ 'project.lister-loading' | translate }}</p>
  } @else if (loadFailed) {
    <p role="alert">{{ 'project.projects-load-failed' | translate }}</p>
    <button type="button" (click)="load()">{{ 'project.lister-retry' | translate }}</button>
  } @else {
    <table>
      <thead><tr><th scope="col">{{ 'project.key' | translate }}</th><th scope="col">{{ 'project.name' | translate }}</th></tr></thead>
      <tbody>
        @for (project of items; track project.id) {
          <tr><td>{{ project.key }}</td><td>{{ project.name }}</td></tr>
        } @empty {
          <tr><td colspan="2">{{ 'project.no-projects' | translate }}</td></tr>
        }
      </tbody>
    </table>
    <button type="button" [disabled]="collectionResponse.page <= 1"
            (click)="changePage(-1)">{{ 'project.lister-previous' | translate }}</button>
    <button type="button"
            [disabled]="collectionResponse.page * collectionResponse.itemsPerPage >= collectionResponse.totalCount"
            (click)="changePage(1)">{{ 'project.lister-next' | translate }}</button>
  }
</section>
```

Merge these keys into the existing `project` object in both management translation
files under `public/i18n`. The other keys are already present in the sample.

| Key | `en.json` | `nl.json` |
| --- | --- | --- |
| `lister-loading` | Loading projects… | Projecten laden… |
| `lister-retry` | Retry | Opnieuw proberen |
| `lister-previous` | Previous page | Vorige pagina |
| `lister-next` | Next page | Volgende pagina |

Add this entry to the `management` route's `children` in `sample.routes.ts`.
It inherits the parent's authentication guard:

```ts
{
  path: 'project-list',
  loadComponent: () => import('./project-list/project-list.component')
    .then(module => module.ProjectListComponent)
}
```

Open `/management/project-list`. Apply your application's shared styles to the
section, table and controls.

## 4. Verify the lister

1. Reload the page. Confirm a successful collection request and database-backed rows.
2. Search for part of a project name. Confirm the request contains `search`, resets
   to page 1 and returns only matching rows.
3. Change ordering. Confirm `orderBy` changes and the API returns the new order.
4. Use a small page size and move to the next page. Check `page`, `itemsPerPage`
   and `totalCount`; the browser should request a new page.
5. Search for an absent name. Confirm the empty state appears.
6. Test a failed API request. Confirm the error message and Retry button appear.

Continue with [Step 2: add CRUD](project-crud.md).
