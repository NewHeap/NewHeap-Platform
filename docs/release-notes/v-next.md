# v-next

## NewHeap.Platform.AspNet.Proxy

| Breaking change | Required action |
| --- | --- |
| Rewrite validation now builds native transforms and compiles route regex, including disabled rules; previously accepted malformed patterns are rejected when loading or saving. | Correct invalid Pattern transforms and route regex in existing configurations before upgrading. |

## NewHeap.Platform.AspNet.Proxy.Sqlite

| Breaking change | Required action |
| --- | --- |
| Proxy storage upgrades to SQLite schema 4 for durable API change auditing; older binaries cannot reopen it. | Back up before upgrading; restore a compatible pre-upgrade backup before downgrading. |
