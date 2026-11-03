# v-next

## @newheap/platform-common: regional languages fall back to their base language (FB88)

- `NhTranslateBrowserLoader` loads `nl.json` when the file of a regional language such as `nl-NL` or `nl_BE`
  does not exist (`404`), and `en.json` for `en-GB`, so the application's texts follow the libraries' own texts,
  which already did. A regional file that exists is used as it is; other errors propagate as before. An
  application that relied on a regional language showing no texts of its own now shows those of its base language.

## @newheap/nh-toastr: a development warning for a toast without a container (FB90)

- In development mode `NhToastrService` warns once in the console when a toast is shown while no
  `NhToastrContainerComponent` is rendered, and names the fix: one `<nh-toastr-container>` in the root template and
  `provideNhToastr(...)` in the root providers. Production builds log nothing, and the toasts behave as before.
