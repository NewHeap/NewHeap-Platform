# v-next

## NewHeap.Platform.AI.Chat.AspNet: a warning when a delivery skips a person the principal factory cannot rebuild (FB89)

- A live update, Web Push or in-app notification, agent notification or alert that skips a person because the
  application's `INhAiPrincipalFactory` rebuilds nobody for them now logs one content-free warning per person per
  hour, without the actor id. It names `INhAiPrincipalFactory`: when people who do sign in are skipped, the
  application's actor ids are its own and need a principal factory of its own. Who receives what does not change.

## @newheap/platform-common: regional languages fall back to their base language (FB88)

- `NhTranslateBrowserLoader` loads `nl.json` when the file of a regional language such as `nl-NL` or `nl_BE`
  does not exist (`404`), and `en.json` for `en-GB`, so the application's texts follow the libraries' own texts,
  which already did. A regional file that exists is used as it is; other errors propagate as before. An
  application that relied on a regional language showing no texts of its own now shows those of its base language.
