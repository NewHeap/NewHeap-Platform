# v-next

## NewHeap.Platform.AspNet.Proxy

| Breaking change | Required action |
| --- | --- |
| `INhProxyDraftTester` adds `TestSavedAsync` for testing saved rules without a draft. | Implement the new member in custom testers; the registered `NhProxyDraftTester` already supports it. |

## NewHeap.Platform.AspNet.Proxy.Sqlite

| Breaking change | Required action |
| --- | --- |
| Proxy storage upgrades to schema 3, which older schema-2 runtimes cannot open. | Back up SQLite before upgrading and restore that backup for a rollback; no consumer DAL migration is required. |
