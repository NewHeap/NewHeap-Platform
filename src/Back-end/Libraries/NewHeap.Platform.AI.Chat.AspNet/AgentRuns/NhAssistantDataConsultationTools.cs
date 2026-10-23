using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// The generated consult and request tools of consultations defined as data
/// (<c>assistant-consultations-data</c>, AC7). The catalog follows the run configuration in effect: it
/// builds a view per snapshot, attested on its own, and announces a change of its tool list. Its
/// functions bind late, so a call after a consultation was removed or its contract changed fails with
/// <c>assistant-consult-agent-unavailable</c> (AC12). MCP clients see a consultation's tools only when
/// the ceiling allows MCP and the consultation asks for it (AC8). The catalog of consultations in code
/// stays static.
/// </summary>
internal sealed class NhAssistantDataConsultationToolCatalog : INhAiChangingToolCatalog, INhAssistantRunSnapshotListener
{
    public const string CatalogId = "assistant-consultations-data";

    private readonly NhAssistantRegistrationState _registration;
    private readonly NhAssistantOptions _options;
    private readonly NhAssistantRunConfigurations _configurations;
    private readonly IServiceProvider _services;
    private readonly ILogger<NhAssistantDataConsultationToolCatalog> _logger;
    private readonly object _sync = new();
    private Built _built;
    private string _announced;

    public NhAssistantDataConsultationToolCatalog(
        NhAssistantRegistrationState registration,
        IOptions<NhAssistantOptions> options,
        NhAssistantRunConfigurations configurations,
        IServiceProvider services,
        ILogger<NhAssistantDataConsultationToolCatalog> logger)
    {
        _registration = registration;
        _options = options.Value;
        _configurations = configurations;
        _services = services;
        _logger = logger;
        _built = new Built(null, NhAssistantConsultationCatalogView.Empty(CatalogId));
        _announced = _built.View.ListingHash;
    }

    public event EventHandler? Changed;

    public INhAiAttestedToolCatalog Current => ViewOf(_registration.Snapshot);

    /// <summary>
    /// The view in effect, with the consultation of every tool.
    /// </summary>
    internal NhAssistantConsultationCatalogView CurrentView => ViewOf(_registration.Snapshot);

    public bool MayExportToMcp => _registration.ConsultationsAsData?.AllowMcp == true;

    public NhAiToolCatalogGovernance Governance => NhAiToolCatalogGovernance.SharedInvoker;

    public IReadOnlyList<NhAiToolDescriptor> Descriptors => CurrentView.Descriptors;

    public NhAiToolCatalogManifest Manifest => CurrentView.Manifest;

    public string AttestationHash => CurrentView.AttestationHash;

    public IReadOnlyList<AIFunction> CreateFunctions(IServiceProvider services)
    {
        return CurrentView.CreateFunctions(services);
    }

    /// <summary>
    /// Reads the run configuration stamp at most once per check interval (AC23), so a listing on a
    /// host that only chats sees a change another host made.
    /// </summary>
    public async ValueTask RefreshAsync(CancellationToken cancellationToken)
    {
        await _configurations.EnsureCurrentAsync(cancellationToken);
    }

    /// <summary>
    /// The consultation a data tool asks, as it is in effect now, when it still has the contract the
    /// caller's function was built with; <see langword="null"/> otherwise.
    /// </summary>
    public NhAssistantConsultationOptions? Resolve(string agentId, string subjectType, bool request, string contractHash)
    {
        var toolId = request
            ? NhAssistantConsultationTools.RequestToolId(agentId, subjectType)
            : NhAssistantConsultationTools.ConsultToolId(agentId, subjectType);
        return CurrentView.Tools.TryGetValue(toolId, out var tool)
               && tool.IsRequest == request
               && string.Equals(tool.ContractHash, contractHash, StringComparison.Ordinal)
            ? tool.Options
            : null;
    }

    public Task AppliedAsync(NhAssistantRunSnapshot snapshot, CancellationToken cancellationToken)
    {
        // Announced once per listing, also when a reader built the view of this snapshot first.
        var after = ViewOf(snapshot).ListingHash;
        var before = Interlocked.Exchange(ref _announced, after);
        if (!string.Equals(before, after, StringComparison.Ordinal))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return Task.CompletedTask;
    }

    private NhAssistantConsultationCatalogView ViewOf(NhAssistantRunSnapshot? snapshot)
    {
        var built = Volatile.Read(ref _built);
        if (ReferenceEquals(built.Snapshot, snapshot) || snapshot is null)
        {
            return built.View;
        }

        lock (_sync)
        {
            built = _built;
            if (ReferenceEquals(built.Snapshot, snapshot))
            {
                return built.View;
            }

            var view = Build(snapshot);
            Volatile.Write(ref _built, new Built(snapshot, view));
            return view;
        }
    }

    private NhAssistantConsultationCatalogView Build(NhAssistantRunSnapshot snapshot)
    {
        var view = NhAssistantConsultationCatalogView.Create(
            CatalogId,
            snapshot.AgentRuns.Values
                .SelectMany(run => run.Consultations)
                .Where(consultation => consultation.IsData),
            consultation => NhAssistantConsultationCatalogView.PoliciesOf(_registration, _options, consultation),
            lateBinding: true);
        try
        {
            // A view enters chats, agents and the MCP export only once its own attestation holds.
            NhAiToolCatalogAttestation.Validate(view, _services);
            return view;
        }
        catch (InvalidOperationException exception)
        {
            _logger.LogError(
                "The tools of consultations defined as data did not pass their attestation ({Message}); none are offered until the next change.",
                exception.Message);
            return NhAssistantConsultationCatalogView.Empty(CatalogId);
        }
    }

    private sealed record Built(NhAssistantRunSnapshot? Snapshot, NhAssistantConsultationCatalogView View);
}

/// <summary>
/// The tool ids and export names of every other tool catalog, which a consultation defined as data may
/// not take (<c>conflicts-with-tool</c>). Catalogs registered at startup do not change, so they are read
/// once.
/// </summary>
internal sealed class NhAssistantReservedToolNames(IServiceScopeFactory scopeFactory) : INhAssistantReservedToolNames
{
    private readonly object _sync = new();
    private (HashSet<string> Ids, HashSet<string> ExportNames)? _names;

    public bool IsReserved(string toolId, string exportName)
    {
        var names = Names();
        return names.Ids.Contains(toolId) || names.ExportNames.Contains(exportName);
    }

    private (HashSet<string> Ids, HashSet<string> ExportNames) Names()
    {
        lock (_sync)
        {
            if (_names is { } known)
            {
                return known;
            }

            using var scope = scopeFactory.CreateScope();
            var descriptors = scope.ServiceProvider.GetServices<INhAiToolCatalog>()
                .Where(catalog => catalog is not INhAiChangingToolCatalog)
                .SelectMany(catalog => catalog.Descriptors)
                .ToArray();
            var names = (
                descriptors.Select(descriptor => descriptor.Id).ToHashSet(StringComparer.Ordinal),
                descriptors.Select(descriptor => descriptor.ExportName).ToHashSet(StringComparer.Ordinal));
            _names = names;
            return names;
        }
    }
}
