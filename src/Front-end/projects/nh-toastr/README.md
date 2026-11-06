# @newheap/nh-toastr

NewHeap toast notifications for Angular applications.

## Installation

The package is public on npmjs.org and installs without a registry token. An optional project `.npmrc` can make the public source explicit:

```text
registry=https://registry.npmjs.org/
@newheap:registry=https://registry.npmjs.org/
```

Install the package:

```bash
npm install @newheap/nh-toastr
```

View available versions on [npmjs.org](https://www.npmjs.com/package/@newheap/nh-toastr). For NuGet, npm and AI-plugin installation, see [Consume public packages](../../../../docs/how-to/consume-public-packages.md).

## Compatibility

The package supports Angular 20.3.28 through Angular 22 and
`@ngx-translate/core` 17 or 18. It is built on the oldest supported Angular
baseline so the published partial-Ivy output remains consumable across that
range.

## Usage

Register the provider in the root application module:

```typescript
import { provideNhToastr } from '@newheap/nh-toastr';

@NgModule({
  providers: [
    provideNhToastr({ /* options */ })
  ],
  bootstrap: [AppComponent]
})
export class AppModule {}
```

Add the toast container once to the root application template:

```html
<div class="container">
  <router-outlet></router-outlet>
</div>
<nh-toastr-container></nh-toastr-container>
```

Toasts are visible only while a container is rendered. In development mode the
service warns once in the console when a toast is shown without one.

Inject the service where a notification is needed:

```typescript
import { NhToastrService } from '@newheap/nh-toastr';

@Component({
  selector: 'my-component'
})
export class MyComponent {
  private readonly toastr = inject(NhToastrService);

  showToast(): void {
    this.toastr.success('Hello world');
  }
}
```

### Action button

Pass `action` per toast through the `override` parameter to render a real
button, such as "Undo", inside the toast. Clicking it emits `action.value` on
the toast's `onAction` and dismisses the toast; set `dismiss: false` to keep it
open. The click never counts as a tap, so `onTap` and `tapToDismiss` are not
involved. Pass a translated label, as for the message.

```typescript
const toast = this.toastr.success(
  this.translate.instant('project.status-update-succeeded', { key: project.key }),
  undefined,
  { action: { label: this.translate.instant('project.undo'), value: previousStatus } }
);

toast?.onAction.subscribe(status => this.restoreStatus(project, status as ProjectStatus));
```

The timeout pauses while the pointer is over the toast or keyboard focus is
inside it, so keyboard users can reach the button. Theme the button with the
`--nh-toastr-action-*` custom properties (`color`, `background`,
`hover-background`, `border-color`, `border-width`, `radius`, `padding`,
`font-size`, `font-weight` and `min-height`), and place it with
`--nh-toastr-actions-justify` and `--nh-toastr-actions-margin-top`;
its focus outline uses `--nh-toastr-focus-color`.
