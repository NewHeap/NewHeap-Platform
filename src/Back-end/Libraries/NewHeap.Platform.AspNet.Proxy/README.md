# NewHeap ASP.NET Proxy

Manage redirects and reverse-proxy rules in ASP.NET Core with a built-in
administration panel, SQLite storage and YARP forwarding.

[Getting started](#installation) · [Configuration](#configuration) · [Usage reference](../../../../docs/how-to/use-newheap-proxy.md) · [Runnable demo](../../../../examples/SampleProjectManagement/docs/proxy-administration.md)

## Overview

- Redirect moved pages using exact paths or regular expressions.
- Forward requests to backend services with path, header and query transforms.
- Create, test and activate rules from the administration panel.
- Protect administration with an account or host ASP.NET authentication, IP restrictions and login activity.
- Keep rules across restarts without a separate database server.
- Optionally manage rules from another server through Basic or host ASP.NET authentication.

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

The server-to-server management API is off by default. Call
`app.MapProxyEndpoints()` **before** `app.UseNewHeapProxy()` to enable
`/newheap-proxy/api`. It uses the configured administrator credentials with Basic
authentication and HTTPS, without cookies. See the
[API contracts and limits](../../../../docs/how-to/use-newheap-proxy.md#optional-management-api).

Configuration options belong under `NewHeapProxy` in `appsettings.json`.
For environment variables, use `__` instead of `:` and prefix with `NewHeapProxy__`:

```text
NewHeapProxy__Administrator__SessionDuration=04:00:00
NewHeapProxy__IpAllowlist__Enabled=true
NewHeapProxy__IpAllowlist__Entries__0=192.0.2.10
```

Restart after changing settings. Durations use `hh:mm:ss` or `d.hh:mm:ss`.
All durations, counts and size limits must be positive.

See the [configuration reference](../../../../docs/how-to/use-newheap-proxy.md#configuration-options)
for all options, defaults and limits.

### Customize authentication

The configured administrator account is the default. To use your application's
ASP.NET schemes and policies, follow
[custom UI and API authentication](../../../../docs/how-to/use-newheap-proxy.md#custom-authentication).

## Documentation

- [Usage reference](../../../../docs/how-to/use-newheap-proxy.md) — matching, authentication, testing and activation.
- [SQLite storage](../NewHeap.Platform.AspNet.Proxy.Sqlite/README.md) — database setup, backups and offline seeding.
- [Runnable demo](../../../../examples/SampleProjectManagement/docs/proxy-administration.md) — try redirects and rewrites locally.

## For maintainers

The [completion record](../../../../docs/plans/newheap-proxy-design.md#version-one-completion)
tracks implementation scope and future work. Use the
[release guide](../../../../docs/how-to/release-newheap-libraries.md) for publishing.
