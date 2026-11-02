# v-next

## @newheap/platform-common: the notification bell reads its overview once per change

- A bell or list built on `NhUserNotificationsAbstractComponent` reads its shown pages again when the polled
  overview changes, as before, but no longer reads the overview a second time right after the poll that brought
  the change. `reload()` takes an optional `refreshOverview` (default `true`), so a call from an application
  still refreshes the unread count.
