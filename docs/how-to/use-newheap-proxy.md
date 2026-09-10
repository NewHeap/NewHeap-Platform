# Proxy usage reference

Start with [installation and configuration](../../src/Back-end/Libraries/NewHeap.Platform.AspNet.Proxy/README.md),
or follow the [local demo](../../examples/SampleProjectManagement/docs/proxy-administration.md).

- [Connect the proxy to your application](#host-integration)
- [Secure administration](#administration)
- [Match exact paths](#literal-redirects)
- [Match variable paths](#regex-redirects)
- [Create and test rewrites](#stored-rewrites-and-the-test-tool)
- [Save and activate changes](#persistence-and-activation)
- [Resolve common problems](#troubleshooting)
- [Configure local rule chains](#local-rule-chain-limit)
- [Customize YARP](#native-yarp-customization)
- [Seed rules before startup](#seed-a-redirect-before-starting-the-host)

## Host integration

Host the proxy at the origin root, for example `https://proxy.example/`.
Use `AddNewHeapProxy` and `UseNewHeapProxy` as shown in the installation example.
The database is created and rules are loaded before requests are accepted.

| If your application uses… | Integration |
| --- | --- |
| An upstream proxy | Configure trusted forwarded headers before `UseNewHeapProxy` so client IPs and public URLs are correct. |
| Its own authentication | Keep its authentication scheme as the default. Administration uses the separate `NewHeapProxy` scheme. |
| CORS, rate limiting or request timeouts | Place the required middleware after route selection and before endpoint execution. |
| Catch-all routes | `/newheap-proxy` remains reserved for administration. |

`UseNewHeapProxy` already maps the proxy endpoints; do not also call `MapNewHeapProxy`.
The lower-level `MapNewHeapProxy` alternative maps YARP endpoints only and does not
install redirect processing or administration.

## Administration

Open `/newheap-proxy` to manage rules and view login activity.
Supply credentials through deployment secrets; use user-secrets for local development.
See [Configuration](../../src/Back-end/Libraries/NewHeap.Platform.AspNet.Proxy/README.md#configuration)
for account, session, IP restriction and audit settings.

| Task | What to do |
| --- | --- |
| Set a password | Configure `Administrator:UserName` and either `Password` or `PasswordHash`. Setting both password options prevents startup. |
| Choose a password | Use at most 1,024 characters with at least one non-whitespace character. Leading and trailing spaces count. |
| Use a password hash | Supply an ASP.NET Identity hash. The [demo includes a hash command](../../examples/SampleProjectManagement/docs/proxy-administration.md#use-a-password-hash). |
| Rotate credentials | Update your deployment secret and restart. Existing sessions must sign in again. |
| End existing sessions | Change `Administrator:CredentialVersion` and restart. |
| Keep sessions across restarts | Use a stable `PasswordHash` and persist ASP.NET Data Protection keys. `Password` requires a fresh login after every restart. |
| Restrict client IPs | Enable the IP allowlist and add addresses or CIDR ranges. An enabled empty list denies all administration access. |

HTTPS is required outside Development. Missing credentials make the panel
unavailable with HTTP 503 while ordinary proxy traffic continues. Login activity
records UTC timestamps, outcomes and client IPs. If activity cannot be recorded,
login fails.

## Literal redirects

Use exact matching to move one URL. Leave **Use regular expression** off.

| Setting | Value |
| --- | --- |
| Source path | `/old-projects` |
| Destination | `/projects?source=proxy` |
| Status | `302` |

Choose **Test draft**, inspect the destination, then **Save and activate**.

| Matching rule | Behavior |
| --- | --- |
| Path | Exact and case-sensitive; `/projects` and `/projects/` are different. The query does not participate in path matching. |
| Host | Case-insensitive. A hostname without a port accepts any port; a specified port must match. Wildcards are unsupported. |
| Method | Exact, case-sensitive HTTP method, such as `GET`. |
| Empty host or method list | Accepts all hosts or methods. |
| Priority | Lower numbers win; ties are ordered by rule ID. Disabled rules are skipped. |
| Status | `301`, `302`, `303`, `307` or `308`; default `302`. |
| Destination | A path starting with `/` or an absolute HTTP(S) URL. |

For incoming `/old-projects?campaign=demo&source=old` and destination
`/projects?source=proxy`, query modes produce:

| Query mode | Result |
| --- | --- |
| Preserve | `/projects?campaign=demo&source=proxy` |
| Replace | `/projects?source=proxy` |
| Discard | `/projects` |

Preserve merges keys case-insensitively, with destination values taking precedence.
Repeated values and URL fragments are retained; query escaping is normalized.
Query parameter order is not significant in these examples.

Destinations containing credentials, control characters, backslashes, `//host`
paths or non-HTTP schemes are rejected. A destination starting with `/` cannot
redirect to the same path, even when only the query changes.

## Regex redirects

Enable **Use regular expression** to match variable paths. The pattern sees the
URL-escaped path and query, including `?`. Use `^` and `$` to match the entire input.
Matching is case-sensitive unless the pattern specifies otherwise.

| Pattern | Destination | Input | Result |
| --- | --- | --- | --- |
| `^/old-projects/([^?]+)(\?.*)?$` | `/projects/$1$2` | `/old-projects/42?tag=new` | `/projects/42?tag=new` |
| `^/old-projects/([^?]+)(\?.*)?$` | `/projects/$1` | `/old-projects/42?tag=new` | `/projects/42` |

Use `$1` for numbered captures, `${name}` for named captures, and `$$` for a literal
dollar sign. Unmatched input is not appended. **Query modes do not apply to regex
rules**; include the query captures you want to keep in the destination.

- Captures retain URL escaping. Absolute destination hosts and ports must be fixed;
  substitutions belong in the path, query or fragment.
- Patterns are limited to 2,048 characters, destination templates to 4,096, and
  inputs and expanded destinations to 65,536.
- Invalid patterns or destination templates cannot be saved. Unsafe expansions
  or input/output limits skip redirect evaluation; timeouts return HTTP 503.
- Older proxy versions without regex support cannot load saved regex rules.

## Stored rewrites and the test tool

Open **Rewrites**, choose **Create rewrite**, and enter:

| Setting | Value |
| --- | --- |
| Path template | `/api/{**rest}` |
| Backend URL | `https://backend.example/base/` |
| Path transform | Remove prefix `/api` |
| Test URL | `https://public.example/api/projects/42?source=test` |
| Expected backend request | `https://backend.example/base/projects/42?source=test` |

The backend's base path is added after path transforms. Replace the example
backend with your own service before saving. The browser keeps its original URL.

To reuse a destination, select it and choose **Load destination**. Changes affect
all rules using that destination; deleting a rule keeps its destination available.

Advanced JSON configures header/query conditions, ordered transforms, policies,
timeouts and health checks. Valid edits synchronize with the form; invalid JSON
stays visible for correction. Keep the NewHeap JSON structure shown in the editor:
native YARP examples use different property names and nesting.

### Choose a test

| Tool | Evaluates |
| --- | --- |
| **Test a URL** | A GET against all saved rules. Enter `/foo?s=1` for the current proxy host or a complete HTTP(S) URL for another host. |
| Redirect **Test draft** | The one unsaved redirect, without the other rules. |
| Rewrite **Test draft** | Your rewrite changes alongside all saved rules, with redirects taking precedence. |
| **Simulate enabled** | Includes a disabled rewrite draft in the test. |

Tests report the matching rule and destination. Rewrite previews also show route
values and safe outgoing headers. Conflicting saved revisions require reloading
the editor. Saved-versus-active differences are shown explicitly.

Tests do not save changes or contact backends. They cannot verify connectivity,
health, authorization, CORS, rate limiting or backend responses. Custom host
callbacks/transforms and rules from other configuration sources are outside preview.
Response header transforms are listed but not evaluated. Only synthetic non-secret
headers are accepted; administrator credentials and cookies are never copied.

For testing from code, use `INhProxyDraftTester.TestSavedAsync`, `TestRewriteAsync`
or `TestRedirectAsync`. Supply the expected revisions when required.

## Persistence and activation

Use **Save and activate** to apply changes. Redirects and rewrites have separate
saved and active revisions.

| Result | Next step |
| --- | --- |
| Saved and active revisions match | The saved rules are running. |
| Another editor saved first | Reload the latest rules and reapply your changes; the conflicting save does not overwrite them. |
| Saved rules could not be activated | The previous rules remain active. Retry activation from the panel without saving another revision. |

For live updates from code, use `INhProxyConfigurationService.SaveRedirectsAsync`
or `SaveRewritesAsync`. Submit the complete rule collection with its expected
revision, then inspect `TaskResult.Success`. If activation fails after saving,
`Data` retains the committed revision so you can retry. Cancellation after a
successful commit does not undo the save or prevent activation.

The lower-level store saves only; it does not activate changes. Use it for
[offline seeding](#seed-a-redirect-before-starting-the-host).

## Troubleshooting

| Symptom | Check or action |
| --- | --- |
| Administration returns HTTP 503 | Configure an administrator account and restart. |
| Administration access is denied | Check HTTPS, the IP allowlist and trusted forwarded-header configuration. |
| Login is throttled | Wait for the login attempt window to reset; the limit is shared across clients. |
| A redirect does not match | Check case, trailing slash, host, method, enabled state and priority. Use **Test a URL** to find a competing rule. |
| Redirect evaluation returns HTTP 503 | Simplify expensive regex patterns or review the configured redirect timeout. |
| A request returns HTTP 508 | Shorten the local rule chain or review `Limits:MaximumChainDepth`. |
| A preview differs from live traffic | Compare saved and active revisions, then check backend responses and host customizations that preview does not evaluate. |
| The database prevents startup | Check the file path, permissions, compatibility and whether another proxy instance owns it. See [SQLite storage](../../src/Back-end/Libraries/NewHeap.Platform.AspNet.Proxy.Sqlite/README.md). |

## Local rule-chain limit

Each local redirect or rewrite counts as one step. Two steps are allowed by
default; a third returns HTTP 508 before redirecting or forwarding. This also
rejects a legitimate longer chain. Configure `Limits:MaximumChainDepth` in the
[configuration table](../../src/Back-end/Libraries/NewHeap.Platform.AspNet.Proxy/README.md#configuration).

The check follows managed rules on the same scheme, host and port. It stops at
administration paths, external destinations or other routes; it cannot detect
loops involving external aliases or backend responses.

| Redirect status | Method used for the next step |
| --- | --- |
| `301` / `302` | POST becomes GET; other methods stay unchanged. |
| `303` | GET, except HEAD stays HEAD. |
| `307` / `308` | Original method. |

## Native YARP customization

Use `ConfigureYarp` in the registration callback for custom YARP behavior:

```csharp
builder.Services.AddNewHeapProxy(options =>
{
    options.ConfigureYarp(yarp =>
    {
        yarp.ConfigureHttpClient((_, handler) =>
        {
            handler.ConnectTimeout = TimeSpan.FromSeconds(5);
        });
    });
});
```

Import `Microsoft.Extensions.DependencyInjection` for YARP builder extensions.
Repeated `ConfigureYarp` calls replace the callback. Restart after changing options.

## Seed a redirect before starting the host

For offline setup, stop the proxy and use the same absolute database path as the
host, outside the webroot. This example adds a redirect while retaining existing
rules. The administration panel or configuration service remains the preferred
way to change a running proxy.

```csharp
using Microsoft.Extensions.Options;
using NewHeap.Platform.AspNet.Proxy;
using NewHeap.Platform.AspNet.Proxy.Sqlite;

var storageOptions = new NhProxySqliteOptions { DatabasePath = databasePath };
await using (var store = new NhProxySqliteConfigurationStore(Options.Create(storageOptions)))
{
    await store.InitializeAsync();
    var current = await store.LoadRedirectsAsync();
    var rule = new NhProxyRedirectRule
    {
        Id = Guid.NewGuid(),
        Name = "Moved page",
        Match = new NhProxyRedirectMatch { Path = "/old" },
        Target = "/new"
    };
    var result = await store.SaveRedirectsAsync(
        new NhProxyRedirectSaveRequest(current.Revision, current.Rules.Add(rule)));
    if (!result.Success)
    {
        // Handle validation or revision conflicts before starting the host.
        return;
    }
}

builder.Services.AddNewHeapProxy(configureStorage: options =>
    options.DatabasePath = databasePath);
var app = builder.Build();
app.UseNewHeapProxy();
app.Run();
```

Dispose the offline store before starting the host so it can acquire the database.
See [backups and upgrades](../../src/Back-end/Libraries/NewHeap.Platform.AspNet.Proxy.Sqlite/README.md#backups-and-upgrades)
before working with an existing database.
