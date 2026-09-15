using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NewHeap.Platform.AspNet.Proxy;

// Scoped scheme aliases keep host defaults and identities outside the selected scheme independent.
internal sealed class NhProxyHostAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, IOptions<NhProxyOptions> proxyOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder), IAuthenticationSignOutHandler
{
    internal const string AdministrationScheme = "NewHeapProxy.HostAdministration";
    internal const string ApiScheme = "NewHeapProxy.HostApi";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var proxy = proxyOptions.Value;
        var scheme = Scheme.Name == AdministrationScheme
            ? proxy.AdministrationAuthentication!.ChallengeScheme
            : proxy.ApiAuthentication!.AuthenticationScheme;
        if (Scheme.Name == AdministrationScheme)
        {
            // Never use a caller-supplied return URL for the external authentication round trip.
            properties.RedirectUri = Request.PathBase + "/";
        }

        await Context.ChallengeAsync(scheme, properties);
        if (Scheme.Name == ApiScheme && Response.StatusCode is >= 300 and < 400)
        {
            Response.Headers.Remove("Location");
            Response.StatusCode = StatusCodes.Status401Unauthorized;
        }
    }

    public Task SignOutAsync(AuthenticationProperties? properties) =>
        Context.SignOutAsync(proxyOptions.Value.AdministrationAuthentication!.SignOutScheme, properties);
}

internal static class NhProxyHostAuthentication
{
    internal static async Task InitializeAsync(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<NhProxyOptions>>().Value;
        var schemes = services.GetRequiredService<IAuthenticationSchemeProvider>();
        var policies = services.GetRequiredService<IAuthorizationPolicyProvider>();
        var authorization = services.GetRequiredService<IOptions<AuthorizationOptions>>().Value;

        async Task<AuthenticationScheme> RequireSchemeAsync(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name is NhProxyOptions.AuthenticationScheme or NhProxyOptions.ApiAuthenticationScheme
                or NhProxyHostAuthenticationHandler.AdministrationScheme or NhProxyHostAuthenticationHandler.ApiScheme)
            {
                throw new InvalidOperationException("Proxy host authentication requires an explicit, non-proxy authentication scheme.");
            }

            return await schemes.GetSchemeAsync(name)
                ?? throw new InvalidOperationException($"Proxy authentication scheme '{name}' is not registered.");
        }

        async Task ConfigurePolicyAsync(NhProxyApiAuthenticationOptions authentication, string policyName, string alias)
        {
            await RequireSchemeAsync(authentication.AuthenticationScheme);
            if (string.IsNullOrWhiteSpace(authentication.AuthorizationPolicy)
                || authentication.AuthorizationPolicy is NhProxyOptions.AdministrationPolicy or NhProxyOptions.ApiPolicy)
            {
                throw new InvalidOperationException("Proxy host authentication requires an explicit host authorization policy.");
            }

            var policy = await policies.GetPolicyAsync(authentication.AuthorizationPolicy)
                ?? throw new InvalidOperationException($"Proxy authorization policy '{authentication.AuthorizationPolicy}' is not registered.");
            if (policy.AuthenticationSchemes.Any(scheme => scheme != authentication.AuthenticationScheme))
            {
                throw new InvalidOperationException("A proxy host policy may only select its configured authentication scheme.");
            }

            authorization.AddPolicy(policyName, new AuthorizationPolicyBuilder(alias)
                .RequireAuthenticatedUser().AddRequirements(policy.Requirements.ToArray()).Build());
        }

        if (options.AdministrationAuthentication is { } panel)
        {
            await RequireSchemeAsync(panel.ChallengeScheme);
            var signOut = await RequireSchemeAsync(panel.SignOutScheme);
            if (!typeof(IAuthenticationSignOutHandler).IsAssignableFrom(signOut.HandlerType))
            {
                throw new InvalidOperationException($"Proxy sign-out scheme '{signOut.Name}' does not support sign-out.");
            }

            await ConfigurePolicyAsync(panel, NhProxyOptions.AdministrationPolicy, NhProxyHostAuthenticationHandler.AdministrationScheme);
        }

        if (options.ApiAuthentication is { } api)
        {
            await ConfigurePolicyAsync(api, NhProxyOptions.ApiPolicy, NhProxyHostAuthenticationHandler.ApiScheme);
        }
    }
}
