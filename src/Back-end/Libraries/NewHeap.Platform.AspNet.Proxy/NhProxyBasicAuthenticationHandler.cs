using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NewHeap.Platform.AspNet.Proxy;

/// <summary>Authenticates server-to-server management requests without creating browser sessions.</summary>
public sealed class NhProxyBasicAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, INhProxyAdministrationService administration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private int _failureStatus = StatusCodes.Status401Unauthorized;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Only the explicitly mapped API may consume these credentials, never ordinary proxy traffic.
        if (!Context.Items.ContainsKey(typeof(NhProxyBasicAuthenticationHandler)))
        {
            return AuthenticateResult.NoResult();
        }

        var credentials = new NhProxyLoginRequest { UserName = "", Password = "" };
        var value = Request.Headers.Authorization;
        if (value.Count == 1 && value[0] is { Length: <= 8192 } header
            && AuthenticationHeaderValue.TryParse(header, out var authorization)
            && authorization.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase)
            && authorization.Parameter is { } encoded)
        {
            try
            {
                var decoded = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(encoded));
                var separator = decoded.IndexOf(':');
                if (separator > 0)
                {
                    credentials = new NhProxyLoginRequest { UserName = decoded[..separator], Password = decoded[(separator + 1)..] };
                }
            }
            catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
            {
                // Malformed credentials consume the API failure budget, not a browser login attempt.
            }
        }

        var result = await administration.AuthenticateAsync(Context, credentials, Context.RequestAborted);
        if (!result.Success)
        {
            var codes = result.GetResultItems().Select(item => item.Name).ToArray();
            _failureStatus = codes.Contains(NhProxyErrorCodes.AccessDenied) ? StatusCodes.Status403Forbidden
                : codes.Contains(NhProxyErrorCodes.Throttled) ? StatusCodes.Status429TooManyRequests
                : StatusCodes.Status401Unauthorized;
            return AuthenticateResult.Fail("Proxy API authentication failed.");
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, credentials.UserName)], Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = _failureStatus;
        if (_failureStatus == StatusCodes.Status401Unauthorized)
        {
            Response.Headers.WWWAuthenticate = "Basic realm=\"NewHeap Proxy\", charset=\"UTF-8\"";
        }

        return Task.CompletedTask;
    }
}
