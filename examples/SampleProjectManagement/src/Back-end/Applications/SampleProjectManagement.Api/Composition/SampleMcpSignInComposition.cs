using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
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
/// <para>
/// Other applications get an MCP endpoint of their own, <see cref="ApplicationsMcpPath"/>: a named
/// protected resource (<see cref="ApplicationsResourceName"/>) with its own audience, authorization
/// servers, scopes and metadata, so a token for one endpoint never signs in at the other.
/// </para>
/// <para>
/// Alternative for a deployment whose identity provider issues tokens for <c>/mcp</c> that the
/// application validates with a scheme of its own (<see cref="ServiceTokenScheme"/>): that scheme marks
/// each token as never passed on with <see cref="NhAiCallerCredentials.MarkNotForwardable"/>, as the
/// protected resources do for theirs. Prefer a protected resource.
/// </para>
/// </summary>
/// <remarks>
/// A deployment names, for example in its secrets or environment:
/// <c>SampleProjectManagement:McpSignIn:Resource</c> (the public URL of <c>/mcp</c>, such as
/// <c>https://projects.example.org/mcp</c>), <c>AuthorizationServers:0</c> (the issuer of its
/// authorization server) and <c>ScopesSupported:0</c> (<c>projects.read</c>). The authorization server
/// registers the clients and asks the person's consent; it issues the tokens for that resource. The
/// endpoint for other applications takes the same keys under <c>SampleProjectManagement:McpSignIn:Applications</c>,
/// with the public URL of <c>/applications/mcp</c> as its <c>Resource</c>; the alternative scheme takes
/// <c>SampleProjectManagement:McpSignIn:ServiceTokens:Authority</c> and <c>:Audience</c>.
/// </remarks>
public static class SampleMcpSignInComposition
{
    public const string SectionName = "SampleProjectManagement:McpSignIn";

    public const string ApplicationsSectionName = SectionName + ":Applications";

    public const string ServiceTokensSectionName = SectionName + ":ServiceTokens";

    public const string McpPath = "/mcp";

    /// <summary>The MCP endpoint of other applications, a named protected resource of its own.</summary>
    public const string ApplicationsMcpPath = "/applications/mcp";

    /// <summary>The name of the protected resource of <see cref="ApplicationsMcpPath"/>.</summary>
    public const string ApplicationsResourceName = "applications";

    /// <summary>
    /// The application's own scheme for tokens of its identity provider whose audience is <c>/mcp</c>,
    /// accepted at <c>/mcp</c> as a host scheme of the protected resource.
    /// </summary>
    public const string ServiceTokenScheme = "SampleProjectManagement.McpServiceTokens";

    public const string LoginProviderDisplayName = "MCP sign-in";

    /// <summary>Whether the deployment configured the sign-in of MCP clients.</summary>
    public static bool IsConfigured(IConfiguration configuration)
    {
        return !string.IsNullOrWhiteSpace(configuration[SectionName + ":Resource"]);
    }

    /// <summary>Whether the deployment configured the MCP endpoint of other applications.</summary>
    public static bool HasApplicationsEndpoint(IConfiguration configuration)
    {
        return IsConfigured(configuration)
            && !string.IsNullOrWhiteSpace(configuration[ApplicationsSectionName + ":Resource"]);
    }

