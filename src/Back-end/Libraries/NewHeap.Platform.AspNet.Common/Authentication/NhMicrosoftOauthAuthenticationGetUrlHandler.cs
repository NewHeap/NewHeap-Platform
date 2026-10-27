using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AspNet.Common.Authentication.TwoFactor;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Models.MicrosoftAuth;
using NewHeap.Platform.Common.Models.Options;
using NewHeap.Platform.Common.Services;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using HttpMethod = NewHeap.Platform.AspNet.Common.Builders.HttpMethod;

namespace NewHeap.Platform.AspNet.Common.Authentication;

public class NhMicrosoftOauthAuthenticationGetUrlHandler : BaseNhAuthenticationEndpoint
{
    public NhMicrosoftOauthAuthenticationGetUrlHandler(
        IHttpContextAccessor httpContextAccessor,
        AuthenticationConfiguration configuration
    ) : base(httpContextAccessor, "authentication/oath/microsoft", configuration)
    {
        Method = HttpMethod.Post;
        Handler = ExecuteAsync;
    }

    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Authentication")]
    [EndpointName("Get Microsoft OAuth url")]
    [Produces<Results<Ok<string>,BadRequest>>]
    private Task<IResult> ExecuteAsync([FromBody] MicrosoftAuthUrlMutateModel? request)
    {
        var result = new TaskResult<string>();
        if (string.IsNullOrWhiteSpace(request?.CallbackUrl))
        {
            result.AddError(nameof(MicrosoftAuthUrlMutateModel.CallbackUrl), "Field is required");
        }

        if (string.IsNullOrWhiteSpace(request?.UserName))
        {
            result.AddError(nameof(MicrosoftAuthUrlMutateModel.UserName), "Field is required");
        }

        if (!result.Success)
        {
            return Task.FromResult(BadRequest(result));
        }

        var services = HttpContext!.RequestServices;
        var userManager = services.GetRequiredService<INhUserManager>();
        if (!userManager.IsOauthAccount(request!.UserName))
        {
            return Task.FromResult<IResult>(TypedResults.Unauthorized());
        }

        var settings = services.GetRequiredService<IOptions<MicrosoftAuthSettings>>().Value;
        if (!NhMicrosoftOAuthReturnUrls.IsAllowed(request.CallbackUrl, settings, services))
        {
            return Task.FromResult(BadRequest(TaskResult.Failed(
                nameof(MicrosoftAuthUrlMutateModel.CallbackUrl),
                "The return URL is not a path of this application or an allowed origin")));
        }

        // The state is the protected flow; the cookie binds it to this browser.
        var stateProtector = services.GetRequiredService<NhMicrosoftOAuthStateProtector>();
        var flow = stateProtector.Start(request.CallbackUrl);
        HttpContext.Response.Cookies.Append(
            NhMicrosoftOAuthStateProtector.CorrelationCookieName(flow),
            NhMicrosoftOAuthReturnUrls.CorrelationCookieValue,
            NhMicrosoftOAuthReturnUrls.CorrelationCookieOptions(settings, flow.ExpiresAt));

        var microsoftAuthService = services.GetRequiredService<MicrosoftAuthService>();
        var url = microsoftAuthService.GetLoginUrl(
            stateProtector.Protect(flow),
            flow.Nonce,
            NhMicrosoftOAuthStateProtector.CodeChallenge(flow.CodeVerifier));

        return Task.FromResult(Ok(url));
    }
}

/// <summary>
/// The return-URL and correlation-cookie rules that the Microsoft OAuth endpoints share.
/// </summary>
internal static class NhMicrosoftOAuthReturnUrls
{
    internal const string CorrelationCookieValue = "1";

    /// <summary>
    /// Whether the callback may return to <paramref name="returnUrl"/>: a path on the callback's
    /// host, or an absolute URL on <see cref="MicrosoftAuthSettings.AllowedReturnOrigins"/>, or on
    /// the application's allowed origins when that list is empty.
    /// </summary>
    internal static bool IsAllowed(string? returnUrl, MicrosoftAuthSettings settings, IServiceProvider services)
    {
        IEnumerable<string> allowedOrigins = settings.AllowedReturnOrigins is { Length: > 0 }
            ? settings.AllowedReturnOrigins
            : services.GetService<IOptions<NewHeapAspNetCommonSettings>>()?.Value.AllowedOrigins ?? [];

        return NhReturnUrls.IsAllowed(returnUrl, allowedOrigins);
    }

