# v-next

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: a missing tenant is no longer silent

Since 23.0.0 agent work stays in the caller's tenant, and a person without a tenant reaches only work
stored without one. An application that built `NhAssistantDecidingPerson` without the person's tenant
compiled as before, but its digests counted zero and its decisions, decision facts and subject work
read the work as unknown. The tenant is now a required parameter, and a caller without a tenant who
addresses one thread, run, proposal or subject stored under a tenant is logged as a warning by
`NhAssistantTenancy`, at most once an hour per path, with the path and the subject type only. The
answer stays the same: the work reads as unknown, as for a caller of another tenant. Listings and
callers of another tenant log nothing. Hosts without tenants see no change in behavior.

| Breaking change | Required action |
|---|---|
| `NhAssistantDecidingPerson(User, ActorId, TenantId)` requires `TenantId`; the default `null` is gone. | Pass the tenant the person signs in with, as the context resolver gives it; pass `null` explicitly only in a host without tenants or for its operators. |

## @newheap/platform-common

| Breaking change | Required action |
|---|---|
| Collection and mutate bases now expose `NhToastrService` instead of `ngx-toastr` and support Angular 20.3.28+, 21 and 22. | Install `@newheap/nh-toastr`, replace `ToastrModule.forRoot` with `provideNhToastr(...)`, render one `NhToastrContainerComponent` in the shell, migrate direct service injections and unsupported custom options, and remove the old dependency and stylesheet. |
