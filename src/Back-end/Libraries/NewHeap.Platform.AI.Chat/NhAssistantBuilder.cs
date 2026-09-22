using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.Chat.Persistence;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Configures the assistant registered by <c>AddNewHeapAssistant</c>. Choose one storage provider
/// with <c>UseSqlServer</c> or <c>UsePostgreSql</c>.
/// </summary>
public sealed class NhAssistantBuilder
{
    private readonly NhAssistantRegistrationState _state;

    internal NhAssistantBuilder(IServiceCollection services, NhAssistantRegistrationState state)
    {
        Services = services;
        _state = state;
    }

    /// <summary>
    /// The service collection the assistant is registered in.
    /// </summary>
    public IServiceCollection Services { get; }

    /// <summary>
    /// Sets the authorization policy every assistant endpoint requires. Overrides
    /// <c>NewHeap:AI:Assistant:AccessPolicy</c>; the default is <c>app.assistant.access</c>.
    /// </summary>
    public NhAssistantBuilder UseAccessPolicy(string policyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        if (policyName.Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(policyName));
        }
        _state.AccessPolicy = policyName;
        return this;
    }

    /// <summary>
    /// Declares the chat profile the assistant requires. Startup fails when the profile is not
    /// registered or cannot call functions and stream.
    /// </summary>
    public NhAssistantBuilder UseChatProfile(string profileName)
    {
        NhAssistantNames.ValidateSegment(profileName, nameof(profileName));
        _state.ChatProfileName = profileName;
        return this;
    }

    /// <summary>
    /// Adds an agent. Adding the same definition twice is idempotent; a different definition
    /// with the same id and version is rejected.
    /// </summary>
    public NhAssistantBuilder AddAgent(NhAssistantAgentDefinition agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        _state.AddAgent(agent);
        return this;
    }

    /// <summary>
    /// Adds a consumer audit sink that receives content-free assistant events.
    /// </summary>
    public NhAssistantBuilder AddBusinessAuditSink<TSink>()
        where TSink : class, INhAssistantBusinessAuditSink
    {
        Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<INhAssistantBusinessAuditSink, TSink>());
        return this;
    }

    /// <summary>
    /// Adds an optional presenter for localized, user-facing tool names and approval explanations.
    /// Presenters cannot alter authorization, proposal evidence or approval decisions.
    /// </summary>
    public NhAssistantBuilder AddToolPresenter<TPresenter>()
        where TPresenter : class, INhAssistantToolPresenter
    {
        Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<INhAssistantToolPresenter, TPresenter>());
        return this;
    }

    /// <summary>
    /// Replaces the default title generator, which uses the first line of the first message.
    /// </summary>
    public NhAssistantBuilder UseTitleGenerator<TGenerator>()
        where TGenerator : class, INhAssistantTitleGenerator
    {
        Services.Replace(ServiceDescriptor.Scoped<INhAssistantTitleGenerator, TGenerator>());
        return this;
    }

    /// <summary>
    /// Configures turn limits, approval lifetime and durable daily budgets.
    /// </summary>
    public NhAssistantBuilder WithLimits(Action<NhAssistantLimits> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var limits = _state.Limits.Clone();
        configure(limits);
        limits.Validate();
        _state.Limits = limits;
        return this;
    }

    /// <summary>
    /// Sets the authorization policy the administration endpoints require in addition to the
    /// access policy. Overrides <c>NewHeap:AI:Assistant:AdminPolicy</c> (default <c>app.assistant.admin</c>).
    /// </summary>
    public NhAssistantBuilder UseAdminPolicy(string policyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        if (policyName.Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(policyName));
        }
        _state.AdminPolicy = policyName;
        return this;
    }

    /// <summary>
    /// Seeds the application context as version 1 when no context is stored yet. A changed seed never
    /// replaces a context that already exists, including one an administrator edited.
    /// </summary>
    public NhAssistantBuilder UseDefaultApplicationContext(NhAiTextAsset context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.Content)
            || context.Content.Length > NhAssistantApplicationContexts.MaxTextLength)
        {
            throw new ArgumentException(
                "The default application context must contain at most 20,000 characters.",
                nameof(context));
        }
        _state.DefaultApplicationContext = context;
        return this;
    }

    /// <summary>
    /// Configures the connection rules for administrator-connected MCP servers. Applied after the
    /// <c>NewHeap:AI:Assistant:Mcp</c> configuration.
    /// </summary>
    public NhAssistantBuilder ConfigureMcp(Action<NhAssistantMcpOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Services.PostConfigure<NhAssistantOptions>(options => configure(options.Mcp));
        return this;
    }

    /// <summary>
    /// Sets the execution region every agent model call must be permitted to run in. Defaults to
    /// <c>local</c>, the NewHeap agent default; the chat profile must permit the region.
    /// </summary>
    public NhAssistantBuilder UseExecutionRegion(string region)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        if (region.Length > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(region));
        }
        _state.ExecutionRegion = region;
        return this;
    }

    /// <summary>
    /// Adds a provider of situational facts (such as the user's name, roles or active division) for
    /// the "Situation" block of every turn. Several providers are allowed; they run in registration
    /// order. A failing provider is skipped for the turn.
    /// </summary>
    public NhAssistantBuilder UseTurnContextProvider<TProvider>()
        where TProvider : class, INhAssistantTurnContextProvider
    {
        Services.TryAddEnumerable(ServiceDescriptor.Scoped<INhAssistantTurnContextProvider, TProvider>());
        return this;
    }

    /// <summary>
    /// Sets the time zone of the date, weekday and time the library adds to every turn, such as
    /// <c>Europe/Amsterdam</c>. The default is UTC.
    /// </summary>
    public NhAssistantBuilder UseTimeZone(string timeZoneId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);
        _state.TimeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        return this;
    }

    internal NhAssistantBuilder UseStorage(NhAssistantStorageRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        NhAssistantStorage.Register(Services, registration);
        _state.StorageProvider = registration.ProviderName;
        return this;
    }
}

/// <summary>
/// Registration state shared by every <c>AddNewHeapAssistant</c> call in one service collection.
/// </summary>
internal sealed class NhAssistantRegistrationState
{
    private readonly Dictionary<string, NhAssistantAgentDefinition> _agents = new(StringComparer.Ordinal);

    public string? AccessPolicy { get; set; }

    public string? ChatProfileName { get; set; }

    public string? StorageProvider { get; set; }

    public string ExecutionRegion { get; set; } = "local";

    public string? AdminPolicy { get; set; }

    public NhAiTextAsset? DefaultApplicationContext { get; set; }

    public NhAssistantLimits Limits { get; set; } = new();

    public TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.Utc;

    public IReadOnlyCollection<NhAssistantAgentDefinition> Agents => _agents.Values;

    public void AddAgent(NhAssistantAgentDefinition agent)
    {
        NhAssistantAgentDefinition.ValidateShape(agent);
        if (_agents.TryGetValue(agent.Id, out var existing))
        {
            if (existing.IsEquivalentTo(agent))
            {
                return;
            }
            throw new InvalidOperationException(
                $"Assistant agent '{agent.Id}' is already registered with a different definition.");
        }
        _agents.Add(agent.Id, agent);
    }
}
