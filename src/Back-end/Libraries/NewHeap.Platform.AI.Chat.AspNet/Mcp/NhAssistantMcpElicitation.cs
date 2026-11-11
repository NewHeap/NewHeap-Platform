using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Mcp;

namespace NewHeap.Platform.AI.Chat.AspNet.Mcp;

/// <summary>
/// The assistant as MCP client of administrator-connected servers that ask for input while a tool
/// runs (MCP elicitation), in both forms the protocol has: a multi round-trip <c>input_required</c>
/// result, which the SDK resolves and retries within the call, and an <c>elicitation/create</c> request
/// on the call's own stream. Either asks the person of the governed call in progress
/// (<see cref="NhAssistantElicitationScope"/>) with the server's message attributed to the server.
/// <para>
/// A form may ask for strings, numbers, integers, booleans and single choices, within
/// <see cref="NhAssistantElicitationLimits"/>; a link must be absolute https without user information.
/// Anything else is declined without asking anyone, as is every request without a person to ask: a run,
/// a consultation, a rule turn, a call of an exported tool or a request that arrives outside the call.
/// The server's text never reaches the model, and the answer is never stored.
/// </para>
/// <para>
/// A request waits the server's wait time (<see cref="NhAssistantMcpConnectionPlan.ElicitationWait"/>,
/// five minutes unless an administrator or the application's code sets one to ten), not the tool's own
/// timeout: the clock of the tool's timeout stands still while the person is asked, in either form and
/// for a call the server refuses until a page was finished, and runs on with the time that was left
/// once they answered. The turn's own timeout keeps running and ends the wait at the turn's deadline.
/// </para>
/// </summary>
internal static class NhAssistantMcpElicitation
{
    /// <summary>
    /// The options of a client of <paramref name="plan"/>'s server: it declares that it takes requests
    /// for input in both modes and answers them through <see cref="HandleAsync"/>.
    /// </summary>
    public static McpClientOptions ClientOptions(NhAssistantMcpConnectionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new McpClientOptions
        {
            Capabilities = new ClientCapabilities
            {
                Elicitation = new ElicitationCapability
                {
                    Form = new FormElicitationCapability(),
                    Url = new UrlElicitationCapability()
                }
            },
            Handlers = new McpClientHandlers
            {
                ElicitationHandler = CreateHandler(plan.ServerId, plan.DisplayName ?? plan.ServerId, plan.ElicitationWait)
            }
        };
    }

    /// <summary>
    /// The handler of one server's requests for input with the default wait time; it answers only for
    /// a call of that server.
    /// </summary>
    public static Func<ElicitRequestParams?, CancellationToken, ValueTask<ElicitResult>> CreateHandler(
        string serverId,
        string serverName)
    {
        return CreateHandler(serverId, serverName, NhAssistantElicitationLimits.DefaultWait);
    }

    /// <summary>
    /// The handler of one server's requests for input, which wait for the person at most
    /// <paramref name="wait"/>; it answers only for a call of that server.
    /// </summary>
    public static Func<ElicitRequestParams?, CancellationToken, ValueTask<ElicitResult>> CreateHandler(
        string serverId,
        string serverName,
        TimeSpan wait)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        if (wait <= TimeSpan.Zero || wait > NhAssistantElicitationLimits.MaxWait)
        {
            throw new ArgumentOutOfRangeException(nameof(wait), wait, "A server's wait time is greater than zero and at most ten minutes.");
        }

