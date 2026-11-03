# v-next

## @newheap/nh-toastr: a development warning for a toast without a container (FB90)

- In development mode `NhToastrService` warns once in the console when a toast is shown while no
  `NhToastrContainerComponent` is rendered, and names the fix: one `<nh-toastr-container>` in the root template and
  `provideNhToastr(...)` in the root providers. Production builds log nothing, and the toasts behave as before.
