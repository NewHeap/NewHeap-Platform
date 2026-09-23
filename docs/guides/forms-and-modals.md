# Edit a record in a modal

Use `NhModalMutateBaseComponent<TMutate, TView>` for an edit form. It coordinates
form initialization, submission state and validation results with the modal.

## Start with the project editor

Configure the [Angular root module](collections.md#configure-the-application-once),
then use the sample's [ProjectEditModalComponent](../../examples/SampleProjectManagement/src/Front-end/projects/management/src/app/project-edit-modal/project-edit-modal.component.ts)
and its [template](../../examples/SampleProjectManagement/src/Front-end/projects/management/src/app/project-edit-modal/project-edit-modal.component.html)
as a complete example. Its mutate model contains only editable fields.

The component extends
`NhModalMutateBaseComponent<ProjectMutateModel, ProjectViewModel>` and initializes
the form in this hook:

```ts
override async appOnInit(): Promise<void> {
  await this.newFormData(
    this.project() ? MutationType.Update : MutationType.Create
  );
}
```

`onNewFormData` returns either a copy of the record's editable fields or the
defaults for a new record. Use `appOnInit` and `appOnDestroy` for NewHeap component
lifecycle work; the base owns Angular's `ngOnInit` and `ngOnDestroy`.

## Show field validation

Inside the template's form and `@if (formData)` block, bind a field and its error
message to the same Angular control:

```html
<label>
  <span>{{ 'project.name' | translate }}</span>
  <input #projectName="ngModel" name="name" required maxlength="150"
         [(ngModel)]="formData.name">
  <nh-form-error-message [control]="projectName.control"></nh-form-error-message>
</label>
```

Import `FormsModule`, `NhCommonModule` and `TranslateModule` in the component.
Keep translation keys under the module object, such as `project.name`. The full
template also shows form-level errors and disables Save while the form is invalid
or a request is running.

## Submit through the API service

In `onSubmitCreate`, return the API result to the base so it can display validation
errors and emit the created event:

```ts
override async onSubmitCreate(): Promise<TaskResult<ProjectViewModel>> {
  return this.projectApi
    .createProject(this.formData!)
    .taskResultLastValueFrom();
}
```

`TaskResult` is exported by `@newheap/platform-common`. For updates, the sample
sends the mutate model and then reloads the view model after a successful save.

## Open the modal

Inject `NhModalService` into the parent and import `NhModalOptions` from
`@newheap/platform-common`. Pass the selected `project` as component input:

```ts
const modal = modalService.open(
  ProjectEditModalComponent,
  new NhModalOptions({ modalClasses: 'large' }),
  { project }
);
```

Handle the content's `created`/`updated` events to refresh the row and close the
modal; release those subscriptions when `modal.onClose` fires. The
[sample modal flow](../../examples/SampleProjectManagement/docs/sample-catalog.md#edit-modal)
describes that interaction. Try clearing Name, then enter a valid value and save:
the invalid form cannot submit, and a successful save updates the register.

## Further reading

The [lifecycle reference](../consumer-guide/frontend-lifecycle-modals.md) covers
route-driven initialization, cleanup and non-mutating modals.
