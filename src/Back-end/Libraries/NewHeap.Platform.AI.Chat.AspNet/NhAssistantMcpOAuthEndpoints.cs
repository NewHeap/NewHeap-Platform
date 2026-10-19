using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;

namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>
/// The anonymous half of MCP OAuth: the redirect target of authorization servers and the host's Client
/// ID Metadata Document. The callback carries no session (D7); it exchanges the code and hands a
/// one-time completion code to the starter's browser, which completes the connection with its own
/// session through <c>POST mcp-connections/complete</c>.
/// </summary>
internal static class NhAssistantMcpOAuthEndpoints
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
        IOptionsMonitor<NhAssistantOptions> options)
    {
        var query = httpContext.Request.Query;
        var outcome = await flows.CallbackAsync(
            Single(query["state"]),
            Single(query["code"]),
            Single(query["iss"]),
            Single(query["error"]),
            httpContext.RequestAborted);
        var headers = httpContext.Response.Headers;
        headers.CacheControl = "no-store";
        headers.Pragma = "no-cache";
        headers["Referrer-Policy"] = "no-referrer";
        headers.XContentTypeOptions = "nosniff";
        if (outcome.ReturnUrl is { } returnUrl)
        {
            var fragment = outcome.CompletionCode is { } completion
                ? $"nh-mcp-flow={outcome.FlowId:D}&nh-mcp-code={Uri.EscapeDataString(completion)}"
                : $"nh-mcp-error={Uri.EscapeDataString(outcome.FailureCode ?? NhAssistantAdminErrorCodes.McpAuthorizationFailed)}";
            return TypedResults.Redirect(returnUrl + "#" + fragment);
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
    /// The page in the authorization window: it posts the outcome to its opener, addressed to each
    /// allowed origin so the browser delivers it only when the opener has one of them, and closes.
    /// Without an opener it tells the person to return to the application.
    /// </summary>
    private static string Page(NhAssistantMcpCallbackOutcome outcome, IReadOnlyList<string> origins, string nonce, bool dutch)
    {
        var message = new JsonObject
        {
            ["type"] = MessageType,
            ["flowId"] = outcome.FlowId?.ToString("D"),
            ["completionCode"] = outcome.CompletionCode,
            ["code"] = outcome.FailureCode
        };
        // The default encoder escapes <, >, & and quotes, so the values cannot leave the script.
        var payload = message.ToJsonString();
        var allowed = new JsonArray([.. origins.Where(item => item.Length > 0).Select(item => (JsonNode)JsonValue.Create(item)!)]).ToJsonString();
        var (title, text) = (outcome.CompletionCode is not null, dutch) switch
        {
            (true, false) => ("Authorization received", "You can close this window and return to the application."),
            (true, true) => ("Autorisatie ontvangen", "Je kunt dit venster sluiten en teruggaan naar de applicatie."),
            (false, false) => ("Authorization not completed", "The connection was not made. Close this window and try again from the application."),
            (false, true) => ("Autorisatie niet voltooid", "De koppeling is niet gemaakt. Sluit dit venster en probeer het opnieuw vanuit de applicatie.")
        };
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
