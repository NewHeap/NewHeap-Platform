using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace NewHeap.Platform.AI.AspNet.Mvc;

/// <summary>
/// Where the bridge places a parameter value on the self-HTTP request.
/// </summary>
public enum NhAiBridgeParameterSource
{
    Route = 0,
    Query = 1,
    Body = 2,
    Form = 3,
    FormFile = 4
}

/// <summary>
/// One bound parameter of a bridged MVC action.
/// </summary>
public sealed class NhAiBridgeParameterInfo
{
    /// <summary>The binding name: the route key, query key or form key the action reads.</summary>
    public required string Name { get; init; }

    /// <summary>The top-level property name in the flat tool input object.</summary>
    public required string InputName { get; init; }

    public required NhAiBridgeParameterSource Source { get; init; }

    public required Type ParameterType { get; init; }

    public bool IsRequired { get; init; }

    /// <summary>
    /// <see langword="true"/> for a query model with <c>Page</c>, <c>ItemsPerPage</c>,
    /// <c>OrderBy</c>, <c>Filter</c> and <c>Search</c>. Its paging, search, ordering and
    /// filter values are published as the collection input fragment.
    /// </summary>
    public bool IsCollectionRequest { get; init; }

    /// <summary>
    /// <see langword="true"/> for a body without a fixed shape, such as a JSON object or
    /// <c>JObject</c> partial update. Its input is forwarded unchanged.
    /// </summary>
    public bool IsFreeForm { get; init; }
}

/// <summary>
/// The description of one MVC action that the bridge publishes as a tool.
/// </summary>
public sealed class NhAiBridgeActionInfo
{
    /// <summary>The controller name without the <c>Controller</c> suffix.</summary>
    public required string ControllerName { get; init; }

    public required string ActionName { get; init; }

    /// <summary>The upper-case HTTP method, such as <c>GET</c>.</summary>
    public required string HttpMethod { get; init; }

    /// <summary>The relative route template without constraints, such as <c>orders/{id}</c>.</summary>
    public required string RouteTemplate { get; init; }

    public required IReadOnlyList<NhAiBridgeParameterInfo> Parameters { get; init; }

    /// <summary>The declared success response type, when the action documents one.</summary>
    public Type? ResponseType { get; init; }

    /// <summary>The named authorization policies of the action and its controller, ordered.</summary>
    public required IReadOnlyList<string> AuthorizationPolicies { get; init; }

    /// <summary>
    /// <see langword="true"/> when another included action of the same controller has the same
    /// action name; the default conventions then disambiguate the tool id with route parameters.
    /// </summary>
    public bool ActionNameCollides { get; init; }

    /// <summary>The <see cref="NhAiBridgeToolAttribute"/> on the action, when present.</summary>
    public NhAiBridgeToolAttribute? BridgeTool { get; init; }

    public required MethodInfo MethodInfo { get; init; }

    public required ControllerActionDescriptor ActionDescriptor { get; init; }

    public required ApiDescription ApiDescription { get; init; }

    /// <summary>The route parameters in template order.</summary>
    public IEnumerable<NhAiBridgeParameterInfo> RouteParameters =>
        Parameters.Where(parameter => parameter.Source == NhAiBridgeParameterSource.Route);

    /// <summary>The body parameter, when the action binds one.</summary>
    public NhAiBridgeParameterInfo? BodyParameter =>
        Parameters.FirstOrDefault(parameter => parameter.Source == NhAiBridgeParameterSource.Body);
}

/// <summary>
/// The self-HTTP request the bridge sends for one tool invocation. Paths are relative to the
/// configured self base URL.
/// </summary>
public sealed class NhAiBridgeHttpRequest(string method, string path)
{
    public string Method { get; } = method;

    /// <summary>The escaped relative path without a leading slash.</summary>
    public string Path { get; } = path;

    public IList<KeyValuePair<string, string>> Query { get; } = new List<KeyValuePair<string, string>>();

    /// <summary>The body object passed to <see cref="INhAiBridgeBodySerializer"/>; null sends no JSON body.</summary>
    public object? Body { get; set; }

    public IList<KeyValuePair<string, string>> FormFields { get; } = new List<KeyValuePair<string, string>>();

    public IList<NhAiBridgeFormFile> FormFiles { get; } = new List<NhAiBridgeFormFile>();

    /// <summary>Builds the relative URI with an escaped query string.</summary>
    public string BuildRelativeUri()
    {
        if (Query.Count == 0)
        {
            return Path;
        }

        var query = string.Join(
            "&",
            Query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        return Path + "?" + query;
    }
}

/// <summary>
/// A file sent as multipart form data. Tool input supplies it as base64 content.
/// </summary>
public sealed record NhAiBridgeFormFile(
    string Name,
    string FileName,
    string? ContentType,
    byte[] Content);

/// <summary>
/// The tool output of a bridged action, wrapped in <c>TaskResult&lt;NhAiBridgeResponse&gt;</c>.
/// </summary>
public sealed record NhAiBridgeResponse
{
    [JsonPropertyOrder(0)]
    public int Status { get; init; }

    [JsonPropertyOrder(1)]
    public string? ContentType { get; init; }

    /// <summary>The JSON response body; omitted when the body was truncated or empty.</summary>
    [JsonPropertyOrder(2)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Body { get; init; }

    [JsonPropertyOrder(3)]
    public bool Truncated { get; init; }

    /// <summary>The size of the response body in bytes as received from the API.</summary>
    [JsonPropertyOrder(4)]
    public long BodyBytes { get; init; }

    /// <summary>The truncated response body fragment as text, only when <see cref="Truncated"/> is true.</summary>
    [JsonPropertyOrder(5)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BodyText { get; init; }

    [JsonPropertyOrder(6)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Hint { get; init; }

    /// <summary>
    /// Structured guidance of a gateway <c>query</c> or <c>get</c> result that was shortened to
    /// whole items; omitted for bridge tools and for results that fit.
    /// </summary>
    [JsonPropertyOrder(7)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NhAiBridgeTruncation? Truncation { get; init; }
}

/// <summary>
/// Stable failure codes of bridged tool invocations. Failure messages never contain
/// response body text.
/// </summary>
public static class NhAiBridgeFailureCodes
{
    /// <summary>HTTP 400 or 422, or tool input that cannot be mapped to the request.</summary>
    public const string Validation = "api-bridge-validation";

    public const string Unauthenticated = "api-bridge-unauthenticated";

    public const string Forbidden = "api-bridge-forbidden";

    public const string NotFound = "api-bridge-not-found";

    public const string Conflict = "api-bridge-conflict";

    /// <summary>HTTP 5xx, another unexpected status, or a transport failure.</summary>
    public const string Upstream = "api-bridge-upstream";

    public const string Timeout = "api-bridge-timeout";

    /// <summary>
    /// A gateway resource that does not exist or that the user may not use; both are reported the
    /// same way so the gateway never reveals which resources exist.
    /// </summary>
    public const string ResourceNotFound = "ai-tool-not-found";
}

/// <summary>
/// Thrown by <see cref="INhAiBridgeConventions.BuildRequest"/> when the tool input cannot be
/// mapped to a request. The bridge returns it as <see cref="NhAiBridgeFailureCodes.Validation"/>
/// without calling the API. The message must be safe to show to the model and the user.
/// </summary>
public sealed class NhAiBridgeInputException(string message) : Exception(message)
{
}
