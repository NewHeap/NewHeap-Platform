# NewHeap ASP.NET Proxy

Manage redirects and reverse-proxy rules in ASP.NET Core with a built-in
administration panel, SQLite storage and YARP forwarding.

[Getting started](#installation) · [Configuration](#configuration) · [Usage reference](../../../../docs/how-to/use-newheap-proxy.md) · [Runnable demo](../../../../examples/SampleProjectManagement/docs/proxy-administration.md)

## Overview

- Redirect moved pages using exact paths or regular expressions.
- Forward requests to backend services with path, header and query transforms.
- Create, test and activate rules from the administration panel.
- Protect administration with an account, IP restrictions and login activity.
- Keep rules across restarts without a separate database server.

## Installation

Add the package to your ASP.NET Core application (.NET 10):

```sh
dotnet add package NewHeap.Platform.AspNet.Proxy.Sqlite
```

Register the proxy in `Program.cs`:

```csharp
using NewHeap.Platform.AspNet.Proxy;
using NewHeap.Platform.AspNet.Proxy.Sqlite;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddNewHeapProxy(builder.Configuration.GetSection("NewHeapProxy"));

var app = builder.Build();
app.UseNewHeapProxy();
app.Run();
```

Set the administrator account through your deployment platform's secrets:

```text
NewHeapProxy__Administrator__UserName=administrator
NewHeapProxy__Administrator__Password=<your password>
```

Host the proxy at the origin root, for example `https://proxy.example/`, and open
`/newheap-proxy` to sign in. HTTPS is required outside Development.

The SQLite database is created automatically. Use persistent local storage and
run one proxy instance per database file. See [storage and backups](../NewHeap.Platform.AspNet.Proxy.Sqlite/README.md#storage-configuration).

## Usage

### Redirect a page

Create a redirect in the panel, then choose **Test draft** and **Save and activate**.

```text
Source:       /old-projects
Destination:  /projects
Status:       302
```

For variable paths, enable **Use regular expression**:

```text
Source:       ^/old-projects/([^?]+)(\?.*)?$
Destination:  /projects/$1$2

/old-projects/42?tag=new  →  /projects/42?tag=new
```

Regex rules preserve only the query values included in the destination.
See [redirect matching](../../../../docs/how-to/use-newheap-proxy.md#literal-redirects).

### Forward requests to a backend

Open **Rewrites**, choose **Create rewrite**, and configure:

```text
Path template:  /api/{**rest}
Backend URL:    https://backend.example/base/
Path transform: Remove prefix /api

/api/projects/42  →  https://backend.example/base/projects/42
```

Replace the example backend with your service URL. Requests are forwarded without
a browser redirect. See [rewrites and testing](../../../../docs/how-to/use-newheap-proxy.md#stored-rewrites-and-the-test-tool).

### Test before activating

Use **Test a URL** to preview which saved rule handles a request, or **Test draft**
to check unsaved changes. Tests show the matching rule and target without contacting
the backend. They do not verify backend availability or responses.

## Configuration

Options below belong under `NewHeapProxy` in `appsettings.json`.
For environment variables, use `__` instead of `:` and prefix with `NewHeapProxy__`:

```text
NewHeapProxy__Administrator__SessionDuration=04:00:00
NewHeapProxy__IpAllowlist__Enabled=true
NewHeapProxy__IpAllowlist__Entries__0=192.0.2.10
```

Restart after changing settings. Durations use `hh:mm:ss` or `d.hh:mm:ss`.
All durations, counts and size limits must be positive.

| Option | Default | Explanation |
| --- | --- | --- |
| `Administrator:UserName` | Empty | Administrator login name; required to sign in. |
| `Administrator:Password` | Empty | Password supplied through secrets. Use either this or `PasswordHash`. Requires a new login after every restart. |
| `Administrator:PasswordHash` | Empty | Alternative ASP.NET Identity password hash. A stable hash and persistent Data Protection keys allow sessions to survive restarts. |
| `Administrator:CredentialVersion` | Empty | Change to invalidate existing sessions after restarting. |
| `Administrator:SessionDuration` | `08:00:00` | Maximum administration session duration. |
| `Administrator:LoginAttemptLimit` | `5` | Login attempts allowed per window, shared across all clients. |
| `Administrator:LoginAttemptWindow` | `00:01:00` | Time window for the login attempt limit. |
| `IpAllowlist:Enabled` | `false` | Restrict administration to allowed IPs. An enabled, empty list blocks all access. |
| `IpAllowlist:Entries` | `[]` | Allowed IPv4/IPv6 addresses or CIDR ranges. |
| `LoginAudit:Retention` | `90.00:00:00` | Retain login activity for 90 days; expired records are cleaned up on login attempts. |
| `LoginAudit:CleanupBatchSize` | `1000` | Maximum expired login records removed per cleanup. |
| `LoginAudit:MaximumPageSize` | `100` | Maximum login activity records returned per page. |
| `AllowedDestinationHosts` | `[]` | Allowed rewrite backend hostnames, matched case-insensitively. Empty allows any host. Does not restrict redirects. |
| `Limits:MaximumChainDepth` | `2` | Maximum local redirect/rewrite steps before HTTP 508. |
| `Limits:RedirectResolutionTimeoutMilliseconds` | `50` | Redirect evaluation timeout; live requests return HTTP 503 on expiry. Range: 1–2,147,483,646 ms. |
| `Limits:MaximumRulesPerEngine` | `1000` | Maximum redirects, rewrites and rewrite destination groups, counted separately. |
| `Limits:MaximumTestRequestBytes` | `65536` | Maximum test input and draft-test request size in bytes (64 KiB). |
| `Limits:TestTimeout` | `00:00:05` | Preview time limit; maximum 2,147,483,647 ms. |
| `Sqlite:DatabasePath` | `App_Data/newheap-proxy.db` | Database path, relative to the application content root unless absolute. Use persistent local storage outside the webroot. |
| `Sqlite:BusyTimeout` | `00:00:05` | Database lock wait time, rounded up to seconds; maximum 2,147,483,647 seconds. |
| `ConfigureYarp(...)` (code only) | None | [Customize YARP](../../../../docs/how-to/use-newheap-proxy.md#native-yarp-customization) during registration. Repeated calls replace the callback. |

## Documentation

- [Usage reference](../../../../docs/how-to/use-newheap-proxy.md) — matching, authentication, testing and activation.
- [SQLite storage](../NewHeap.Platform.AspNet.Proxy.Sqlite/README.md) — database setup, backups and offline seeding.
- [Runnable demo](../../../../examples/SampleProjectManagement/docs/proxy-administration.md) — try redirects and rewrites locally.
