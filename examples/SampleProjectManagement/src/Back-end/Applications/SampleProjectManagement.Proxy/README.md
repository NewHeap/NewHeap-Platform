# NewHeap Proxy demo

To run with the full sample, select `SampleProjectManagement.AppHost` as the
startup project. Open the `sample-project-management-proxy` resource URL in the
Aspire dashboard to reach the administration panel. Aspire selects the **Proxy demo**
profile and assigns an available port. The account and SQLite storage are the same
as below. Run one instance at a time because the SQLite file has exclusive ownership.

Select this project as the startup project and choose the **Proxy demo** launch
profile in Visual Studio or Rider. No database server or credential setup is needed.

From this directory:

```text
dotnet run --launch-profile "Proxy demo"
```

Open http://localhost:5289/newheap-proxy. Sign in as `administrator` with password
`NewHeap123!`. This fixed test credential is used only in the explicit
Development-only, loopback-only demo mode.

The first run creates `App_Data/proxy-demo.db` and a literal 302 redirect from
`/old-projects` to `/projects?source=proxy`. Visit
http://localhost:5289/old-projects?campaign=demo to try it. Use the panel to edit,
test, disable or delete rules and view login activity. Changes survive restarts;
the example rule is not recreated after editing or deleting it.

Choose **Configured proxy** to use your own account and storage settings instead.
See [configuration and verification](../../../../docs/proxy-administration.md)
for the complete setup and the runnable smoke check. The demo covers literal
redirects and administration; managed rewrite editing is still a library gap.
