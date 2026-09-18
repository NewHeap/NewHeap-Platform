using System.Collections;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.AspNet;
using Xunit;

namespace NewHeap.Platform.AI.Tests;

public sealed class NhAiCallerCredentialTests
{
    private const string SavedToken = "saved-access-token-value";
    private const string HeaderToken = "header-bearer-token-value";

    [Fact]
    public async Task Default_accessor_returns_the_saved_access_token_of_the_authentication_result()
    {
        var httpContext = CreateHttpContext();
        httpContext.Request.Headers.Authorization = "Bearer " + HeaderToken;
        var services = CreateServices(httpContext);
        services.AddLogging();
        services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, SavedTokenHandler>("test", _ => { });
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        httpContext.RequestServices = scope.ServiceProvider;

        var token = await scope.ServiceProvider
            .GetRequiredService<INhAiCallerCredentialAccessor>()
            .GetBearerTokenAsync();

        Assert.Equal(SavedToken, token);
    }

    [Fact]
    public async Task Default_accessor_falls_back_to_the_bearer_authorization_header()
    {
        var httpContext = CreateHttpContext();
        httpContext.Request.Headers.Authorization = "Bearer " + HeaderToken;
        await using var provider = CreateServices(httpContext).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        httpContext.RequestServices = scope.ServiceProvider;

        var token = await scope.ServiceProvider
            .GetRequiredService<INhAiCallerCredentialAccessor>()
            .GetBearerTokenAsync();

        Assert.Equal(HeaderToken, token);
    }

    [Fact]
    public async Task Default_accessor_ignores_non_bearer_authorization()
    {
        var httpContext = CreateHttpContext();
        httpContext.Request.Headers.Authorization = "Basic dXNlcjpwYXNz";
        await using var provider = CreateServices(httpContext).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        httpContext.RequestServices = scope.ServiceProvider;

        var token = await scope.ServiceProvider
            .GetRequiredService<INhAiCallerCredentialAccessor>()
            .GetBearerTokenAsync();

        Assert.Null(token);
    }

    [Fact]
    public async Task Default_accessor_returns_null_without_a_request()
    {
        await using var provider = CreateServices(null).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var token = await scope.ServiceProvider
            .GetRequiredService<INhAiCallerCredentialAccessor>()
            .GetBearerTokenAsync();

        Assert.Null(token);
    }

    [Fact]
    public async Task Consumers_can_replace_the_default_accessor()
    {
        var services = new ServiceCollection();
        services.AddScoped<INhAiCallerCredentialAccessor, FixedCredentialAccessor>();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
        services.AddSingleton<IAuthorizationService>(new AllowAllAuthorizationService());
        services.AddNewHeapPlatformAIAspNet(_ => { });
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var accessor = scope.ServiceProvider.GetRequiredService<INhAiCallerCredentialAccessor>();

        Assert.IsType<FixedCredentialAccessor>(accessor);
    }

    [Fact]
    public async Task Bearer_token_never_enters_the_invocation_context()
    {
        var httpContext = CreateHttpContext();
        httpContext.Request.Headers.Authorization = "Bearer " + HeaderToken;
        await using var provider = CreateServices(httpContext).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        httpContext.RequestServices = scope.ServiceProvider;

        var token = await scope.ServiceProvider
            .GetRequiredService<INhAiCallerCredentialAccessor>()
            .GetBearerTokenAsync();
        var gate = await scope.ServiceProvider
            .GetRequiredService<INhAiToolInvocationGate>()
            .AuthorizeAsync(new NhAiToolDescriptor(
                "bridge.probe",
                1,
                "Probe the invocation context.",
                typeof(string),
                typeof(string),
                NhAiToolEffect.ReadOnly,
                NhAiToolExposure.Local,
                true,
                ["probe"]));

        Assert.Equal(HeaderToken, token);
        Assert.True(gate.Success);
        Assert.DoesNotContain(HeaderToken, DescribeContext(gate.Data), StringComparison.Ordinal);
    }

    private static string DescribeContext(NhAiInvocationContext context)
    {
        var values = new List<string>();
        foreach (var property in typeof(NhAiInvocationContext).GetProperties())
        {
            var value = property.GetValue(context);
            if (value is IDictionary<string, string> dictionary)
            {
                values.AddRange(dictionary.Select(pair => pair.Key + "=" + pair.Value));
            }
            else if (value is IEnumerable enumerable and not string)
            {
                values.AddRange(enumerable.Cast<object?>().Select(item => item?.ToString() ?? string.Empty));
            }
            else
            {
                values.Add(value?.ToString() ?? string.Empty);
            }
        }
        foreach (var pair in context.Scope)
        {
            values.Add(pair.Key + "=" + pair.Value);
        }
        return string.Join("\n", values);
    }

    private static ServiceCollection CreateServices(HttpContext? httpContext)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHttpContextAccessor>(
            new HttpContextAccessor { HttpContext = httpContext });
        services.AddSingleton<IAuthorizationService>(new AllowAllAuthorizationService());
        services.AddNewHeapPlatformAIAspNet(_ => { });
        return services;
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "actor-1")],
                "test"))
        };
    }

    private sealed class SavedTokenHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var properties = new AuthenticationProperties();
            properties.StoreTokens([new AuthenticationToken { Name = "access_token", Value = SavedToken }]);
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "actor-1")],
                Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, properties, Scheme.Name)));
        }
    }

    private sealed class FixedCredentialAccessor : INhAiCallerCredentialAccessor
    {
        public ValueTask<string?> GetBearerTokenAsync(CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<string?>("fixed");
        }
    }

    private sealed class AllowAllAuthorizationService : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user,
            object? resource,
            IEnumerable<IAuthorizationRequirement> requirements)
        {
            return Task.FromResult(AuthorizationResult.Success());
        }

        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user,
            object? resource,
            string policyName)
        {
            return Task.FromResult(AuthorizationResult.Success());
        }
    }
}
