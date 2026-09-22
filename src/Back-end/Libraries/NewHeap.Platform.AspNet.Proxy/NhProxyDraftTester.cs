using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using NewHeap.Platform.Common.Models;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

namespace NewHeap.Platform.AspNet.Proxy;

/// <summary>Tests persisted-rule candidates with native routing and transforms, without starting a server or contacting destinations.</summary>
public sealed partial class NhProxyDraftTester(INhProxyConfigurationService configuration, INhProxyConfigurationValidator validator,
    IOptions<NhProxyOptions> options) : INhProxyDraftTester
{
    internal static async Task<TaskResult<NhProxyRedirectPreview?>> TestIsolatedRedirectAsync(NhProxyRedirectRule rule,
        NhProxyTestRequest input, INhProxyConfigurationValidator validator, IOptions<NhProxyOptions> options,
        CancellationToken cancellationToken)
    {
        // The redirect editor deliberately tests only its draft, independently of stored rules and revisions.
        var runtime = new NhProxyRuntime(validator, options);
        var published = await runtime.PublishRedirectsAsync(new NhProxyRedirectConfiguration { Rules = [rule] }, cancellationToken);
        if (!published.Success)
        {
            return TaskResult<NhProxyRedirectPreview?>.Failed(published);
        }

        var context = CreateContext(input, cancellationToken);
        var chainFailure = await runtime.CheckChainAsync(context);
        if (chainFailure is not null)
        {
            return TaskResult<NhProxyRedirectPreview?>.Failed(
                chainFailure == NhProxyRuntime.ChainDepthFailure ? NhProxyErrorCodes.MaximumChainDepth : NhProxyErrorCodes.Validation,
                chainFailure);
        }

        if (runtime.TryRedirect(context, out var failure))
        {
            return TaskResult<NhProxyRedirectPreview?>.Succeeded(new(
                (NhProxyRedirectStatus)context.Response.StatusCode, context.Response.Headers.Location.ToString(),
                context.Response.StatusCode is 307 or 308));
        }

        return failure is null ? TaskResult<NhProxyRedirectPreview?>.Succeeded(null)
            : TaskResult<NhProxyRedirectPreview?>.Failed(NhProxyErrorCodes.Validation, failure);
    }

    public Task<TaskResult<NhProxyRuleTestResult>> TestSavedAsync(NhProxyRevisions expectedRevisions, NhProxyTestRequest request, CancellationToken cancellationToken = default)
    {
        return TestAsync(expectedRevisions, request, null, null, cancellationToken);
    }

    public Task<TaskResult<NhProxyRuleTestResult>> TestRewriteAsync(NhProxyRewriteTestRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return TestAsync(request.ExpectedRevisions, request.Request, request, null, cancellationToken);
    }

    public Task<TaskResult<NhProxyRuleTestResult>> TestRedirectAsync(NhProxyRedirectTestRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return TestAsync(request.ExpectedRevisions, request.Request, null, request, cancellationToken);
    }

    private async Task<TaskResult<NhProxyRuleTestResult>> TestAsync(NhProxyRevisions expected, NhProxyTestRequest input,
        NhProxyRewriteTestRequest? rewrite, NhProxyRedirectTestRequest? redirect, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        if (expected is null || input is null || !IsValidInput(input)
            || (rewrite is not null && rewrite.Draft is null) || (redirect is not null && redirect.Draft is null))
        {
            return TaskResult<NhProxyRuleTestResult>.Failed(NhProxyErrorCodes.Validation, "newheap-proxy.invalid-test-request");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Value.Limits.TestTimeout);
        try
        {
            var rewrites = await configuration.GetRewritesAsync(timeout.Token);
            var redirects = await configuration.GetRedirectsAsync(timeout.Token);
            if (expected != new NhProxyRevisions(rewrites.Revision, redirects.Revision))
            {
                return TaskResult<NhProxyRuleTestResult>.Failed(NhProxyErrorCodes.RevisionConflict, NhProxyErrorCodes.RevisionConflict);
            }

            if (rewrite is not null)
            {
                var draft = rewrite.SimulateEnabled ? rewrite.Draft with { Enabled = true } : rewrite.Draft;
                rewrites = rewrites with
                {
                    Rules = rewrites.Rules.Where(rule => rule.Id != draft.Id).Append(draft).ToImmutableArray(),
                    Clusters = rewrite.DraftClusters ?? rewrites.Clusters
                };
            }

            if (redirect is not null)
            {
                var draft = redirect.SimulateEnabled ? redirect.Draft with { Enabled = true } : redirect.Draft;
                redirects = redirects with { Rules = redirects.Rules.Where(rule => rule.Id != draft.Id).Append(draft).ToImmutableArray() };
            }

            var validRewrites = await validator.ValidateRewritesAsync(new(rewrites.Revision, rewrites.Rules, rewrites.Clusters), timeout.Token);
            if (!validRewrites.Success)
            {
                return TaskResult<NhProxyRuleTestResult>.Failed(validRewrites);
            }

            if (JsonSerializer.SerializeToUtf8Bytes((object?)rewrite ?? (object?)redirect ?? input).Length > options.Value.Limits.MaximumTestRequestBytes)
            {
                return TaskResult<NhProxyRuleTestResult>.Failed(NhProxyErrorCodes.Validation, "newheap-proxy.test-request-too-large");
            }

            var redirectRuntime = new NhProxyRuntime(validator, options);
            var validRedirects = await redirectRuntime.PublishRedirectsAsync(redirects, timeout.Token);
            if (!validRedirects.Success)
            {
                return TaskResult<NhProxyRuleTestResult>.Failed(validRedirects);
            }

            var context = CreateContext(input, timeout.Token);
            var result = new NhProxyRuleTestResult
            {
                TestedRevisions = expected, LiveStatus = configuration.GetStatus(), Outcome = NhProxyTestOutcome.NoMatch,
                SimulatedEnabled = rewrite?.SimulateEnabled == true || redirect?.SimulateEnabled == true,
                Warnings = [new("newheap-proxy.preview-scope", "newheap-proxy.preview-scope")]
            };
            if (context.Request.Path.StartsWithSegments(NhProxyOptions.AdministrationPath, StringComparison.OrdinalIgnoreCase))
            {
                result = result with { Outcome = NhProxyTestOutcome.ReservedAdministrationPath };
                return TaskResult<NhProxyRuleTestResult>.Succeeded(result with
                {
                    Matches = await DiagnoseAsync(rewrites, redirects, input, result, [], timeout.Token)
                });
            }

            await using var previewApp = CreatePreviewApplication(rewrites);
            var constraintResolver = previewApp.Services.GetRequiredService<IInlineConstraintResolver>();
            if (rewrites.Rules.Any(rule => rule.Enabled && !NhProxyConfigurationValidator.HasSupportedConstraints(rule.Match.Path, constraintResolver)))
            {
                return TaskResult<NhProxyRuleTestResult>.Failed(NhProxyErrorCodes.Validation, "newheap-proxy.unsupported-preview-constraint");
            }

            context.RequestServices = previewApp.Services;
            redirectRuntime.ConfigureChainRouting(previewApp);
            var chainFailure = await redirectRuntime.CheckChainAsync(context);
            if (chainFailure is not null)
            {
                return TaskResult<NhProxyRuleTestResult>.Failed(chainFailure == NhProxyRuntime.ChainDepthFailure
                    ? NhProxyRuntime.ChainDepthFailure : NhProxyErrorCodes.Validation, chainFailure);
            }

            var redirectDiagnostics = new List<NhProxyRuleMatchDiagnostic>();
            if (redirectRuntime.TryRedirect(context, out var failure, out var redirectId, redirectDiagnostics))
            {
                result = result with
                {
                    Outcome = NhProxyTestOutcome.Redirect, SelectedRuleId = redirectId,
                    Redirect = new((NhProxyRedirectStatus)context.Response.StatusCode, context.Response.Headers.Location.ToString(),
                        context.Response.StatusCode is 307 or 308)
                };
            }
            else if (failure is not null)
            {
                return TaskResult<NhProxyRuleTestResult>.Failed(NhProxyErrorCodes.Validation, "newheap-proxy.redirect-test-failed");
            }
            else
            {
                var evaluated = await EvaluateRewriteAsync(previewApp, rewrites, context, result);
                if (!evaluated.Success)
                {
                    return evaluated;
                }

                result = evaluated.Data!;
            }

            timeout.Token.ThrowIfCancellationRequested();
            result = result with
            {
                Matches = await DiagnoseAsync(rewrites, redirects, input, result, redirectDiagnostics, timeout.Token)
            };
            if (Stopwatch.GetElapsedTime(started) > options.Value.Limits.TestTimeout)
            {
                return TaskResult<NhProxyRuleTestResult>.Failed(NhProxyErrorCodes.Validation, "newheap-proxy.test-timeout");
            }

            return TaskResult<NhProxyRuleTestResult>.Succeeded(result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return TaskResult<NhProxyRuleTestResult>.Failed(NhProxyErrorCodes.Validation, "newheap-proxy.test-timeout");
        }
        catch (Exception exception) when (exception.GetType().FullName == "Microsoft.AspNetCore.Routing.Matching.AmbiguousMatchException")
        {
            return TaskResult<NhProxyRuleTestResult>.Failed(NhProxyErrorCodes.Validation, "newheap-proxy.ambiguous-rewrite");
        }
    }

    private bool IsValidInput(NhProxyTestRequest input)
    {
        return input.Url is { IsAbsoluteUri: true } url && url.Scheme is "http" or "https"
            && url.UserInfo.Length == 0 && url.Fragment.Length == 0 && NhProxyConfigurationValidator.IsToken(input.Method)
            && input.Headers is not null && input.Headers.All(header => NhProxyConfigurationValidator.IsSafeHeader(header.Key)
                && !header.Value.IsDefault && header.Value.All(value => value is not null && !value.Any(char.IsControl)))
            && JsonSerializer.SerializeToUtf8Bytes(input).Length <= options.Value.Limits.MaximumTestRequestBytes;
    }

    private static DefaultHttpContext CreateContext(NhProxyTestRequest input, CancellationToken cancellationToken)
    {
        var context = new DefaultHttpContext { RequestAborted = cancellationToken };
        context.Request.Scheme = input.Url.Scheme;
        context.Request.Host = HostString.FromUriComponent(input.Url);
        context.Request.Path = PathString.FromUriComponent(input.Url);
        context.Request.QueryString = new QueryString(input.Url.Query);
        context.Request.Method = input.Method;
        foreach (var header in input.Headers)
        {
            context.Request.Headers.Append(header.Key, new StringValues(header.Value.ToArray()));
        }

        return context;
    }

    private static WebApplication CreatePreviewApplication(NhProxyRewriteConfiguration configuration, EndpointSelector? selector = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [], ApplicationName = typeof(NhProxyDraftTester).Assembly.GetName().Name
        });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        if (selector is not null)
        {
            builder.Services.AddSingleton(selector);
        }
        builder.Services.AddReverseProxy().LoadFromMemory(configuration.Rules.Where(rule => rule.Enabled)
            .Select(NhProxyYarpConfiguration.Route).Select(route => route with
            {
                AuthorizationPolicy = null, RateLimiterPolicy = null,
                CorsPolicy = string.IsNullOrEmpty(route.CorsPolicy) ? null : "Default"
            }).ToArray(), configuration.Clusters.Select(NhProxyYarpConfiguration.Cluster)
            .Select(cluster => cluster with { HealthCheck = null }).ToArray());
        var app = builder.Build();
        app.MapReverseProxy();
        return app;
    }

    private static async Task<TaskResult<NhProxyRuleTestResult>> EvaluateRewriteAsync(WebApplication app, NhProxyRewriteConfiguration configuration,
        HttpContext context, NhProxyRuleTestResult result)
    {
        app.UseRouting();
        app.Run(async request =>
        {
            var route = request.GetEndpoint()?.Metadata.GetMetadata<RouteModel>();
            if (route is null)
            {
                return;
            }

            var rule = configuration.Rules.Single(item => NhProxyYarpConfiguration.Key(item.Id) == route.Config.RouteId);
            var cluster = configuration.Clusters.Single(item => item.Id == rule.ClusterId);
            using var message = new HttpRequestMessage { Method = new HttpMethod(request.Request.Method) };
            await route.Transformer.TransformRequestAsync(request, message, cluster.Destination.Address.AbsoluteUri, request.RequestAborted);
            var target = message.RequestUri ?? RequestUtilities.MakeDestinationAddress(cluster.Destination.Address.AbsoluteUri,
                request.Request.Path, request.Request.QueryString);
            result = result with
            {
                Outcome = NhProxyTestOutcome.Rewrite, SelectedRuleId = rule.Id,
                RouteValues = request.Request.RouteValues.ToImmutableDictionary(pair => pair.Key,
                    pair => Convert.ToString(pair.Value, CultureInfo.InvariantCulture) ?? ""),
                Rewrite = new(rule.ClusterId, cluster.Destination.Address, target,
                    NhProxyRuntime.GetSafeHeaders(message)
                        .ToImmutableDictionary(header => header.Key, header => header.Value.ToImmutableArray()),
                    rule.Transforms.OfType<NhProxyHeaderTransform>().Where(header => header.Direction == NhProxyHeaderDirection.Response).ToImmutableArray())
            };
        });
        context.RequestServices = app.Services;
        await ((IApplicationBuilder)app).Build()(context);
        return TaskResult<NhProxyRuleTestResult>.Succeeded(result);
    }
}
