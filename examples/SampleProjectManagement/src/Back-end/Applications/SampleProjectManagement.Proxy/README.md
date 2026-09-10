# NewHeap Proxy demo

Try redirects, rewrites and the administration panel with a local SQLite database.

## Run locally

From this directory:

```text
dotnet run --launch-profile "Proxy demo"
```

Or select this project and the **Proxy demo** profile in Visual Studio or Rider.

| Setting | Value |
| --- | --- |
| Administration | [Open the panel](http://localhost:5289/newheap-proxy) |
| Username | `info@newheap.com` |
| Password | `NewHeap123!` |
| Example redirect | [Try `/old-projects`](http://localhost:5289/old-projects?campaign=demo) |

The fixed account is for the local Development demo. Changes persist in
`App_Data/proxy-demo.db`; editing or deleting a rule is preserved after restart.
Run one instance per database file.

## Run with Aspire

Select `SampleProjectManagement.AppHost` as the startup project. Open
`sample-project-management-proxy` in the dashboard; Aspire assigns the port.
The account and database are the same as above. Stop any standalone proxy first.

## Next steps

- [Follow the walkthrough](../../../../docs/proxy-administration.md) to edit a redirect and preview a rewrite.
- [Use your own account](../../../../docs/proxy-administration.md#configure-your-own-account) with the **Configured proxy** profile.
- [Browse configuration options](../../../../../../src/Back-end/Libraries/NewHeap.Platform.AspNet.Proxy/README.md#configuration).
