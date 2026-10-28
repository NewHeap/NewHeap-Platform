using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Mcp;

namespace NewHeap.Platform.AI.Scripting;

/// <summary>
/// The scripting settings of a host: the limits of scripts per agent and of the MCP exposure, and the
/// destructive tools that opted in to scripts. Whether an agent or the MCP exposure is offered the script
/// tool at all is decided elsewhere: in code by the agent's <c>AllowScripts()</c> and the exposure's
/// <c>AllowScripts()</c>, at run time by the application's composition switch.
/// </summary>
public sealed class NhAiScriptingOptions
{
    private readonly Dictionary<string, NhAiScriptLimits> _agents = new(StringComparer.Ordinal);
    private readonly HashSet<string> _destructive = new(StringComparer.Ordinal);

    /// <summary>The limits of an agent without limits of its own. Read-only scripts by default.</summary>
    public NhAiScriptLimits Defaults { get; } = new();

    /// <summary>The limits of scripts that MCP clients run. Read-only scripts by default.</summary>
    public NhAiScriptLimits Mcp { get; } = new();

    /// <summary>The agents with limits of their own.</summary>
    public IReadOnlyCollection<string> AgentsWithLimits => _agents.Keys;

    /// <summary>The limits of <paramref name="agentId"/>'s scripts: its own, otherwise <see cref="Defaults"/>.</summary>
    public NhAiScriptLimits LimitsOf(string? agentId)
    {
        return agentId is not null && _agents.TryGetValue(agentId, out var limits) ? limits : Defaults;
    }

    /// <summary>
    /// Whether the destructive tool <paramref name="toolId"/> opted in to scripts; without it, a script
    /// never calls the tool.
    /// </summary>
    public bool AllowsDestructive(string toolId)
    {
        return _destructive.Contains(toolId);
    }

    internal NhAiScriptLimits GetOrAddAgent(string agentId)
    {
        if (!_agents.TryGetValue(agentId, out var limits))
        {
            limits = Defaults.Clone();
            _agents.Add(agentId, limits);
        }

        return limits;
    }

    internal void AllowDestructive(string toolId)
    {
        _destructive.Add(toolId);
    }

    internal void Validate()
    {
        Defaults.Validate();
        Mcp.Validate();
        foreach (var limits in _agents.Values)
        {
            limits.Validate();
        }
    }
}

/// <summary>
/// Configures the script tool of a host (<see cref="NhAiScriptingServiceCollectionExtensions.AddNewHeapPlatformAIScripting"/>).
/// </summary>
public sealed class NhAiScriptingBuilder
{
    private readonly NhAiScriptingOptions _options;

    internal NhAiScriptingBuilder(NhAiScriptingOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// Sets the limits of agents without limits of their own. Call it before <see cref="ConfigureAgent"/>,
    /// whose limits start as a copy of these.
    /// </summary>
    public NhAiScriptingBuilder ConfigureDefaults(Action<NhAiScriptLimits> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_options.Defaults);
        _options.Defaults.Validate();
        return this;
    }

    /// <summary>
    /// Sets the limits of one agent's scripts, for example
    /// <c>limits =&gt; limits.MaxMutationsPerScript = 2</c> to let its scripts make two calls that change
    /// data. The agent still needs <c>AllowScripts()</c> in its agent context to be offered the tool.
    /// </summary>
    public NhAiScriptingBuilder ConfigureAgent(string agentId, Action<NhAiScriptLimits> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(configure);
        var limits = _options.GetOrAddAgent(agentId);
        configure(limits);
        limits.Validate();
        return this;
    }

