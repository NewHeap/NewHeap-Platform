# Proxy usage reference

Start with [installation and configuration](../../src/Back-end/Libraries/NewHeap.Platform.AspNet.Proxy/README.md),
or follow the [local demo](../../examples/SampleProjectManagement/docs/proxy-administration.md).

- [Connect the proxy to your application](#host-integration)
- [Secure administration](#administration)
- [Enable the server-to-server API](#optional-management-api)
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

## Optional management API

The management API is disabled by default. Enable it explicitly before installing
the proxy middleware:

```csharp
builder.Services.AddNewHeapProxy(builder.Configuration.GetSection("NewHeapProxy"));
var app = builder.Build();

// Configure trusted forwarded headers here when deployed behind a trusted proxy.
app.MapProxyEndpoints();
app.UseNewHeapProxy();
app.Run();
```

`MapProxyEndpoints` is separate from `MapNewHeapProxy`, which maps YARP forwarding.
Call `MapProxyEndpoints` once, before `UseNewHeapProxy`; calling it afterwards
throws a configuration error. The API occupies `/newheap-proxy/api` in an isolated
branch, so neither redirects nor catch-all rewrites can intercept its requests.
Omit the call to keep these endpoints unavailable.

### Authentication and limits

Send `Authorization: Basic <base64(UTF-8 username:password)>` on every request,
using the configured `Administrator:UserName` and `Password` or `PasswordHash`.
The username cannot contain a colon; the password may contain colons. The API
does not issue or accept the administration session cookie. Host authentication
defaults remain unchanged. HTTPS is required outside Development; configure
trusted forwarded headers before the API when HTTPS terminates at an upstream
proxy. The same administration IP allowlist applies.

For example, curl prompts for the password without putting it in the command:

```sh
curl --user automation https://proxy.example/newheap-proxy/api/status
```

API authentication does not create a login record or consume a browser login
attempt. Successful calls do not consume the API failure budget either. Only
incorrect or malformed credentials consume `Administrator:ApiAuthenticationFailureLimit`
per `Administrator:ApiAuthenticationFailureWindow` (defaults: 5 failures per minute).
After that budget is exhausted, API authentication returns `429` until the window
resets; browser login has its own independent budget. Requests carrying an `Origin` header
are rejected: this interface is for server clients, not browser JavaScript.
Cookie/antiforgery-based browser management remains in the existing panel.

JSON bodies use the existing `Limits:MaximumTestRequestBytes` limit (64 KiB by
default), including configuration saves. Unknown JSON members, missing required
constructor fields and malformed JSON are rejected. This API uses the NewHeap
configuration model, not raw YARP configuration.

Enum fields accept both names and numeric values, including nested rewrite
transforms. For example, redirect `status` accepts `"Found"` or `302`; a path
transform's `operation` accepts `"AddPrefix"` or `0`. Names are case-insensitive.
Unknown names and unsupported numeric values return `400`.

### Endpoints

All paths below are relative to `/newheap-proxy/api`.

| Method | Path | Contract |
| --- | --- | --- |
| GET | `/redirects` | Returns `NhProxyRedirectConfiguration`. |
| PUT | `/redirects` | Accepts `NhProxyRedirectSaveRequest`; replaces and activates the redirect snapshot. |
| GET | `/rewrites` | Returns `NhProxyRewriteConfiguration`, including shared clusters. |
| PUT | `/rewrites` | Accepts `NhProxyRewriteSaveRequest`; replaces and activates the rewrite snapshot. |
| GET | `/status` | Returns `NhProxyStatus` with independent desired/active revisions. |
| GET | `/audit` | Returns a paged `NhProxyChangeAuditPage` inside `NhProxyApiResult<T>`. |
| POST | `/redirects/activate` | Retries the latest saved redirect revision; no body. |
| POST | `/rewrites/activate` | Retries the latest saved rewrite revision; no body. |
| POST | `/test` | Accepts `NhProxySavedTestRequest` to preview saved rules. |
| POST | `/redirects/test` | Accepts `NhProxyRedirectTestRequest` to preview a draft. |
| POST | `/rewrites/test` | Accepts `NhProxyRewriteTestRequest` to preview a draft and optional clusters. |

### Read, modify, replace

Read the current engine configuration, retain rules that should remain, and
submit the changed snapshot with its revision as `ExpectedRevision`. Add a rule
with a stable new ID to create it; replace it to edit it; change `Enabled` to
toggle it; omit it to delete it. Rewrite saves include the complete cluster list.
**PUT replaces the entire engine configuration; omitted rules are deleted.**

Before saving, the shared validator checks the complete candidate snapshot,
builds its YARP transforms and compiles regex route constraints. This includes
disabled rules. Invalid route or transform patterns return `400` with a readable
description before any configuration, revision, active rules or audit event changes.
The same check applies to panel saves, direct configuration-store saves and draft
previews. It requires no test URL and makes no backend requests; use the preview
endpoints to check which rule matches a particular URL. Backend availability and
responses are not part of this validation.

Before saving, the shared validator checks the complete candidate snapshot,
builds its YARP transforms and compiles regex route constraints. This includes
disabled rules. Invalid route or transform patterns return `400` with a readable
description before any configuration, revision, active rules or audit event changes.
The same check applies to panel saves, direct configuration-store saves and draft
previews. It requires no test URL and makes no backend requests; use the preview
endpoints to check which rule matches a particular URL. Backend availability and
responses are not part of this validation.

Example body for an empty redirect store at revision zero:

```json
{
  "expectedRevision": 0,
  "rules": [
    {
      "id": "9b02c0ac-26c5-47bf-894f-540b7c63e57f",
      "name": "Moved projects",
      "match": { "path": "/old-projects" },
      "target": "/projects"
    }
  ]
}
```

Save, retry and test responses use `NhProxyApiResult<T>` with `success`, `data`
and safe `issues` containing codes/localization keys. A successful save returns
`NhProxySaveResult` in `data`; inspect its activation state. Validation errors
return `400`; stale revisions return `409`. Reload and reconcile after a conflict.
After a committed save with failed/unconfirmed activation, `503` retains the
saved revision and activation status in `data`: retry activation, not the old PUT.
Infrastructure failures return a safe `503` without diagnostic details.

Invalid input returns `400` with `success: false` and an `issues` array. Each
input issue includes a readable English `message`; JSON errors also identify
`field` using a JSON path. For example, a transform without `kind` returns:

```json
{
  "success": false,
  "data": null,
  "issues": [{
    "code": "newheap-proxy.validation",
    "localizationKey": "newheap-proxy.invalid-json",
    "field": "$.rules[0].transforms[0]",
    "message": "A transform must specify 'kind': 'path', 'query', 'header', 'original-host' or 'forwarded-headers'."
  }]
}
```

Malformed JSON, missing required fields, unknown members and invalid enum values
also return input descriptions. Invalid saves and draft tests do not change
configuration, active revisions or the change audit. Infrastructure exceptions
remain `503`; their messages and stack traces are not returned to the caller.

Authentication failures return `401` with a Basic challenge, IP/HTTPS/origin
restrictions return `403`, throttling returns `429`, oversized bodies return
`413`, and unsupported body content types return `415`. Protocol/security errors
may have no response body. Responses are marked `no-store`.
Previews never contact a backend, persist changes, activate rules or return an
actual redirect response. External YARP sources remain outside their scope.

### Durable change audit

Successful API saves write a `ConfigurationSaved` event to SQLite **in the same
transaction as the configuration**. Each entry contains the UTC timestamp,
authenticated account, effective client IP, correlation ID, engine, previous/new
revisions and normalized configuration snapshots before and after the save. Account
credentials, authentication headers and raw HTTP requests are excluded. If the audit
insert fails, the save rolls back and the active configuration remains unchanged.
A committed save remains in the audit even when subsequent activation fails.

Reads, previews, authentication checks, validation failures and revision conflicts
create no change events. Activation retry writes an `ActivationRequested` event
before running; this records intent, not successful activation, and has no claimed
revision or before/after snapshots. Failure to record the request prevents retry.
Inspect the retry response and `/status` for activation outcome.

Query `/audit?engine=Redirect&offset=0&pageSize=50`. Optional `fromUtc` and `toUtc`
are inclusive timestamp filters. The default page size is 50, maximum 100; results
are newest first and include the filtered total count. Records survive restarts
and have no automatic expiration or API edit/delete endpoint. They are separate
from the panel's login audit, whose existing retention still applies.

Startup upgrades the SQLite store to schema 4 without rewriting existing rules or
login events. Existing history cannot be reconstructed; change auditing begins with
new API saves. Back up before upgrading: older binaries cannot open schema 4.

Existing custom `INhProxyAdministrationService` implementations keep working for
the panel. Their default `AuthenticateAsync` denies API access; implement cookie-free
credential validation without login auditing explicitly to opt in. Custom configuration
stores must honor the server-owned save-request `Audit` context and persist the event
atomically with the snapshot. API clients cannot supply this context.

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

## Measure forwarding overhead

Run `npm run benchmark:proxy` from the repository root to measure full-request
time (ms) with BenchmarkDotNet and a standard YARP app as its baseline.
Use `npm run benchmark:proxy:load` for the separate requests/sec load test.
See the [proxy benchmark](../../src/Back-end/Benchmarks/NewHeap.Platform.AspNet.Proxy.Benchmarks/README.md)
for the workload, baseline ratios, reports and reproducible checks.
