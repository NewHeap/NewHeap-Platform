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