    /// <summary>
    /// Sets the limits of the scripts MCP clients run. The exposure still needs
    /// <c>WithNewHeapPlatformAITools(mcp =&gt; mcp.AllowScripts())</c> to list the tool.
    /// </summary>
    public NhAiScriptingBuilder ConfigureMcp(Action<NhAiScriptLimits> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_options.Mcp);
        _options.Mcp.Validate();
        return this;
    }

    /// <summary>
    /// Lets the named destructive tools run in scripts that may change data. A destructive tool is never
    /// called by a script without this opt-in; its calls still get the decision they get on their own.
    /// </summary>
    public NhAiScriptingBuilder AllowDestructive(params string[] toolIds)
    {
        ArgumentNullException.ThrowIfNull(toolIds);
        foreach (var toolId in toolIds)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
            _options.AllowDestructive(toolId);
        }

        return this;
    }
}

public static class NhAiScriptingServiceCollectionExtensions
{
    /// <summary>
    /// Adds the script tool, <c>run_script</c>: its catalog for agents, which the assistant offers to every
    /// agent whose context allows scripts while the application's switch has them on, and its tool for the
    /// MCP export, listed when the exposure allows scripts and the switch has them on. Each script runs in
    /// a fresh sandboxed engine with the agent's or the exposure's limits and calls only the tools of its
    /// turn or request. Calling it again adds to the same settings.
    /// </summary>
    public static IServiceCollection AddNewHeapPlatformAIScripting(
        this IServiceCollection services,
        Action<NhAiScriptingBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = services
            .Where(descriptor => descriptor.ServiceType == typeof(NhAiScriptingOptions))
            .Select(descriptor => descriptor.ImplementationInstance)
            .OfType<NhAiScriptingOptions>()
            .FirstOrDefault();
        if (options is null)
        {
            options = new NhAiScriptingOptions();
            services.AddSingleton(options);
        }

        configure?.Invoke(new NhAiScriptingBuilder(options));
        options.Validate();

        services.AddNewHeapPlatformAI();
        services.TryAddSingleton<NhAiScriptRunner>();
        services.TryAddSingleton<NhAiScriptToolCatalog>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INhAiToolCatalog, NhAiScriptToolCatalog>(
            provider => provider.GetRequiredService<NhAiScriptToolCatalog>()));
        services.AddNewHeapPlatformAIMcpToolContributor<NhAiScriptMcpToolContributor>();
        return services;
    }
}

/// <summary>
/// The catalog of the script tool for agents: one descriptor, marked
/// <see cref="NhAiCompositionFeature.Scripts"/>, whose function describes the tools of the current turn
/// (<see cref="NhAssistantToolStepTurn"/>) and runs its scripts with the limits of the turn's agent.
/// </summary>
internal sealed class NhAiScriptToolCatalog : INhAiAttestedToolCatalog
{
    public NhAiScriptToolCatalog()
    {
        var descriptor = NhAiScriptTool.Descriptor;
        Descriptors = [descriptor];
        var material = descriptor.Id + "@1:" + descriptor.ContractHash;
        AttestationHash = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material)));
        Manifest = new NhAiToolCatalogManifest(
            NhAiScriptTool.CatalogId,
            1,
            AttestationHash,
            [new NhAiToolManifestEntry(descriptor.Id, descriptor.Version, descriptor.SchemaHash, descriptor.ContractHash) { ExportName = descriptor.ExportName }]);
    }

    public NhAiToolCatalogGovernance Governance => NhAiToolCatalogGovernance.SharedInvoker;

    public IReadOnlyList<NhAiToolDescriptor> Descriptors { get; }

    public NhAiToolCatalogManifest Manifest { get; }

    public string AttestationHash { get; }

    public IReadOnlyList<AIFunction> CreateFunctions(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = services.GetService<NhAiScriptingOptions>() ?? new NhAiScriptingOptions();
        var function = new NhAiScriptFunction(
            services,
            NhAiScriptTool.Descriptor,
            () => NhAssistantToolStepTurn.Current(NhAiScriptTool.Descriptor) is { } turn
                ? new NhAiScriptScope(turn.Tools, options.LimitsOf(turn.AgentId))
                : new NhAiScriptScope(null, options.Defaults));
        return [NhAiGovernedAIFunction.Create(NhAiScriptTool.Descriptor, function, services)];
    }
}