    /// <summary>Whether the deployment configured the alternative scheme for service tokens.</summary>
    public static bool AcceptsServiceTokens(IConfiguration configuration)
    {
        return IsConfigured(configuration)
            && !string.IsNullOrWhiteSpace(configuration[ServiceTokensSectionName + ":Authority"]);
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
    /// Makes the MCP exposure a protected resource of the configured authorization servers, and adds the
    /// named resource of other applications when it is configured; does nothing without
    /// <see cref="SectionName"/>.
    /// </summary>
    public static NhAiMcpExposureBuilder WithSampleMcpSignIn(
        this NhAiMcpExposureBuilder exposure,
        IConfiguration configuration)
    {
        if (!IsConfigured(configuration))
        {
            return exposure;
        }

        exposure.UseProtectedResource(resource =>
        {
            configuration.GetSection(SectionName).Bind(resource);
            resource.ResourceName ??= "Sample Project Management";
            // The application's own clients keep their NewHeap token at the same endpoint.
            resource.AcceptHostSchemes.Add(JwtBearerDefaults.AuthenticationScheme);
            if (AcceptsServiceTokens(configuration))
            {
                resource.AcceptHostSchemes.Add(ServiceTokenScheme);
            }
            resource.OnTokenValidated = SignInLinkedAccountAsync;
        });
        if (HasApplicationsEndpoint(configuration))
        {
            // FB57: an audience, authorization servers, scopes and metadata of its own.
            exposure.UseProtectedResource(ApplicationsResourceName, resource =>
            {
                configuration.GetSection(ApplicationsSectionName).Bind(resource);
                resource.ResourceName ??= "Sample Project Management for applications";
                resource.OnTokenValidated = SignInLinkedAccountAsync;
            });
        }

        return exposure;
    }

    /// <summary>
    /// Alternative to a protected resource: the application's own bearer scheme for tokens of its
    /// identity provider whose audience is <c>/mcp</c>. Each validated token is marked as never passed on
    /// (FB58), because it is a credential for the MCP endpoint only, and signs in as its linked account.
    /// Does nothing without <see cref="ServiceTokensSectionName"/>. Outside Development the provider's
    /// metadata is read over https only.
    /// </summary>
    public static AuthenticationBuilder AddSampleMcpServiceTokens(
        this AuthenticationBuilder authentication,
        IConfiguration configuration)
    {
        if (!AcceptsServiceTokens(configuration))
        {
            return authentication;
        }

        var section = configuration.GetSection(ServiceTokensSectionName);
        authentication.AddJwtBearer(ServiceTokenScheme, options =>
        {
            options.Authority = section["Authority"];
            options.Audience = section["Audience"];
            options.MapInboundClaims = false;
            options.Events = new JwtBearerEvents { OnTokenValidated = SignInServiceAccountAsync };
        });
        authentication.Services
            .AddOptions<JwtBearerOptions>(ServiceTokenScheme)
            .Configure<IHostEnvironment>((options, environment) => options.RequireHttpsMetadata = !environment.IsDevelopment());
        return authentication;
    }

    /// <summary>
    /// Maps the API's MCP endpoint: behind the protected resource's policy when the sign-in is configured,
    /// otherwise behind the API's default policy as before. The endpoint of other applications is mapped
    /// behind its own resource's policy when it is configured.
    /// </summary>
    public static IEndpointConventionBuilder MapSampleMcp(
        this IEndpointRouteBuilder endpoints,
        IConfiguration configuration)
    {
        if (HasApplicationsEndpoint(configuration))
        {
            endpoints.MapMcp(ApplicationsMcpPath)
                .RequireAuthorization(NhAiMcpProtectedResourceDefaults.AuthorizationPolicyFor(ApplicationsResourceName));
        }

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
        var (principal, failure) = await LinkedAccountAsync(context.HttpContext, context.Issuer, context.Subject, context.Principal);
        if (principal is null)
        {
            context.Fail(failure!);
            return;
        }

        context.Principal = principal;
    }

    /// <summary>
    /// The alternative scheme's sign-in: the token is marked as never passed on before anything else, and
    /// the request signs in as the token's linked account.
    /// </summary>
    private static async Task SignInServiceAccountAsync(TokenValidatedContext context)
    {
        var issuer = context.SecurityToken.Issuer;
        NhAiCallerCredentials.MarkNotForwardable(context.HttpContext, issuer);

        var token = context.Principal ?? new ClaimsPrincipal(new ClaimsIdentity());
        var (principal, failure) = await LinkedAccountAsync(context.HttpContext, issuer, token.FindFirst("sub")?.Value, token);
        if (principal is null)
        {
            context.Fail(failure!);
            return;
        }

        context.Principal = principal;
    }

    /// <summary>
    /// The account linked to an issuer and subject with the claims a NewHeap sign-in of that account
    /// carries and the scopes the token grants, or why there is none.
    /// </summary>
    private static async Task<(ClaimsPrincipal? Principal, string? Failure)> LinkedAccountAsync(
        HttpContext httpContext,
        string issuer,
        string? subject,
        ClaimsPrincipal token)
    {
        if (subject is null)
        {
            return (null, "The token names no subject.");
        }

        var services = httpContext.RequestServices;
        var cancellationToken = httpContext.RequestAborted;
        var account = await services.GetRequiredService<UserManager<NhUser>>().FindByLoginAsync(issuer, subject);
        if (account is null)
        {
            return (null, "The subject is linked to no account.");
        }
        if (!await services.GetRequiredService<INhAuthenticationService>().MaySignInAsync(account.Id, cancellationToken))
        {
            return (null, "The linked account may not sign in.");
        }

        var claims = await services.GetRequiredService<INhUserManager<NhUser>>()
            .GetValidClaimsByUserIdAsync(account.Id, withDivision: true, cancellationToken);
        var identity = new ClaimsIdentity(claims, NhAiMcpProtectedResourceDefaults.AuthenticationScheme);
        identity.AddClaims(token.FindAll("scope").Select(scope => new Claim(scope.Type, scope.Value)));
        return (new ClaimsPrincipal(identity), null);
    }
}
