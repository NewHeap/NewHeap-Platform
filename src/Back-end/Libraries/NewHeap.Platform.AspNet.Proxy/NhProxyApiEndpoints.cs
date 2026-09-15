using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AspNet.Proxy;

/// <summary>Safe workflow response. Failed activation retains the committed save in Data.</summary>
public sealed record NhProxyApiResult<T>(bool Success, T? Data, ImmutableArray<NhProxyIssue> Issues);

public sealed record NhProxySavedTestRequest(NhProxyRevisions ExpectedRevisions, NhProxyTestRequest Request);

/// <summary>Opt-in server-to-server management API using the configured administrator's Basic credentials.</summary>
public static class NhProxyApiEndpoints
{
    private static readonly JsonSerializerOptions RequestJsonOptions = CreateRequestJsonOptions();

    private static JsonSerializerOptions CreateRequestJsonOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(typeInfo =>
        {
            if (typeInfo.Type == typeof(NhProxyTransform))
            {
                // Without a discriminator, STJ otherwise tries to construct the abstract base and throws NotSupportedException.
                typeInfo.CreateObject = () => throw new JsonException(
                    "A transform must specify 'kind': 'path', 'query', 'header', 'original-host' or 'forwarded-headers'.");
            }
        });

        return new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = resolver,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            Converters = { new JsonStringEnumConverter(allowIntegerValues: true) }
        };
    }

    /// <summary>
    /// Maps /newheap-proxy/api. Call before UseNewHeapProxy, after trusted forwarded headers and PathBase.
    /// Requires HTTPS outside Development, Basic credentials and the administration IP allowlist.
    /// API authentication does not create logins. Configuration saves are audited atomically; no cookies are issued.
    /// </summary>
    public static WebApplication MapProxyEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.Services.GetRequiredService<IOptions<NhProxyOptions>>().Value.Administrator.UserName.Contains(':'))
        {
            throw new InvalidOperationException("Basic authentication requires an administrator username without a colon.");
        }

        var properties = ((IApplicationBuilder)app).Properties;
        if (properties.ContainsKey(typeof(NhProxyApplicationExtensions).FullName!))
        {
            throw new InvalidOperationException("Call MapProxyEndpoints before UseNewHeapProxy so proxy rules cannot intercept the API.");
        }

        if (!properties.TryAdd(typeof(NhProxyApiEndpoints).FullName!, true))
        {
            throw new InvalidOperationException("MapProxyEndpoints may only be called once.");
        }

        ((IApplicationBuilder)app).Map(NhProxyOptions.ApiPath, preserveMatchedPathSegment: true, api =>
        {
            api.Use(async (context, next) =>
            {
                context.SetEndpoint(null);
                context.Request.RouteValues = new RouteValueDictionary();
                context.Items[typeof(NhProxyBasicAuthenticationHandler)] = true;
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                // Basic credentials can be cached by browsers. This API deliberately accepts server clients only.
                if ((!context.Request.IsHttps && !app.Environment.IsDevelopment()) || context.Request.Headers.ContainsKey("Origin"))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

                var limit = context.RequestServices.GetRequiredService<IOptions<NhProxyOptions>>().Value.Limits.MaximumTestRequestBytes;
                if (context.Request.ContentLength > limit)
                {
                    context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    return;
                }

                if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize)
                {
                    bodySize.MaxRequestBodySize = limit;
                }

                try
                {
                    await next(context);
                }
                catch (BadHttpRequestException exception) when (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = exception.StatusCode;
                    if (exception.StatusCode == StatusCodes.Status400BadRequest)
                    {
                        var jsonError = exception.InnerException as JsonException;
                        var issue = new NhProxyIssue(NhProxyErrorCodes.Validation, "newheap-proxy.invalid-json", jsonError?.Path ?? "$")
                        {
                            Message = jsonError?.Message ?? "The request body is missing or invalid. Supply a JSON object matching the endpoint contract."
                        };
                        await context.Response.WriteAsJsonAsync(new NhProxyApiResult<object>(false, null, [issue]),
                            JsonSerializerOptions.Web, context.RequestAborted);
                    }
                }
                catch (Exception exception) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
                {
                    context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(NhProxyApiEndpoints))
                        .LogError(exception, "Proxy API request failed.");
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                }
            });
            api.UseRouting();
            api.UseAuthentication();
            api.UseAuthorization();
            api.UseEndpoints(endpoints =>
            {
                var group = endpoints.MapGroup(NhProxyOptions.ApiPath).WithTags("NewHeap Proxy")
                    .RequireAuthorization(new AuthorizationPolicyBuilder(NhProxyOptions.ApiAuthenticationScheme).RequireAuthenticatedUser().Build());
                group.WithMetadata(new ProducesResponseTypeMetadata(StatusCodes.Status401Unauthorized, typeof(void)),
                    new ProducesResponseTypeMetadata(StatusCodes.Status403Forbidden, typeof(void)),
                    new ProducesResponseTypeMetadata(StatusCodes.Status429TooManyRequests, typeof(void)),
                    new ProducesResponseTypeMetadata(StatusCodes.Status503ServiceUnavailable, typeof(void)));

                group.MapGet("/redirects", async ([FromServices] INhProxyConfigurationService configuration, CancellationToken token) =>
                    await configuration.GetRedirectsAsync(token))
                    .WithSummary("Read managed redirects")
                    .WithDescription("Returns the complete saved redirect configuration and its revision. Requires Basic authentication.");
                group.MapGet("/rewrites", async ([FromServices] INhProxyConfigurationService configuration, CancellationToken token) =>
                    await configuration.GetRewritesAsync(token))
                    .WithSummary("Read managed rewrites")
                    .WithDescription("Returns saved rewrite rules, shared single-destination clusters and their revision. Requires Basic authentication.");
                group.MapGet("/status", ([FromServices] INhProxyConfigurationService configuration) => configuration.GetStatus())
                    .WithSummary("Read activation status")
                    .WithDescription("Returns independent desired and active revisions for redirects and rewrites. Requires Basic authentication.");
                group.MapGet("/audit", async ([FromServices] INhProxyChangeAuditStore audit,
                    [FromQuery] NhProxyEngine? engine, [FromQuery] DateTimeOffset? fromUtc, [FromQuery] DateTimeOffset? toUtc,
                    [FromQuery] int? offset, [FromQuery] int? pageSize, CancellationToken token) =>
                    Result(await audit.QueryAsync(new NhProxyChangeAuditQuery
                    {
                        Engine = engine, FromUtc = fromUtc, ToUtc = toUtc, Offset = offset ?? 0, PageSize = pageSize ?? 50
                    }, token)))
                    .WithSummary("Read the API change audit")
                    .WithDescription("Returns durable configuration changes and activation requests, newest first. Filters by engine and inclusive UTC timestamps; pageSize is 1–100. Saved events contain before/after configuration and revisions, never credentials. Requires Basic authentication.")
                    .Produces<NhProxyApiResult<NhProxyChangeAuditPage>>()
                    .Produces<NhProxyApiResult<NhProxyChangeAuditPage>>(StatusCodes.Status400BadRequest);

                Document<NhProxyRedirectSaveRequest, NhProxySaveResult>(group.MapPut("/redirects", async (HttpContext context,
                    [FromServices] INhProxyConfigurationService configuration) =>
                {
                    var request = await ReadAsync<NhProxyRedirectSaveRequest>(context);
                    return Result(await configuration.SaveRedirectsAsync(request with { Audit = Actor(context) }, context.RequestAborted));
                }),
                    "Replace managed redirects", "Atomically replaces all redirect rules at ExpectedRevision, then activates them. Add, edit, toggle or remove rules in the submitted snapshot; omitted rules are deleted.");
                Document<NhProxyRewriteSaveRequest, NhProxySaveResult>(group.MapPut("/rewrites", async (HttpContext context,
                    [FromServices] INhProxyConfigurationService configuration) =>
                {
                    var request = await ReadAsync<NhProxyRewriteSaveRequest>(context);
                    return Result(await configuration.SaveRewritesAsync(request with { Audit = Actor(context) }, context.RequestAborted));
                }),
                    "Replace managed rewrites", "Atomically replaces all rewrite rules and clusters at ExpectedRevision, then activates them independently of redirects. Omitted rules and clusters are deleted.");
                Document<NhProxySavedTestRequest, NhProxyRuleTestResult>(group.MapPost("/test", async (HttpContext context,
                    [FromServices] INhProxyDraftTester tester) =>
                {
                    var request = await ReadAsync<NhProxySavedTestRequest>(context);
                    return Result(await tester.TestSavedAsync(request.ExpectedRevisions, request.Request, context.RequestAborted));
                }), "Test saved rules", "Evaluates a synthetic request at the supplied revisions without outbound requests, writes or live publication.");
                Document<NhProxyRedirectTestRequest, NhProxyRuleTestResult>(group.MapPost("/redirects/test", async (HttpContext context,
                    [FromServices] INhProxyDraftTester tester) =>
                    Result(await tester.TestRedirectAsync(await ReadAsync<NhProxyRedirectTestRequest>(context), context.RequestAborted))),
                    "Test a draft redirect", "Evaluates an unsaved redirect against the saved rules, without saving, forwarding or returning an HTTP redirect.");
                Document<NhProxyRewriteTestRequest, NhProxyRuleTestResult>(group.MapPost("/rewrites/test", async (HttpContext context,
                    [FromServices] INhProxyDraftTester tester) =>
                    Result(await tester.TestRewriteAsync(await ReadAsync<NhProxyRewriteTestRequest>(context), context.RequestAborted))),
                    "Test a draft rewrite", "Evaluates an unsaved rewrite and optional candidate clusters without contacting destinations or activating changes.");

                foreach (var engine in Enum.GetValues<NhProxyEngine>())
                {
                    group.MapPost(engine == NhProxyEngine.Redirect ? "/redirects/activate" : "/rewrites/activate",
                        async (HttpContext context, [FromServices] INhProxyConfigurationService configuration,
                            [FromServices] INhProxyChangeAuditStore audit) =>
                        {
                            await audit.RecordActivationRequestAsync(engine, Actor(context), context.RequestAborted);
                            return Result(await configuration.RetryActivationAsync(engine, CancellationToken.None));
                        })
                        .WithSummary($"Retry {engine.ToString().ToLowerInvariant()} activation")
                        .WithDescription("Records activation intent durably before retrying this engine's latest persisted revision. Does not increment the revision or publish the other engine. The audit entry records the request, not successful activation. Requires Basic authentication.")
                        .Produces<NhProxyApiResult<NhProxyEngineStatus>>()
                        .Produces<NhProxyApiResult<NhProxyEngineStatus>>(StatusCodes.Status400BadRequest)
                        .Produces<NhProxyApiResult<NhProxyEngineStatus>>(StatusCodes.Status409Conflict)
                        .Produces<NhProxyApiResult<NhProxyEngineStatus>>(StatusCodes.Status503ServiceUnavailable);
                }
            });
            api.Run(context =>
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            });
        });

        return app;
    }

    private static void Document<TRequest, TResponse>(RouteHandlerBuilder endpoint, string summary, string description) where TRequest : notnull
    {
        endpoint.WithSummary(summary).WithDescription(description + " Requires Basic authentication. Invalid input returns 400 with issue descriptions and a JSON field path when available. Failed activation retains the committed saved revision in Data.")
            .Accepts<TRequest>("application/json").Produces<NhProxyApiResult<TResponse>>()
            .Produces<NhProxyApiResult<TResponse>>(StatusCodes.Status400BadRequest)
            .Produces<NhProxyApiResult<TResponse>>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status413PayloadTooLarge).Produces(StatusCodes.Status415UnsupportedMediaType)
            .Produces<NhProxyApiResult<TResponse>>(StatusCodes.Status503ServiceUnavailable);
    }

    private static NhProxyChangeAuditContext Actor(HttpContext context)
    {
        return new NhProxyChangeAuditContext(context.User.Identity?.Name ?? throw new InvalidOperationException("Missing API identity."),
            context.Connection.RemoteIpAddress is { } ip ? NhProxyAdministrationService.Normalize(ip).ToString() : null,
            context.TraceIdentifier);
    }

    private static async Task<T> ReadAsync<T>(HttpContext context)
    {
        if (!context.Request.HasJsonContentType())
        {
            throw new BadHttpRequestException("Expected JSON.", StatusCodes.Status415UnsupportedMediaType);
        }

        try
        {
            return await context.Request.ReadFromJsonAsync<T>(RequestJsonOptions, context.RequestAborted)
                ?? throw new BadHttpRequestException("A request is required.");
        }
        catch (JsonException exception)
        {
            throw new BadHttpRequestException("The request JSON is invalid.", StatusCodes.Status400BadRequest, exception);
        }
    }

    private static IResult Result<T>(TaskResult<T> result)
    {
        var issues = result.GetResultItems().SelectMany(item => item.ErrorMessages.Select(message =>
            new NhProxyIssue(item.Name, message.Format)
            {
                Message = message.Format switch
                {
                    "newheap-proxy.invalid-redirect-snapshot" => "ExpectedRevision must be non-negative and rules must be an array within MaximumRulesPerEngine.",
                    "newheap-proxy.invalid-literal-redirect" => "Each redirect needs a unique non-empty id, a name, a valid match and target, and supported status and queryMode values. Literal paths must start with '/' and targets must be local paths or HTTP(S) URLs.",
                    "newheap-proxy.invalid-redirect-host" => "Redirect hosts must be valid hostnames with an optional port, without paths, wildcards, credentials or whitespace.",
                    "newheap-proxy.invalid-redirect-method" => "Redirect methods must be non-empty HTTP method tokens without whitespace or control characters.",
                    "newheap-proxy.self-redirect" => "An enabled redirect cannot target its own matching path.",
                    "newheap-proxy.invalid-rewrite-snapshot" => "Rewrites require unique non-empty rule and cluster ids, names, valid matches and transforms, and references to existing clusters. Destinations must be allowed HTTP(S) URLs and timeouts must be positive.",
                    "newheap-proxy.invalid-rewrite-route-pattern" => "The rewrite match.path must be a valid route template with supported parameter constraints. Check balanced braces and the syntax of regex constraints.",
                    "newheap-proxy.invalid-rewrite-transform-pattern" => "A path transform with operation Pattern must have a valid route template in value. Check balanced braces and parameter syntax.",
                    _ => null
                }
            })).ToImmutableArray();
        var status = result.Success ? StatusCodes.Status200OK
            : issues.Any(issue => issue.Code == NhProxyErrorCodes.RevisionConflict) ? StatusCodes.Status409Conflict
            : issues.Any(issue => issue.Code == NhProxyErrorCodes.ActivationFailed) ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status400BadRequest;
        return Results.Json(new NhProxyApiResult<T>(result.Success, result.Data, issues), JsonSerializerOptions.Web, statusCode: status);
    }
}
