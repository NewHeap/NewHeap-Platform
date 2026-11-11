# v-next

## NewHeap.Platform.AspNet.Proxy, NewHeap.Platform.AspNet.Proxy.Sqlite: .NET 10 and .NET 11

| Breaking change | Required action |
|---|---|
| Proxy packages now ship separate .NET 10 and .NET 11 assets with framework-aligned Microsoft dependencies in a new major release. | Upgrade both proxy packages together and restore and rebuild consumers for `net10.0` or `net11.0`. |

## NewHeap.Platform.Media: .NET 10 and .NET 11

| Breaking change | Required action |
|---|---|
| Media packages now ship separate .NET 10 and .NET 11 assets in a new major release. | Upgrade the media packages together and restore and rebuild consumers for `net10.0` or `net11.0`; keep EF Core and its providers on 10.x. |
