# Try the NewHeap Proxy demo

Run the administration panel locally, test a redirect, and preview a rewrite.
The demo uses SQLite and needs no separate database server.

For host-owned authentication, see [custom UI and API authentication](../../../docs/how-to/use-newheap-proxy.md#custom-authentication).
SPM-238's `ProxyHostAuthenticationSamplesTests` demonstrates real ASP.NET cookie
and bearer handlers, independent policies, logout and attributed API writes.
The runnable demo keeps its existing local-account defaults.

[Configuration options](../../../src/Back-end/Libraries/NewHeap.Platform.AspNet.Proxy/README.md#configuration) · [Usage reference](../../../docs/how-to/use-newheap-proxy.md)

## Run the demo

From `examples/SampleProjectManagement/src/Back-end`:

```text
dotnet run --project Applications/SampleProjectManagement.Proxy --launch-profile "Proxy demo"
```

In Visual Studio or Rider, select `SampleProjectManagement.Proxy` as the startup
project and choose **Proxy demo**.

Open [the administration panel](http://localhost:5289/newheap-proxy) and sign in:

| Setting | Value |
| --- | --- |
| Username | `info@newheap.com` |
| Password | `NewHeap123!` |
| Database | `App_Data/proxy-demo.db` in the proxy project |

The fixed account works only in the local Development demo. Rules survive
restarts; restarting requires a new login. Run one instance per database file.

## Run through Aspire

Select `SampleProjectManagement.AppHost` as the startup project, or run from the
same backend directory:

```text
aspire start --apphost Orchestration/SampleProjectManagement.AppHost/SampleProjectManagement.AppHost.csproj
```

Open `sample-project-management-proxy` in the Aspire dashboard. The port is
assigned automatically; the account and database are the same as above. Stop a
standalone proxy before starting Aspire. The proxy does not depend on the sample
API, PostgreSQL or RabbitMQ.

## Try a redirect

The first run creates a redirect from `/old-projects` to `/projects?source=proxy`.

1. Open [the example URL](http://localhost:5289/old-projects?campaign=demo).
   With Aspire, use the proxy's assigned address instead of `localhost:5289`.
2. Confirm that the browser reaches `/projects` and shows the moved-page message.
3. Open the redirect in the panel, change its destination query, and choose **Test draft**.
4. Choose **Save and activate**, then visit the example URL again.

You can also disable or delete the rule. It is not recreated after edits or
deletion. To create it again, use these values:

| Field | Value |
| --- | --- |
| Name | `Moved project overview` |
| Source path | `/old-projects` |
| Destination | `/projects?source=proxy` |
| Status | `302` |

For variable paths, follow the [regex examples](../../../docs/how-to/use-newheap-proxy.md#regex-redirects).

## Preview a rewrite

Open **Rewrites**, then **Create rewrite**:

| Field | Value |
| --- | --- |
| Name | `Project API` |
| Path template | `/api/{**rest}` |
| Backend URL | `https://backend.example/base/` |
| Path transform | Remove prefix `/api` |
| Test URL | `https://public.example/api/projects/42?source=test` |

Choose **Test draft**. The expected backend URL is
`https://backend.example/base/projects/42?source=test`.

This is a preview: no backend is contacted. Replace the example backend with
your own service before saving. Use **Test a URL** in the top bar to check which
saved rule handles a URL. See [testing behavior](../../../docs/how-to/use-newheap-proxy.md#choose-a-test)
for the differences between tests.

## Configure your own account

Supply your account through environment variables or local user-secrets:

```text
NewHeapProxy__Administrator__UserName=<your username>
NewHeapProxy__Administrator__Password=<your password>
```

Run with **Configured proxy**, which uses your settings instead of the demo account:

```text
dotnet run --project Applications/SampleProjectManagement.Proxy --launch-profile "Configured proxy"
```

This profile creates `App_Data/newheap-proxy.db` by default, separate from the demo.
See [Configuration](../../../src/Back-end/Libraries/NewHeap.Platform.AspNet.Proxy/README.md#configuration)
for all options. For deployment, use platform-managed secrets and HTTPS, and host
the proxy at the origin root, for example `https://proxy.example/`.

### Use a password hash

As an alternative to `Password`, generate an ASP.NET Identity hash locally. From
the sample backend directory:

```text
dotnet run --project Applications/SampleProjectManagement.Proxy -- --hash-password
dotnet user-secrets set --project Applications/SampleProjectManagement.Proxy NewHeapProxy:Administrator:UserName "<your username>"
dotnet user-secrets set --project Applications/SampleProjectManagement.Proxy NewHeapProxy:Administrator:PasswordHash "<generated hash>"
```

The command reads the password without displaying it. Leave `Password` unset
when using `PasswordHash`; never commit either value. Then select **Configured proxy**.

## Verification

### Optional server-to-server API

The sample leaves the API off unless `ProxyApi=true` is supplied. For example:

```text
dotnet run --project Applications/SampleProjectManagement.Proxy --launch-profile "Proxy demo" -- --ProxyApi=true
```

The sample then calls `MapProxyEndpoints()` before `UseNewHeapProxy()`. Call
`GET /newheap-proxy/api/status` with Basic authentication using the selected
profile's administrator account. Use HTTPS outside Development. The same IP
allowlist applies. API requests do not create logins or consume login attempts;
incorrect API credentials have an independent failure budget. Configuration changes
are audited atomically in SQLite and can be queried through `GET /newheap-proxy/api/audit`.
Reads and previews create no change events. No cookies are issued. See the [API reference](../../../docs/how-to/use-newheap-proxy.md#optional-management-api)
for revision-checked snapshot updates, activation retry and draft testing.

`ProxyApiSamplesTests` is executable SPM-238/SPM-239 evidence for Basic
authentication without login records, redirect and rewrite saves, durable change
audit, conflict handling, safe preview and
the generated OpenAPI route/response contracts using real SQLite. The same workflow
runs with enum names and numeric values, including nested rewrite transforms.
It also submits a transform without `kind` and verifies a `400` response containing
the JSON field path and an explanation, without persisting the rejected change.
The example also rejects invalid path-transform templates and route regex before
saving, including disabled rules. Native configuration construction runs locally;
no backend is contacted and rejected candidates create no change-audit events.

### Contributor checks

For contributors, these commands run from the sample backend directory:

```text
dotnet build Applications/SampleProjectManagement.Proxy --configuration Release
dotnet test Tests/SampleProjectManagement.Core.Tests --filter FullyQualifiedName~ProxyAdministrationSamplesTests
dotnet test Tests/SampleProjectManagement.Core.Tests --filter FullyQualifiedName~ProxyRewriteSamplesTests
dotnet test Tests/SampleProjectManagement.Core.Tests --filter FullyQualifiedName~ProxyApiSamplesTests
```

The existing sample cases cover SQLite redirects and administration (SPM-238),
and rewrite preview and forwarding to a local backend (SPM-239).

The URL test and rewrite draft test include a **Rule evaluation** section. Each
rule shows its name and path, whether it was selected, rejected or skipped, and
a reason such as a path/regex, method, host/port, header or query mismatch.
Redirects stop at the first match; subsequent redirects and all rewrites are
shown as skipped. When rewrites are evaluated, matching alternatives explain
that another rule wins by priority or route specificity. Review links open the
corresponding rule. Results describe the entered URL, not later chain steps or
backend responses. No destination request is made.
