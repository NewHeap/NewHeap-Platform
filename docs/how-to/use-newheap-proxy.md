# Proxy usage reference

For installation and the complete configuration table, see the
[proxy README](../../src/Back-end/Libraries/NewHeap.Platform.AspNet.Proxy/README.md).

- [Host integration](#host-integration)
- [Administration](#administration)
- [Literal redirects](#literal-redirects)
- [Regex redirects](#regex-redirects)
- [Persistence and activation](#persistence-and-activation)
- [Rewrites and testing](#stored-rewrites-and-the-test-tool)
- [YARP customization](#native-yarp-customization)
- [Local rule-chain limit](#local-rule-chain-limit)

## Host integration

Host the proxy at the origin root, for example `https://proxy.example/`.

`AddNewHeapProxy` configures the proxy and loads saved rules before accepting
requests. A missing database is created automatically. Unreadable, invalid or
incompatible storage prevents startup, protecting existing rules.

`UseNewHeapProxy` installs the administration branch and redirect middleware and calls `MapNewHeapProxy`
internally. Do not call both on the same `WebApplication`. Standalone
`MapNewHeapProxy(IEndpointRouteBuilder)` remains a lower-level YARP-only mapping
alternative; it does not install redirect middleware. Host-configured forwarded
headers and other required host middleware belong before `UseNewHeapProxy`.

Proxy rules cannot override the administration panel, even with catch-all routes.

## Administration

Open `/newheap-proxy`. The panel provides search, create/edit, enable/disable,
priority, host/method restrictions, query modes, deletion confirmation, login
activity, a **Use regular expression** checkbox and a local draft test.
The redirect test evaluates one unsaved rule using the same matching behavior as
live requests, without saving or contacting destinations. It does not simulate
the full ordered rule set. The separate rewrite editor tests a candidate against
the saved redirects and rewrites.

For live environments, inject the single administrator account through environment
variables supplied by the deployment platform's secret settings. This is the
recommended setup:

```text
NewHeapProxy__Administrator__UserName=administrator
NewHeapProxy__Administrator__Password=<password supplied by your deployment secret>
```

Bind the section using the standard ASP.NET Core configuration pipeline:

```csharp
builder.Services.AddNewHeapProxy(builder.Configuration.GetSection("NewHeapProxy"));
```

The default environment provider maps `__` to configuration sections and overrides
appsettings values. Configure either `Password` or `PasswordHash`; setting both
fails startup. Passwords must contain a non-whitespace character and
be at most 1024 characters; leading/trailing spaces are preserved.

Alternatively, inject a precomputed ASP.NET Identity `PasswordHasher<string>` hash
through `NewHeapProxy__Administrator__PasswordHash` and leave `Password` unset.
Do not commit credentials to appsettings or source control. Use user-secrets for
local development. Restart after changing credentials. With `Password`, each
restart requires administrators to sign in again. Use a stable `PasswordHash`
and persistent Data Protection keys if sessions should survive restarts.

No default account is created; missing credentials return 503 for the panel while
ordinary proxy traffic continues. HTTPS is required outside Development. The panel
uses a separate session from the rest of your application. Keep your application's
authentication scheme as its default; do not use the `NewHeapProxy` scheme for it.

Every administration request checks the optional IP allowlist. Enabled with no
entries denies access. Only trusted host forwarding middleware may alter the
client IP; configure it before UseNewHeapProxy. Login attempts are audited with
UTC timestamps and IP addresses, without attempted usernames or passwords. Audit
failure prevents login. Login attempts are limited to five per minute by default.

Changing credentials or `CredentialVersion` requires a new login after restarting
the host. Sessions expire after eight hours by default.

See the [runnable administration sample](../../examples/SampleProjectManagement/docs/proxy-administration.md)
for password-hash generation, host configuration and verification.

## Literal redirects

- `PathMode = Exact` is the default. Paths match exactly and case-sensitively.
  Trailing slashes matter.
  The query is not part of path matching.
- With the checkbox off, regex characters and target substitutions remain literal.
  Use regex capture groups to match prefixes or variable path segments.
- Optional hosts match case-insensitively. A host without a port matches any
  request port; a configured port must match. Host wildcards are rejected.
  Methods are exact, case-sensitive HTTP tokens; empty host/method lists mean all.
- Disabled rules never match. Lower priority wins; ties are ordered by rule ID.
  `/newheap-proxy` and its descendants bypass redirect processing.
- Status codes `301`, `302`, `303`, `307`, and `308` are supported; `302` is the default.
- Targets are literal paths starting with `/` or absolute HTTP(S) URLs. Control characters, backslashes,
  network-path targets, userinfo, and other schemes are rejected. Root-relative
  same-path redirects are rejected conservatively even when only the query changes.
- Preserve mode merges incoming and target query values.
  Target keys replace incoming keys case-insensitively; repeated values
  survive and query escaping is normalized. Replace keeps only target query
  values; Discard removes all query values. Fragments are retained.

Matching redirects are returned before rewrites or application endpoints run.
Unmatched requests continue to those routes. A local chain check refuses
requests exceeding `Limits.MaximumChainDepth` (default 2), including local loops
and absolute self-redirects. External origins and backend responses are not followed.

Redirect evaluation has a time limit of 50 ms by default. If evaluation or a regex
times out, the proxy returns HTTP 503 without redirecting or forwarding the request.

Configure `NewHeapProxy:Limits:RedirectResolutionTimeoutMilliseconds` as an integer number of milliseconds:

```json
{
  "NewHeapProxy": {
    "Limits": {
      "RedirectResolutionTimeoutMilliseconds": 50
    }
  }
}
```

Use `AddNewHeapProxy(builder.Configuration.GetSection("NewHeapProxy"))` to bind
the section, or set `options.Limits.RedirectResolutionTimeoutMilliseconds` in the existing
registration callback. The runtime captures the value at startup; restart after
changing it. The same budget applies to the administration draft tester. Values
below 1 ms or above 2,147,483,646 ms are rejected.

## Regex redirects

Enable **Use regular expression**, or set `Match.PathMode = NhProxyRedirectPathMatchMode.Regex`.
The pattern matches the URL-escaped path plus the incoming query string,
including `?`. Matching is case-sensitive unless
the pattern specifies otherwise; use `^` and `$` when the entire input must match.
The first match expands the complete destination using [.NET substitutions](https://learn.microsoft.com/en-us/dotnet/standard/base-types/substitutions-in-regular-expressions):
`$1`, `${name}` and `$$` for a literal dollar sign. Unmatched input is not appended.

For example, `^/old-projects/([^?]+)(\?.*)?$` with target `/projects/$1$2` maps
`/old-projects/42?tag=a%26b` to `/projects/42?tag=a%26b`. Omitting `$2` drops the
incoming query. **All QueryMode settings are ignored for regex rules**: there is
no automatic merge, preserve or discard step. Captures keep their URL escaping;
the final URL is normalized for the Location header without parsing/merging query values.

Invalid patterns or unsafe target templates fail validation. Absolute destination hosts and ports must be fixed;
substitutions belong in the path, query or fragment. Expanded destinations are
validated again, including network-path targets and root-relative self-redirects.
Regex inputs and expanded targets are limited to 65,536 characters, patterns to
2,048 and templates to 4,096. An input/output limit or unsafe expansion stops
redirect evaluation and passes through unless the resolver budget expired.
Timeouts return HTTP 503. The draft test explains the failure without returning
an actual redirect. Local regex self-redirects are refused by the chain-depth guard with HTTP 508.

Existing rules retain exact matching. Older proxy versions without regex support
cannot load regex rules. Host/method filters, ordering, status codes
and administration-path protection apply equally to both modes.

## Persistence and activation

See the [SQLite README](../../src/Back-end/Libraries/NewHeap.Platform.AspNet.Proxy.Sqlite/README.md) for storage
configuration, backups and an offline seeding example. `SaveRedirectsAsync` replaces
the complete redirect document using an expected revision. Validation and stale
writes return failed `TaskResult` values; always inspect `Success`.

Use `INhProxyConfigurationService.SaveRedirectsAsync` for live changes. It commits
first and publishes the new snapshot even if the request is cancelled after the
commit. A failed activation retains `NhProxySaveResult` in the failed TaskResult's
Data; the panel shows the saved/active revision difference and offers retry.
Conflicts do not overwrite another editor's work. Rewrites are saved and activated
separately through `SaveRewritesAsync`.

The lower-level store persists only, and the lower-level runtime publishes only.
Use them for offline setup or custom integrations. Use the configuration service
for routine live changes.

## Stored rewrites and the test tool

Open **Rewrites** in `/newheap-proxy`, create a rule, enter a path template and
HTTP(S) backend URL, and optionally choose a path transform. Destinations may be
shared; explicitly load an existing destination before editing it. Changes to a
shared destination affect every referencing rule. The advanced JSON sections
preserve ordered transforms, header/query conditions, policies, timeouts and
health checks. JSON shows the complete current draft, including the first path
transform, and stays synchronized with the form fields in both directions.
Invalid JSON is retained for correction. Unknown properties are rejected.

The backend URL's base path is prepended after the rule's path transforms.
For `/api/{**rest}`, destination `https://backend.example/base/`, and a remove-prefix
transform `/api`, request `/api/projects/42?source=test` becomes
`https://backend.example/base/projects/42?source=test`. There is no browser redirect.
Matching retains ASP.NET/YARP host, method, path constraint, header, query and
priority semantics. Exact duplicate enabled matches at the same priority are
rejected; broader ambiguity is reported by the tester. Avoid destinations that
route back into the same proxy rule; the local chain-depth guard refuses overlong
chains before forwarding, while cross-service/backend response cycles are outside its scope.

If saved rewrites cannot be activated, the previous rules remain active. The panel
shows the difference between saved and active revisions and offers an activation
retry. Retry activation without saving another revision.

**Test draft** evaluates your rewrite changes alongside all other stored rules.
Disabled rules stay disabled unless **Simulate enabled** is selected. Redirects
take precedence. Output includes the winning rule, route
values, transformed target URL, safe outgoing headers and unevaluated response
header transforms. The reserved administration path bypasses both engines.
The administration top bar accepts a complete HTTP(S) URL or a path such as
`/foo?s=1`. A path uses the proxy's current scheme, host and port.
Trusted forwarded-header middleware must run before `UseNewHeapProxy` when an
upstream proxy supplies the public origin.
The result displays the resolved request URL and previews a GET
against saved managed rules. It shows the winning redirect or rewrite, target URL
and an edit link, or an explicit no-match/reserved-path result. Saved-versus-active
revision differences are highlighted. It never contacts the destination, so an
upstream redirect or error is not part of this preview.

`INhProxyDraftTester.TestSavedAsync` takes both expected revisions and synthetic
input to test saved rules without supplying a draft. Disabled rules remain disabled.
`INhProxyDraftTester.TestRewriteAsync` and `TestRedirectAsync` expose the same
rule testing to code, without saving changes or contacting destinations.

Only synthetic non-secret headers are accepted. Preview does not copy credentials
or cookies from the administrator request. The default request input limit is
64 KiB and the test time limit is five seconds.
Preview does not prove connectivity, health, authorization, CORS, rate limiting
or upstream responses, and does not replay host callbacks, filters, custom
constraints/transforms or other configuration sources. Policy registration remains
host-owned. `UseNewHeapProxy` installs routing, authentication and authorization
after redirects. When using CORS, rate limiting or request timeouts, compose their
middleware after route selection and before endpoint execution as required by ASP.NET.

## Native YARP customization

For native customization, use the callback below. The managed chain limit is
configured separately through `Limits.MaximumChainDepth`.

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
Configure the callback in code. Repeated `ConfigureYarp` calls replace the previous
callback. Restart the host after changing proxy options.

## Local rule-chain limit

`UseNewHeapProxy` checks the active managed-rule chain before sending a redirect
or forwarding a request. Each selected redirect or rewrite counts as one step;
exactly two steps are allowed by default, while a third returns **HTTP 508 Loop
Detected** with `Cache-Control: no-store` and no `Location` or outbound request.
Even a legitimate longer chain is refused. Rules may still be saved; the check
uses the concrete incoming URL, method, headers and query at request time.

```json
{
  "NewHeapProxy": {
    "Limits": { "MaximumChainDepth": 2 }
  }
}
```

Set a positive integer through configuration binding or
`options.Limits.MaximumChainDepth`. The value is captured at startup. The tester
checks its saved/draft candidate with the same limit and returns the safe failure
code `NhProxyErrorCodes.MaximumChainDepth` when exceeded.

The check follows only rules managed by the panel on the same scheme, host and
port. It stops at administration paths, external destinations or other routes.
For redirect simulation, 303 switches to GET except for HEAD;
301/302 switch POST to GET, and 307/308 preserve the method. External aliases,
host endpoint behavior, custom runtime effects and backend responses are outside
this local check.
