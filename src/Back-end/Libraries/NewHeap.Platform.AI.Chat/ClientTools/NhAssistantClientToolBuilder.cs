namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Declares a client tool (<see cref="NhAssistantBuilder.AddClientTool{TInput}"/>): a tool the agent calls
/// that runs in the browser of the person whose turn it is, in the application's own interface, instead of
/// on the server. The application declares it here, on the server: its id, description, input and the
/// policies of the person who may use it, so the model only ever reads the application's own description.
/// The browser registers a handler per tool id (<c>provideNhAssistantClientTools</c> of
/// <c>@newheap/platform-ai-chat</c>) and reports what happened; the model reads that report as the call's
/// result.
/// <para>
/// A client tool changes nothing on the server. Its call runs through the shared invoker like any read:
/// the invocation gate authorizes the person and the agent's selectors, and the tool budget and the audit
/// count it. Whatever the person then does in the interface, such as saving a prefilled form, goes through
/// the application's normal API and authorization.
/// </para>
/// </summary>
/// <typeparam name="TInput">
/// The arguments the model passes, bound from the call's JSON with the web defaults of
/// <see cref="System.Text.Json"/>, required constructor parameters and nullable annotations respected; its
/// JSON schema is the tool's input schema. The browser receives the bound arguments, never the model's raw
/// text.
/// </typeparam>
public sealed class NhAssistantClientToolBuilder<TInput>
    where TInput : class
{
    private readonly List<string> _policies = [];
    private string? _description;
    private string? _exportName;
    private int _version = 1;
    private bool _handsOffChange;
    private Type? _resultType;

    internal NhAssistantClientToolBuilder(string id)
    {
        Id = id;
    }

    /// <summary>The client tool's id.</summary>
    public string Id { get; }

    /// <summary>
    /// What the tool does in the application's interface, as the model reads it. Required; at most
    /// <see cref="NhAssistantClientToolLimits.MaxDescriptionLength"/> characters. The assistant adds that the
    /// tool runs in the person's browser and, for <see cref="HandsOffChange"/>, when to prefer it over a
    /// proposal.
    /// </summary>
    public NhAssistantClientToolBuilder<TInput> Describe(string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (description.Length > NhAssistantClientToolLimits.MaxDescriptionLength)
        {
            throw new ArgumentException(
                $"A client tool's description is at most {NhAssistantClientToolLimits.MaxDescriptionLength} characters.",
                nameof(description));
        }

        _description = description;
        return this;
    }

    /// <summary>
    /// The version of the tool's contract; 1 by default. Raise it when its input or result changes.
    /// </summary>
    public NhAssistantClientToolBuilder<TInput> Version(int version)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        _version = version;
        return this;
    }

    /// <summary>
    /// The function name the model calls the tool by; by default its id with underscores and its version,
    /// such as <c>projects_open_v1</c>.
    /// </summary>
    public NhAssistantClientToolBuilder<TInput> ExportAs(string functionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(functionName);
        if (functionName.Length > 64 || !functionName.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-'))
        {
            throw new ArgumentException(
                "A client tool's function name is at most 64 ASCII letters, digits, underscores or dashes.",
                nameof(functionName));
        }

        _exportName = functionName;
        return this;
    }

    /// <summary>
    /// An authorization policy the person must satisfy for the agent to call the tool, checked by the
    /// invocation gate as for any governed tool; call it again for more policies, all of which apply. Use
    /// the policy of what the interface lets the person do, such as the policy of creating a task, so the
    /// agent never offers a form the person cannot save. The application's API still authorizes the save.
    /// </summary>
    public NhAssistantClientToolBuilder<TInput> RequirePolicy(string policyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        if (!_policies.Contains(policyName, StringComparer.Ordinal))
        {
            _policies.Add(policyName);
        }

        return this;
    }

    /// <summary>
    /// The tool hands a change to the person: it opens the application's own form, prefilled, which the
    /// person reviews and saves themselves. Such a tool is offered only to an agent whose model is offered
    /// changes in a person's chat (execute autonomy), exactly where the agent could propose the same
    /// change, and its description tells the model to prefer it over a proposal while the person takes
    /// part. Without it the tool only shows something, such as a page, and is offered as a read.
    /// </summary>
    public NhAssistantClientToolBuilder<TInput> HandsOffChange()
    {
        _handsOffChange = true;
        return this;
    }

    /// <summary>
    /// The data the browser may report next to the outcome, bound to <typeparamref name="TResult"/> with the
    /// same JSON rules as the input; properties it does not declare are dropped, and data that does not
    /// bind is refused with <see cref="NhAssistantErrorCodes.ClientToolResultInvalid"/>. Without it the
    /// model reads the outcome and the optional id only, never data of the application's records.
    /// </summary>
    /// <typeparam name="TResult">The data the browser reports, such as the id of the record the person saved.</typeparam>
    public NhAssistantClientToolBuilder<TInput> Returns<TResult>()
        where TResult : class
    {
        _resultType = typeof(TResult);
        return this;
    }

    internal NhAssistantClientToolDefinition Build()
    {
        if (_description is null)
        {
            throw new InvalidOperationException($"The client tool '{Id}' needs a description (Describe).");
        }

        return new NhAssistantClientToolDefinition(
            Id,
            _version,
            _description,
            _exportName ?? NhAssistantCompositionDefinition.DefaultExportName(Id, _version),
            typeof(TInput),
            _policies.ToArray(),
            _handsOffChange,
            _resultType);
    }
}

/// <summary>
/// A client tool as the application declared it.
/// </summary>
internal sealed record NhAssistantClientToolDefinition(
    string Id,
    int Version,
    string Description,
    string ExportName,
    Type InputType,
    IReadOnlyList<string> Policies,
    bool HandsOffChange,
    Type? ResultType);
