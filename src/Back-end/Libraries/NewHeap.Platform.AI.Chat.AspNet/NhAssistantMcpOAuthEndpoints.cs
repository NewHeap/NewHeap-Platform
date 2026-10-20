using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;

namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>
/// The anonymous half of MCP OAuth: the redirect target of authorization servers and the host's Client
/// ID Metadata Document. The callback carries no session (D7); it exchanges the code and hands a
/// one-time completion code to the starter's browser, which completes the connection with its own
/// session through <c>POST mcp-connections/complete</c>. A failure reaches the browser as its code
/// and localization key, like every assistant error: in the message to the opener
/// (<c>code</c>, <c>messageKey</c>) or in the return URL's fragment (<c>nh-mcp-error</c>,
/// <c>nh-mcp-message-key</c>).
/// </summary>
internal static partial class NhAssistantMcpOAuthEndpoints
{
    /// <summary>The <c>type</c> of the message the callback page posts to its opener.</summary>
    public const string MessageType = "nh-assistant-mcp-authorization";

    public static void Map(RouteGroupBuilder anonymous)
    {
        anonymous.MapGet("mcp/oauth/callback", CallbackAsync)
            .AllowAnonymous()
            .ExcludeFromDescription();
        anonymous.MapGet("mcp/oauth/client-metadata.json", GetClientMetadata)
            .AllowAnonymous()
            .WithName("NhAssistantGetMcpClientMetadata")
            .WithSummary("Get the host's OAuth client metadata")
            .WithDescription("The Client ID Metadata Document by which an authorization server that supports such documents identifies this host. 404 when NhAssistantOptions.Mcp.OAuth.ClientMetadataDocumentUri is not set.")
            .Produces(StatusCodes.Status200OK, contentType: "application/json")
            .Produces(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> CallbackAsync(
        HttpContext httpContext,
        NhAssistantMcpAuthorizationFlows flows,
        IOptionsMonitor<NhAssistantOptions> options,
        ILoggerFactory loggerFactory)
    {
        var query = httpContext.Request.Query;
        NhAssistantMcpCallbackOutcome outcome;
        try
        {
            outcome = await flows.CallbackAsync(
                Single(query["state"]),
                Single(query["code"]),
                Single(query["iss"]),
                Single(query["error"]),
                httpContext.RequestAborted);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            && !(exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested))
        {
            // The person still gets the page that tells them to try again, never a bare 500; the
            // flow stays pending until it expires, and the exception goes to the log only.
            LogCallbackFailed(loggerFactory.CreateLogger(typeof(NhAssistantMcpOAuthEndpoints)), exception);
            NhAssistantMcpMetrics.Authorization(NhAssistantAdminErrorCodes.McpAuthorizationFailed);
            outcome = new NhAssistantMcpCallbackOutcome(null, null, NhAssistantAdminErrorCodes.McpAuthorizationFailed, null);
        }
        var headers = httpContext.Response.Headers;
        headers.CacheControl = "no-store";
        headers.Pragma = "no-cache";
        headers["Referrer-Policy"] = "no-referrer";
        headers.XContentTypeOptions = "nosniff";
        if (outcome.ReturnUrl is { } returnUrl)
        {
            return TypedResults.Redirect(returnUrl + "#" + Fragment(outcome));
        }
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        headers.ContentSecurityPolicy =
            $"default-src 'none'; script-src 'nonce-{nonce}'; style-src 'nonce-{nonce}'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
        var origins = NhAssistantMcpAuthorizationFlows.AllowedOrigins(options.CurrentValue.Mcp.OAuth);
        return TypedResults.Content(Page(outcome, origins, nonce, Dutch(httpContext)), "text/html; charset=utf-8");
    }

    private static IResult GetClientMetadata(IOptionsMonitor<NhAssistantOptions> options)
    {
        var oauth = options.CurrentValue.Mcp.OAuth;
        if (string.IsNullOrWhiteSpace(oauth.ClientMetadataDocumentUri) || string.IsNullOrWhiteSpace(oauth.RedirectUri))
        {
            return TypedResults.NotFound();
        }
        var document = new JsonObject
        {
            ["client_id"] = oauth.ClientMetadataDocumentUri,
            ["client_name"] = oauth.ClientName,
            ["redirect_uris"] = new JsonArray(oauth.RedirectUri),
            ["grant_types"] = new JsonArray("authorization_code", "refresh_token"),
            ["response_types"] = new JsonArray("code"),
            ["token_endpoint_auth_method"] = "none"
        };
        if (!string.IsNullOrWhiteSpace(oauth.ClientUri))
        {
            document["client_uri"] = oauth.ClientUri;
        }
        return TypedResults.Content(document.ToJsonString(), "application/json");
    }

    /// <summary>
    /// The return URL's fragment: the flow and its completion code, or the failure code and its
    /// localization key.
    /// </summary>
    internal static string Fragment(NhAssistantMcpCallbackOutcome outcome)
    {
        if (outcome.CompletionCode is { } completion)
        {
            return $"nh-mcp-flow={outcome.FlowId:D}&nh-mcp-code={Uri.EscapeDataString(completion)}";
        }
        var code = FailureCodeOf(outcome);
        return $"nh-mcp-error={Uri.EscapeDataString(code)}&nh-mcp-message-key={Uri.EscapeDataString(NhAssistantErrorCodes.MessageKey(code))}";
    }

    private static string FailureCodeOf(NhAssistantMcpCallbackOutcome outcome)
    {
        return outcome.FailureCode ?? NhAssistantAdminErrorCodes.McpAuthorizationFailed;
    }

    /// <summary>
    /// The page in the authorization window: it posts the outcome to its opener, addressed to each
    /// allowed origin so the browser delivers it only when the opener has one of them, and closes.
    /// Without an opener it tells the person what happened and to return to the application.
    /// </summary>
    private static string Page(NhAssistantMcpCallbackOutcome outcome, IReadOnlyList<string> origins, string nonce, bool dutch)
    {
        var failure = outcome.CompletionCode is null ? FailureCodeOf(outcome) : null;
        var message = new JsonObject
        {
            ["type"] = MessageType,
            ["flowId"] = outcome.FlowId?.ToString("D"),
            ["completionCode"] = outcome.CompletionCode,
            ["code"] = failure,
            ["messageKey"] = failure is null ? null : NhAssistantErrorCodes.MessageKey(failure)
        };
        // The default encoder escapes <, >, & and quotes, so the values cannot leave the script.
        var payload = message.ToJsonString();
        var allowed = new JsonArray([.. origins.Where(item => item.Length > 0).Select(item => (JsonNode)JsonValue.Create(item)!)]).ToJsonString();
        var (title, text) = PageText(failure, dutch);
        var builder = new StringBuilder();
        builder.Append("<!doctype html><html lang=\"").Append(dutch ? "nl" : "en").Append("\"><head><meta charset=\"utf-8\">");
        builder.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><meta name=\"referrer\" content=\"no-referrer\">");
        builder.Append("<title>").Append(WebUtility.HtmlEncode(title)).Append("</title>");
        builder.Append("<style nonce=\"").Append(nonce).Append("\">body{font-family:system-ui,sans-serif;margin:3rem auto;max-width:32rem;padding:0 1rem;color:#1f2933}</style>");
        builder.Append("</head><body><h1>").Append(WebUtility.HtmlEncode(title)).Append("</h1><p>").Append(WebUtility.HtmlEncode(text)).Append("</p>");
        // A message addressed to another origin than the opener's is dropped by the browser.
        builder.Append("<script nonce=\"").Append(nonce).Append("\">(function(){var m=").Append(payload).Append(",o=").Append(allowed)
            .Append(";if(window.opener&&o.length){for(var i=0;i<o.length;i++){try{window.opener.postMessage(m,o[i]);}catch(e){}}window.close();}})();</script>");
        builder.Append("</body></html>");
        return builder.ToString();
    }

    /// <summary>
    /// The title and text of the page in English or Dutch: received, or why the connection was not
    /// made, for the failures a person can act on, and a general text for the others.
    /// </summary>
    private static (string Title, string Text) PageText(string? failure, bool dutch)
    {
        if (failure is null)
        {
            return dutch
                ? ("Autorisatie ontvangen", "Je kunt dit venster sluiten en teruggaan naar de applicatie.")
                : ("Authorization received", "You can close this window and return to the application.");
        }
        var title = dutch ? "Autorisatie niet voltooid" : "Authorization not completed";
        var text = failure switch
        {
            NhAssistantAdminErrorCodes.McpAuthorizationExpired => dutch
                ? "De autorisatie is verlopen of al gebruikt. Sluit dit venster en begin opnieuw vanuit de applicatie."
                : "The authorization expired or was already used. Close this window and start again from the application.",
            NhAssistantAdminErrorCodes.McpAuthorizationFailed => dutch
                ? "De autorisatieserver heeft de autorisatie niet gegeven. Sluit dit venster en probeer het opnieuw vanuit de applicatie."
                : "The authorization server did not grant the authorization. Close this window and try again from the application.",
            _ => dutch
                ? "De koppeling is niet gemaakt. Sluit dit venster en probeer het opnieuw vanuit de applicatie."
                : "The connection was not made. Close this window and try again from the application."
        };
        return (title, text);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "An MCP OAuth callback failed; the authorization window shows that the connection was not made.")]
    private static partial void LogCallbackFailed(ILogger logger, Exception exception);

    private static bool Dutch(HttpContext httpContext)
    {
        var languages = httpContext.Request.GetTypedHeaders().AcceptLanguage;
        var first = languages.OrderByDescending(item => item.Quality ?? 1).FirstOrDefault();
        return first?.Value.Value is { } value
            && value.StartsWith("nl", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Single(Microsoft.Extensions.Primitives.StringValues values)
    {
        // A repeated parameter is a malformed callback.
        return values.Count == 1 ? values[0] : null;
    }
}
