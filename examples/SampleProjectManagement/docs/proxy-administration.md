# Try the NewHeap Proxy demo

Run the administration panel locally, test a redirect, and preview a rewrite.
The demo uses SQLite and needs no separate database server.

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

For contributors, these commands run from the sample backend directory:

```text
dotnet build Applications/SampleProjectManagement.Proxy --configuration Release
dotnet test Tests/SampleProjectManagement.Core.Tests --filter FullyQualifiedName~ProxyAdministrationSamplesTests
dotnet test Tests/SampleProjectManagement.Core.Tests --filter FullyQualifiedName~ProxyRewriteSamplesTests
```

The existing sample cases cover SQLite redirects and administration (SPM-238),
and rewrite preview and forwarding to a local backend (SPM-239).