        var sender = string.IsNullOrWhiteSpace(serverName) ? serverId : serverName;
        return (request, cancellationToken) => HandleAsync(serverId, sender, wait, request, cancellationToken);
    }

    /// <summary>
    /// Asks the person of the governed call of <paramref name="serverId"/> in progress, or declines.
    /// The clock of the call's own timeout stands still while the person is asked.
    /// </summary>
    internal static async ValueTask<ElicitResult> HandleAsync(
        string serverId,
        string serverName,
        TimeSpan wait,
        ElicitRequestParams? request,
        CancellationToken cancellationToken)
    {
        var call = NhAiMcpCallContext.Current;
        var elicitor = NhAssistantElicitationScope.Current;
        if (call is null && NhAiMcpCallContext.SoleCallOf(serverId) is { Flow: { } flow } sole)
        {
            // A request on the call's own stream of an older protocol revision arrives outside the
            // call's flow; it belongs to the call only when that is the server's only call in progress.
            call = sole;
            elicitor = ElicitorIn(flow);
        }
        if (request is null
            || call is null
            || elicitor is null
            || !string.Equals(call.ServerId, serverId, StringComparison.Ordinal))
        {
            // Nobody to ask in this flow: a call of an exported tool, a request outside a call or a
            // request that names another server's call.
            return Declined();
        }

        var (asked, reason) = Read(serverId, serverName, request, wait);
        if (asked is null)
        {
            await elicitor.RecordDeclinedAsync(
                new NhAssistantElicitationRequest(serverId, serverName, ModeOf(request), string.Empty),
                reason!);
            return Declined();
        }

        NhAssistantElicitationOutcome outcome;
        // The person's time does not count against the tool's own timeout; the turn's still does.
        using (call.PauseTimeout())
        {
            outcome = await elicitor.ElicitAsync(asked, cancellationToken);
        }

        if (outcome.Action != NhAssistantElicitationActions.Accept)
        {
            return new ElicitResult { Action = outcome.Action };
        }

        return new ElicitResult
        {
            Action = NhAssistantElicitationActions.Accept,
            Content = asked.Mode == NhAssistantElicitationModes.Form
                ? new Dictionary<string, JsonElement>(outcome.Content ?? new Dictionary<string, JsonElement>(), StringComparer.Ordinal)
                : null
        };
    }

    /// <summary>
    /// The request as the chat may show it, or the reason it may not: a message within its bound, a form
    /// of supported fields within the limits, or an https link without user information.
    /// </summary>
    internal static (NhAssistantElicitationRequest? Request, string? Reason) Read(
        string serverId,
        string serverName,
        ElicitRequestParams request,
        TimeSpan within)
    {
        ArgumentNullException.ThrowIfNull(request);
        var message = request.Message?.Trim();
        if (string.IsNullOrEmpty(message) || message.Length > NhAssistantElicitationLimits.MaxMessageLength)
        {
            return (null, NhAssistantElicitationReasons.Unsupported);
        }

        var mode = ModeOf(request);
        if (mode == NhAssistantElicitationModes.Url)
        {
            var url = SafeUrl(request.Url);
            return url is null
                ? (null, NhAssistantElicitationReasons.Unsupported)
                : (new NhAssistantElicitationRequest(serverId, serverName, mode, message) { Url = url, Within = within }, null);
        }

        var fields = FieldsOf(request.RequestedSchema);
        return fields is null
            ? (null, NhAssistantElicitationReasons.Unsupported)
            : (new NhAssistantElicitationRequest(serverId, serverName, mode, message) { Fields = fields, Within = within }, null);
    }

    private static string ModeOf(ElicitRequestParams request)
    {
        return string.Equals(request.Mode, NhAssistantElicitationModes.Url, StringComparison.Ordinal)
            ? NhAssistantElicitationModes.Url
            : NhAssistantElicitationModes.Form;
    }

    /// <summary>
    /// The link of a <c>url</c> request when it is absolute https, without user information and within
    /// its bound; <see langword="null"/> otherwise.
    /// </summary>
    internal static string? SafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || url.Length > NhAssistantElicitationLimits.MaxUrlLength
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }

        return uri.AbsoluteUri;
    }

    /// <summary>
    /// The fields of a form, or <see langword="null"/> when it asks for anything the chat does not show:
    /// more fields than the limit, a nested or multiple-choice value, or text beyond the bounds.
    /// </summary>
    private static List<NhAssistantElicitationField>? FieldsOf(ElicitRequestParams.RequestSchema? schema)
    {
        if (schema?.Properties is not { Count: > 0 and <= NhAssistantElicitationLimits.MaxFields } properties)
        {
            return null;
        }

        var required = schema.Required ?? [];
        var fields = new List<NhAssistantElicitationField>(properties.Count);
        foreach (var (name, definition) in properties)
        {
            if (string.IsNullOrWhiteSpace(name)
                || name.Length > NhAssistantElicitationLimits.MaxFieldNameLength
                || definition is null)
            {
                return null;
            }

            var title = string.IsNullOrWhiteSpace(definition.Title) ? name : definition.Title.Trim();
            var description = string.IsNullOrWhiteSpace(definition.Description) ? null : definition.Description.Trim();
            if (title.Length > NhAssistantElicitationLimits.MaxTitleLength
                || description?.Length > NhAssistantElicitationLimits.MaxDescriptionLength)
            {
                return null;
            }

            var field = FieldOf(name, title, required.Contains(name), definition);
            if (field is null)
            {
                return null;
            }

            fields.Add(field with { Description = description });
        }

        return fields;
    }

    private static NhAssistantElicitationField? FieldOf(
        string name,
        string title,
        bool required,
        ElicitRequestParams.PrimitiveSchemaDefinition definition)
    {
        switch (definition)
        {
            case ElicitRequestParams.StringSchema text:
                if (text.MinLength is < 0 or > NhAssistantElicitationLimits.MaxStringLength
                    || text.MaxLength is < 0)
                {
                    return null;
                }

                return new NhAssistantElicitationField(name, NhAssistantElicitationFieldTypes.String, title, required)
                {
                    MinLength = text.MinLength,
                    MaxLength = text.MaxLength is { } max ? Math.Min(max, NhAssistantElicitationLimits.MaxStringLength) : null
                };
            case ElicitRequestParams.NumberSchema number:
                return new NhAssistantElicitationField(
                    name,
                    string.Equals(number.Type, NhAssistantElicitationFieldTypes.Integer, StringComparison.Ordinal)
                        ? NhAssistantElicitationFieldTypes.Integer
                        : NhAssistantElicitationFieldTypes.Number,
                    title,
                    required)
                {
                    Minimum = number.Minimum,
                    Maximum = number.Maximum
                };
            case ElicitRequestParams.BooleanSchema:
                return new NhAssistantElicitationField(name, NhAssistantElicitationFieldTypes.Boolean, title, required);
            case ElicitRequestParams.UntitledSingleSelectEnumSchema choice:
                return EnumField(name, title, required, choice.Enum?.Select(value => (value, value)).ToArray());
            case ElicitRequestParams.TitledSingleSelectEnumSchema choice:
                return EnumField(name, title, required, choice.OneOf?.Select(option => (option.Const, option.Title ?? option.Const)).ToArray());
#pragma warning disable MCP9001 // The titled enum of the 2025-06-18 revision is still sent by older servers.
            case ElicitRequestParams.LegacyTitledEnumSchema choice:
                return EnumField(
                    name,
                    title,
                    required,
                    choice.Enum?.Select((value, index) => (value, choice.EnumNames is { } names && index < names.Count ? names[index] : value)).ToArray());
#pragma warning restore MCP9001
            default:
                // A multiple choice or any other kind of value is not asked in the chat.
                return null;
        }
    }

    private static NhAssistantElicitationField? EnumField(
        string name,
        string title,
        bool required,
        (string Value, string Label)[]? options)
    {
        if (options is not { Length: > 0 and <= NhAssistantElicitationLimits.MaxOptions }
            || options.Any(option => string.IsNullOrEmpty(option.Value)
                || option.Value.Length > NhAssistantElicitationLimits.MaxOptionLength
                || string.IsNullOrWhiteSpace(option.Label)
                || option.Label.Length > NhAssistantElicitationLimits.MaxOptionLength)
            || options.Select(option => option.Value).Distinct(StringComparer.Ordinal).Count() != options.Length)
        {
            return null;
        }

        return new NhAssistantElicitationField(name, NhAssistantElicitationFieldTypes.Enum, title, required)
        {
            Options = options.Select(option => new NhAssistantElicitationOption(option.Value, option.Label.Trim())).ToArray()
        };
    }

    /// <summary>
    /// The elicitor of the call whose flow <paramref name="flow"/> is.
    /// </summary>
    private static INhAssistantElicitor? ElicitorIn(ExecutionContext flow)
    {
        INhAssistantElicitor? found = null;
        ExecutionContext.Run(flow.CreateCopy(), _ => found = NhAssistantElicitationScope.Current, null);
        return found;
    }

    private static ElicitResult Declined()
    {
        return new ElicitResult { Action = NhAssistantElicitationActions.Decline };
    }
}
