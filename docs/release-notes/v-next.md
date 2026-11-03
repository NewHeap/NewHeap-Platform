# v-next

## NewHeap.Platform.AI.Chat.AspNet: a warning when a delivery skips a person the principal factory cannot rebuild (FB89)

- A live update, Web Push or in-app notification, agent notification or alert that skips a person because the
  application's `INhAiPrincipalFactory` rebuilds nobody for them now logs one content-free warning per person per
  hour, without the actor id. It names `INhAiPrincipalFactory`: when people who do sign in are skipped, the
  application's actor ids are its own and need a principal factory of its own. Who receives what does not change.
