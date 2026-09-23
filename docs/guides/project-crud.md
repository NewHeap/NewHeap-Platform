# Step 2: add create, update and delete

Continue from the [working project lister](first-project-lister.md). Keep its API
service and collection component to add create, edit and delete.

## 1. Define the editable fields

The [ProjectMutateModel](../../examples/SampleProjectManagement/src/Back-end/Libraries/SampleProjectManagement.Core/Models/Mutate/ProjectMutateModel.cs)
contains `DivisionId`, `OwnerUserId`, `Key`, `Name`, `Description`, `Status` and
`Deadline`. Required fields and length limits are validated by the backend.

Add the input map to your existing profile:

```csharp
CreateMap<ProjectMutateModel, Project>().MapOnlyIfChanged();
```

Keep the same editable fields in the frontend `ProjectMutateModel` interface.
When editing, copy those fields into a new object so cancelling the form leaves
the displayed row unchanged.

## 2. Add the mutation actions

The [project controller](../../examples/SampleProjectManagement/src/Back-end/Applications/SampleProjectManagement.Api/Controllers/ProjectController.cs)
already demonstrates the standard action helpers:

| Request | Controller helper | Expected success |
| --- | --- | --- |
| `GET /projects/{id}` | `DoGetById` | `200` with a view model for the editor |
| `POST /projects` | `DoCreate` | `200` with the created view model |
| `PUT /projects/{id}` | `DoUpdate` | `200`; reload the view model after updating |
| `DELETE /projects/{id}` | `DoDelete` | `200` after deletion |

For example, the create action delegates to the service:

```csharp
public Task<IActionResult> Create(
    [FromBody] ProjectMutateModel mutateModel,
    CancellationToken cancellationToken = default)
{
    return DoCreate(mutateModel, cancellationToken: cancellationToken);
}
```

Keep the full action attributes from the linked controller: HTTP method,
`[Authorize(Policy = "app.project.manage")]`, summary, description and response
metadata. Keep read access on `app.project.view`.

The [project service](../../examples/SampleProjectManagement/src/Back-end/Libraries/SampleProjectManagement.Core/Services/ProjectService.cs)
owns normalization, validation and the transaction. Return a failed `TaskResult`
when a business rule prevents a mutation. See
[transaction ownership](data-access.md#commit-a-business-operation) for operations
that combine writes and events.

## 3. Extend the Angular API service

Add these methods to your lister's `ProjectApiService`, following the
[sample service](../../examples/SampleProjectManagement/src/Front-end/projects/sample-project-management-common/src/lib/project-api.service.ts):

```ts
getById(id: string): Observable<ProjectViewModel> {
  return this.get<ProjectViewModel>(id);
}

createProject(model: ProjectMutateModel): Observable<ProjectViewModel> {
  return this.create<ProjectViewModel>(model);
}

updateProject(id: string, model: ProjectMutateModel): Observable<ProjectViewModel> {
  return this.update<ProjectViewModel>(id, model);
}

deleteProject(id: string): Observable<void> {
  return this.delete(id);
}
```

## 4. Create and edit through one modal

Use [ProjectEditModalComponent](../../examples/SampleProjectManagement/src/Front-end/projects/management/src/app/project-edit-modal/project-edit-modal.component.ts)
and its [form template](../../examples/SampleProjectManagement/src/Front-end/projects/management/src/app/project-edit-modal/project-edit-modal.component.html)
as the complete editor example. It extends
`NhModalMutateBaseComponent<ProjectMutateModel, ProjectViewModel>`.

`appOnInit` calls `newFormData` with `MutationType.Create` or `MutationType.Update`.
`onNewFormData` supplies the editable fields. For creation, use the selected
division's ID; the fixed division ID in the sample is seeded development data.

The create hook returns validation and success information to the base:

```ts
override async onSubmitCreate(): Promise<TaskResult<ProjectViewModel>> {
  return this.projectApi.createProject(this.formData!).taskResultLastValueFrom();
}
```

After a successful update, reload the view model with `getById`:

```ts
override async onSubmitUpdate(): Promise<TaskResult<ProjectViewModel>> {
  const result = await this.projectApi
    .updateProject(this.project()!.id, this.formData!)
    .taskResultLastValueFrom();

  if (result.isSuccess) {
    result.data = await this.projectApi.getById(this.project()!.id).lastValueFrom();
  }

  return result;
}
```

Bind fields to `formData`, show `nh-form-error-message` for field and form errors,
and disable Save when the form is invalid or submitting. The
[forms guide](forms-and-modals.md) explains those bindings.

In the list's parent, register the modal host with
`modalService.setViewContainerRef(viewContainerRef)`, then open the editor:

```ts
const modal = this.modalService.open(
  ProjectEditModalComponent,
  new NhModalOptions({ modalClasses: 'large' }),
  { project }
);
```

Pass the selected row for editing, or `undefined` for creation. Subscribe to the
content's `created` and `updated` events. After success, close the modal and reload
the lister so server-side ordering, filters and totals remain correct. Dispose
the content subscriptions through `modal.onClose`. The
[sample parent](../../examples/SampleProjectManagement/src/Front-end/projects/management/src/app/app.component.ts)
shows the host registration and modal event wiring.

## 5. Confirm deletion

Use `NhModalConfirmComponent`, as in the sample's
[openDeleteModal method](../../examples/SampleProjectManagement/src/Front-end/projects/management/src/app/app.component.ts#L239).
Only call `deleteProject` after confirmation. Await the API response before
removing the row or closing the dialog. On failure, keep the row and display the
error; the sample rejects deletion of projects with open tasks.

After success, reload the lister. If deleting the last row on a later page leaves
it empty, move to the previous valid page and reload. Disable the delete action
while the request is pending. Set the sample's `demoMode` to `false` to send
mutations to the API.

## 6. Verify the complete CRUD cycle

Use a fresh project without tasks so it can also be deleted:

1. Create a project with a unique key. Confirm POST success, its assigned ID and
   its presence after reloading the browser.
2. Try an empty required name. The editor stays open and shows validation errors;
   no row is added.
3. Edit the name. Confirm PUT success, reload the view model and verify the new
   name remains after a browser reload.
4. Cancel an edit. The existing row and stored data remain unchanged.
5. Cancel a delete, then confirm it. Only confirmation sends DELETE. The record
   disappears and a subsequent GET by ID returns `404`.
6. Repeat a write request as the viewer account. The API denies it with `403`.
7. Cause a request failure. Keep the editor's input and existing list intact;
   show an actionable error.
