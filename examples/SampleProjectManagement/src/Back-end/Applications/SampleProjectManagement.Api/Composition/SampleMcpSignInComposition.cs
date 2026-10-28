using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Services;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-370: MCP clients outside the application sign in to the API's <c>/mcp</c> through the
/// application's authorization server, as any client that follows the MCP authorization flow does. With
/// <see cref="SectionName"/> configured, the endpoint is a protected resource of that server: it publishes
/// its metadata, challenges a request without a valid token and accepts only tokens whose audience is
/// <c>Resource</c>. The person of such a token is the account linked to the token's issuer and subject
/// (<see cref="LoginInfo"/>), with that account's current rights; a subject linked to no account, or an
/// account that may not sign in, is refused. The application's own clients keep signing in with its NewHeap
/// token. Without the section nothing changes.
/// </summary>
/// <remarks>
/// A deployment names, for example in its secrets or environment:
/// <c>SampleProjectManagement:McpSignIn:Resource</c> (the public URL of <c>/mcp</c>, such as
/// <c>https://projects.example.org/mcp</c>), <c>AuthorizationServers:0</c> (the issuer of its
/// authorization server) and <c>ScopesSupported:0</c> (<c>projects.read</c>). The authorization server
/// registers the clients and asks the person's consent; it issues the tokens for that resource.
/// </remarks>
public static class SampleMcpSignInComposition
{
    public const string SectionName = "SampleProjectManagement:McpSignIn";

    public const string McpPath = "/mcp";

    public const string LoginProviderDisplayName = "MCP sign-in";

    /// <summary>Whether the deployment configured the sign-in of MCP clients.</summary>
    public static bool IsConfigured(IConfiguration configuration)
    {
        return !string.IsNullOrWhiteSpace(configuration[SectionName + ":Resource"]);
    }

    /// <summary>
    /// The login that links an account to a person of the authorization server: the server's issuer and
    /// the person's subject. Link it with <c>UserManager.AddLoginAsync(account, LoginInfo(issuer, subject))</c>.
    /// </summary>
    public static UserLoginInfo LoginInfo(string issuer, string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        return new UserLoginInfo(issuer, subject, LoginProviderDisplayName);
    }

    /// <summary>
    /// Makes the MCP exposure a protected resource of the configured authorization servers; does nothing
    /// without <see cref="SectionName"/>.
    /// </summary>
    public static NhAiMcpExposureBuilder WithSampleMcpSignIn(
        this NhAiMcpExposureBuilder exposure,
        IConfiguration configuration)
    {
        if (!IsConfigured(configuration))
        {
            return exposure;
        }

        return exposure.UseProtectedResource(resource =>
        {
            configuration.GetSection(SectionName).Bind(resource);
            resource.ResourceName ??= "Sample Project Management";
            // The application's own clients keep their NewHeap token at the same endpoint.
            resource.AcceptHostSchemes.Add(JwtBearerDefaults.AuthenticationScheme);
            resource.OnTokenValidated = SignInLinkedAccountAsync;
        });
    }

    /// <summary>
    /// Maps the API's MCP endpoint: behind the protected resource's policy when the sign-in is configured,
    /// otherwise behind the API's default policy as before.
    /// </summary>
    public static IEndpointConventionBuilder MapSampleMcp(
        this IEndpointRouteBuilder endpoints,
        IConfiguration configuration)
    {
        var mcp = endpoints.MapMcp(McpPath);
        return IsConfigured(configuration)
            ? mcp.RequireAuthorization(NhAiMcpProtectedResourceDefaults.AuthorizationPolicy)
            : mcp.RequireAuthorization();
    }

    /// <summary>
    /// Signs the request in as the account linked to the token's issuer and subject, with the claims a
    /// NewHeap sign-in of that account carries and the scopes the token grants.
    /// </summary>
    private static async Task SignInLinkedAccountAsync(NhAiMcpProtectedResourceTokenValidatedContext context)
    {
        if (context.Subject is null)
        {
            context.Fail("The token names no subject.");
            return;
        }

        var services = context.HttpContext.RequestServices;
        var cancellationToken = context.HttpContext.RequestAborted;
        var account = await services.GetRequiredService<UserManager<NhUser>>().FindByLoginAsync(context.Issuer, context.Subject);
        if (account is null)
        {
            context.Fail("The subject is linked to no account.");
            return;
        }
        if (!await services.GetRequiredService<INhAuthenticationService>().MaySignInAsync(account.Id, cancellationToken))
        {
            context.Fail("The linked account may not sign in.");
            return;
        }

        var claims = await services.GetRequiredService<INhUserManager<NhUser>>()
            .GetValidClaimsByUserIdAsync(account.Id, withDivision: true, cancellationToken);
        var identity = new ClaimsIdentity(claims, NhAiMcpProtectedResourceDefaults.AuthenticationScheme);
        identity.AddClaims(context.Principal.FindAll("scope").Select(scope => new Claim(scope.Type, scope.Value)));
        context.Principal = new ClaimsPrincipal(identity);
    }
}