    /// <summary>
    /// A host-only cookie for the path of the configured callback, which Microsoft's top-level
    /// redirect to the callback carries (<c>SameSite=Lax</c>).
    /// </summary>
    internal static CookieOptions CorrelationCookieOptions(MicrosoftAuthSettings settings, DateTimeOffset? expires)
    {
        return new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = CallbackPath(settings),
            Expires = expires,
            IsEssential = true,
        };
    }

    private static string CallbackPath(MicrosoftAuthSettings settings)
    {
        if (Uri.TryCreate(settings.CallbackUrl, UriKind.Absolute, out var callbackUrl)
            && callbackUrl.Scheme is "https" or "http")
        {
            return callbackUrl.AbsolutePath;
        }

        return "/";
    }
}


public class NhMicrosoftOauthAuthenticationAuthorizeHandler<TUser> : BaseNhAuthenticationEndpoint
where TUser : IdentityUser<Guid>
{
    public NhMicrosoftOauthAuthenticationAuthorizeHandler(
        IHttpContextAccessor httpContextAccessor,
        AuthenticationConfiguration configuration
    ) : base(httpContextAccessor, "authentication/oath/microsoft/authorize", configuration)
    {
        Method = HttpMethod.Get;
        Handler = ExecuteAsync;
    }

    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Authentication")]
    [EndpointName("Get Microsoft OAuth authorize")]
    [Produces<Results<Ok<UserToken>,BadRequest>>]
    private async Task<IResult> ExecuteAsync([FromQuery] string code, [FromQuery] string state)
    {
        var request = new MicrosoftAuthorizationRequest { Code = code, State = state };

        if (string.IsNullOrEmpty(request.Code) || string.IsNullOrEmpty(request.State))
        {
            return BadRequest(TaskResult.Failed("Invalid request"));
        }

        var services = HttpContext!.RequestServices;
        var settings = services.GetRequiredService<IOptions<MicrosoftAuthSettings>>().Value;

        // Only a state this application issued, to the browser that presents it, completes a
        // sign-in, and only once: the correlation cookie is removed now.
        var flow = ConsumeFlow(request.State, settings);
        if (flow == null || !NhMicrosoftOAuthReturnUrls.IsAllowed(flow.ReturnUrl, settings, services))
        {
            return BadRequest(TaskResult.Failed("Invalid state"));
        }

        var userManager = services.GetRequiredService<INhUserManager<TUser>>();
        var microsoftAuthService = services.GetRequiredService<MicrosoftAuthService>();

        var token = await microsoftAuthService.RedeemCodeAsync(request.Code, flow.CodeVerifier);
        if (token == null)
        {
            return BadRequest(TaskResult.Failed("Failed to validate token"));
        }

        var identity = NhMicrosoftOAuthIdentity.FromIdToken(
            token.IdToken,
            settings.ClientId,
            flow.Nonce,
            services.GetRequiredService<TimeProvider>().GetUtcNow());
        if (identity == null)
        {
            return BadRequest(TaskResult.Failed("Failed to validate token"));
        }

        if (!identity.TenantMaySignIn(settings))
        {
            return BadRequest(TaskResult.Failed("Invalid login method"));
        }

        var user = await FindOrLinkAccountAsync(identity, token, settings);
        if (user == null)
        {
            return BadRequest(TaskResult.Failed("Unknown user"));
        }

        if (!userManager.IsOauthAccount(user.UserName!))
        {
            return BadRequest(TaskResult.Failed("Invalid login method"));
        }

        if (await userManager.IsBlockedAsync(user))
        {
            return TypedResults.Unauthorized();
        }

        var authService = GetAuthService();
        var redirect = flow.ReturnUrl;

        var twoFactorConfiguration = HttpContext!.RequestServices.GetService<NhTwoFactorConfiguration>();
        if (twoFactorConfiguration?.Enabled == true)
        {
            return await AuthorizeWithTwoFactorAsync(authService, user.Id, redirect);
        }

        var tokenResult = await authService.LoginWithoutValidations(user.Id, true);
        if (!tokenResult.Success)
        {
            return BadRequest(tokenResult);
        }

        WriteTokenToCookie(tokenResult.Data!);

        return TypedResults.Redirect(redirect, preserveMethod:true);
    }

    /// <summary>
    /// The local account linked to the Microsoft account by its tenant and object id. Without a
    /// link, and only when <see cref="NhMicrosoftOAuthIdentity.MayLinkByEmail"/> allows it, the
    /// local account whose user name is the Graph mail address is linked once, provided it signs in
    /// with Microsoft and is not linked to another Microsoft account yet.
    /// </summary>
    private async Task<TUser?> FindOrLinkAccountAsync(
        NhMicrosoftOAuthIdentity identity,
        MicosoftAuthTokenSuccessResponse token,
        MicrosoftAuthSettings settings)
    {
        var services = HttpContext!.RequestServices;
        var identityUsers = services.GetRequiredService<UserManager<TUser>>();
        var login = NhMicrosoftOAuthLogins.LoginInfo(identity.TenantId.ToString(), identity.ObjectId.ToString());

        var linkedUser = await identityUsers.FindByLoginAsync(login.LoginProvider, login.ProviderKey);
        if (linkedUser != null)
        {
            return linkedUser;
        }

        if (!identity.MayLinkByEmail(settings))
        {
            return null;
        }

        var profile = await services.GetRequiredService<MicrosoftAuthService>().GetProfile(token.AccessToken);
        if (string.IsNullOrWhiteSpace(profile?.Mail))
        {
            return null;
        }

        var userManager = services.GetRequiredService<INhUserManager<TUser>>();
        var user = await userManager.FindByNameAsync(profile.Mail);
        if (user == null || !userManager.IsOauthAccount(user.UserName!))
        {
            return null;
        }

        var logins = await identityUsers.GetLoginsAsync(user);
        if (logins.Any(existing => existing.LoginProvider == NhMicrosoftOAuthLogins.LoginProvider))
        {
            // The account belongs to another Microsoft account; the same mail address does not move it.
            return null;
        }

        var linked = await identityUsers.AddLoginAsync(user, login);
        if (!linked.Succeeded)
        {
            return null;
        }

        services.GetRequiredService<ILogger<NhMicrosoftOauthAuthenticationAuthorizeHandler<TUser>>>().LogInformation(
            "Linked local account {UserId} to a Microsoft account of tenant {TenantId} by its mail address.",
            user.Id,
            identity.TenantId);
        return user;
    }

    /// <summary>
    /// The flow in <paramref name="state"/> when this application issued it, it has not expired and
    /// this browser carries its correlation cookie; the cookie is removed, so the flow completes once.
    /// </summary>
    private NhMicrosoftOAuthFlow? ConsumeFlow(string state, MicrosoftAuthSettings settings)
    {
        var stateProtector = HttpContext!.RequestServices.GetRequiredService<NhMicrosoftOAuthStateProtector>();
        var flow = stateProtector.Unprotect(state);
        if (flow == null)
        {
            return null;
        }

        var cookieName = NhMicrosoftOAuthStateProtector.CorrelationCookieName(flow);
        if (!HttpContext.Request.Cookies.TryGetValue(cookieName, out var cookieValue)
            || cookieValue != NhMicrosoftOAuthReturnUrls.CorrelationCookieValue)
        {
            return null;
        }

        HttpContext.Response.Cookies.Delete(cookieName, NhMicrosoftOAuthReturnUrls.CorrelationCookieOptions(settings, expires: null));
        return flow;
    }

    /// <summary>
    /// Completes the sign-in through the two-factor policy. When a NewHeap second factor is
    /// still required, the challenge travels in the URL fragment, which browsers never send
    /// to a server.
    /// </summary>
    private async Task<IResult> AuthorizeWithTwoFactorAsync(INhAuthenticationService authService, Guid userId, string redirect)
    {
        if (authService is not INhMultiFactorAuthenticationService { IsTwoFactorAvailable: true } multiFactorService)
        {
            return BadRequest(NhTwoFactorFailureCodes.Fail(NhTwoFactorFailureCodes.ConfigurationInvalid));
        }

        var result = await multiFactorService.AuthenticateExternalAsync(userId, NhAuthenticationFactors.MicrosoftOAuth);
        if (!result.Success)
        {
            return BadRequest(result);
        }

        var redirectWithoutFragment = redirect.Split('#')[0];
        if (result.Data!.Session == null)
        {
            var challenge = JsonSerializer.SerializeToUtf8Bytes(result.Data.Challenge, JsonSerializerOptions.Web);
            var fragment = "nh-two-factor=" + WebEncoders.Base64UrlEncode(challenge);
            return TypedResults.Redirect(redirectWithoutFragment + "#" + fragment, preserveMethod: true);
        }

        WriteTokenToCookie(result.Data.Session);
        return TypedResults.Redirect(redirectWithoutFragment, preserveMethod: true);
    }
}

public class MicrosoftAuthorizationRequest
{
    public string Code { get; set; } = null!;
    public string State { get; set; } = null!;
}

public class MicrosoftAuthUrlMutateModel
{
    [Required]
    public string CallbackUrl { get; set; } = null!;
    [Required]
    public string UserName { get; set; } = null!;
}