---
id: nh-frontend-toast-notifications
title: "Root-composed toast notifications"
area: frontend
reference: frontend-toast-notifications
summary: "Register NewHeap toast defaults at the application root, render one container, and inject the service wherever localized feedback is needed."
sample-cases: ["SPM-218"]
public-symbols: ["provideNhToastr", "NhToastrService", "NhToastrContainerComponent", "NhToastrAction"]
skills: ["newheap-frontend-development"]
providers: ["frontend"]
risk: medium
---
## Preferred approach

Call `provideNhToastr(...)` in root application composition and render one
`NhToastrContainerComponent` beside the root router outlet. Inject
`NhToastrService` in features and pass translated, user-facing messages to its
typed success, error, warning, or info methods. The package supports Angular
20.3.28 through 22 and `@ngx-translate/core` 17 or 18.

`@newheap/platform-common` accepts `@newheap/nh-toastr` 0.0.7 and 0.0.8.
Install a toast version within its peer range; do not bypass npm's peer checks
with `--force` or `--legacy-peer-deps`.

A toast is visible only while a container is rendered. In development mode
(`isDevMode()`) the service warns once in the console when a toast is shown
while no `NhToastrContainerComponent` is on the page, and names the fix; in
production nothing is logged.

## Offer undo with a toast action

To let people reverse a change they just made, such as a status change, pass
an `NhToastrAction` per toast as `action` in the `override` parameter, with a
translated `label` and the data the reversal needs as `value`. Subscribe to the
returned toast's `onAction`: it emits that value when the button is clicked and
completes when the toast is hidden, so the subscription ends by itself. The
toast is dismissed after the click unless the action sets `dismiss: false`.
Before reverting, check that no later change superseded the one being undone.

```typescript
const toast = this.toastr.success(message, undefined, {
  action: { label: this.translate.instant('project.undo'), value: previous.status }
});
toast?.onAction.subscribe(status => this.restoreStatus(previous.id, status as ProjectStatus));
```

The button is a real `<button type="button">`; its click is not a tap, so
`onTap` and `tapToDismiss` stay out of it. The timeout pauses while the pointer
is over the toast or keyboard focus is inside it, so a keyboard user can reach
the button; give such a toast a longer `timeOut`, such as 10000, because the
default five seconds is short for reaching it from elsewhere on the page.
Theme it with the `--nh-toastr-action-*` custom properties. Use
`triggerAction(toastId, value)` only to emit on `onAction` from your own code;
it neither renders a button nor dismisses the toast.

## Migrate from ngx-toastr

Replace `ToastrModule.forRoot(config)` or `provideToastr(config)` with
`provideNhToastr(config)`; the options `NhToastrConfig` keeps, such as
`timeOut`, `positionClass`, `closeButton` and `preventDuplicates`, keep their
names. Inject `NhToastrService` instead of `ToastrService`: `success`, `error`,
`info`, `warning`, `show`, `clear` and `remove` keep their arguments, and the
returned `NhActiveToast` offers `onShown`, `onHidden`, `onTap` and `onAction`.
Render one `<nh-toastr-container>` in the root template, because
`ngx-toastr` attached its container itself and `NhToastrService` does not.
Remove the `ngx-toastr` package and stylesheet, and rebuild a custom toast
component or an option outside `NhToastrConfig` with the `--nh-toastr-*` CSS
custom properties.

## Avoid

- Registering separate toast defaults in lazy features.
- Rendering a container per page or modal.
- Showing toasts without a rendered container; the service keeps them, but
  nobody sees them.
- Passing raw server errors, HTML, or stack traces to a toast.
- Making the whole toast the undo control through `onTap`; keyboard and screen
  reader users cannot reach it. Use an `action` button instead.
- Setting `action` in `provideNhToastr(...)`; every toast would get the button.
- Expanding the Angular peer range beyond versions exercised by the package
  compatibility check.

## Verification

Build the packaged library from the oldest supported Angular baseline and
compile consumers against Angular 20, 21, and 22 with
`npm --prefix src/Front-end run nh-toastr:test:compatibility`. In the sample,
verify that each application root owns exactly one container and that the
notification playground emits localized feedback through `NhToastrService`.
In development, check that the console shows no warning about a missing toast
container. Change a project's status in the management register, reach the
toast's Undo button with the keyboard and check that the previous status
returns; `component.spec.ts` in `nh-toastr` covers the button, the dismissal
and the pause on focus.

Run `npm --prefix src/Front-end run nh-common:test:compatibility` as well.
It installs the locally packed common and toast artifacts together and compiles
the consumer on each supported Angular version, so it exercises the pair being
released rather than a pinned older toast package.
Release validation runs this consumer check before publishing either the common
package or its toast dependency.
