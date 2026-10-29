using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Mcp;

namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>
/// The links between applications that the application reviewed and stores (FB59): the MCP callers of
/// other applications whose consultation chains it continues, and the context bindings of tools on
/// administrator-connected MCP servers. They count next to the code registrations
/// (<see cref="NhAssistantMcpContext.ContinueMcpChain"/> and <see cref="NhAssistantMcpContext.AddMcpContextBinding"/>),
/// so a link the application creates or revokes while the host runs counts without a restart.
/// </summary>
/// <remarks>
/// <para>
/// Register one with <see cref="NhAssistantMcpContext.UseMcpLinks{TLinks}"/>; it is a scoped service that
/// the assistant resolves in a scope of its own, never in a person's request, so it answers with the
/// application's links and nothing that depends on the request. The assistant reuses an answer for
/// <see cref="NhAssistantMcpOptions.LinkCacheDuration"/> (30 seconds by default): a link counts, and a
/// revoked one stops, within that time.
/// </para>
/// <para>
/// The application owns who may create a link, for example an administrator screen with its own permission
/// and review; NewHeap owns what a link may do. It checks every entry exactly as it checks the same
/// registration in code at startup and leaves out an entry such a registration would refuse, with a warning
/// that names the reason (<see cref="NhAssistantMcpLinkProblems"/>) when the reason first appears. An entry
/// never grants more than a registration could. A stored binding applies only to a tool of an
/// administrator-connected server while the server keeps the binding's id and URL, never to a code-owned
/// import, which keeps its code binding; a server and tool that code binds keep their code binding. A
/// stored caller is believed only for a request whose authenticated principal carries its issuer and,
/// when it names one, its client, and a caller that a code registration names as another application
/// stays that application.
/// </para>
/// <para>
/// It reads and changes nothing and answers quickly. A source that throws or does not answer within
/// <see cref="NhAssistantMcpLinkList.ReadTimeLimit"/> is logged with its type and the exception's type, and
/// until the next read only the code registrations count: stored links fail closed.
/// </para>
/// </remarks>
public interface INhAssistantMcpLinks
{
    /// <summary>
    /// The chain callers and context bindings the application reviewed and stores now.
    /// </summary>
    /// <param name="cancellationToken">Ends the read; the assistant also ends it after
    /// <see cref="NhAssistantMcpLinkList.ReadTimeLimit"/>.</param>
    ValueTask<NhAssistantMcpLinkList> ListAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The links an <see cref="INhAssistantMcpLinks"/> stores: chain callers as
/// <see cref="NhAssistantMcpContext.ContinueMcpChain"/> takes them, and context bindings as
/// <see cref="NhAssistantMcpContext.AddMcpContextBinding"/> takes them.
/// </summary>
/// <param name="ChainCallers">The MCP callers of other applications whose consultation chains this
/// application continues. They need the application's name in chains
/// (<see cref="NhAssistantMcpContext.UseMcpChainApplication"/>).</param>
/// <param name="ContextBindings">The reviewed tools of administrator-connected MCP servers that receive
/// the assistant context.</param>
public sealed record NhAssistantMcpLinkList(
    IReadOnlyList<NhAssistantMcpChainCaller> ChainCallers,
    IReadOnlyList<NhAssistantMcpContextBinding> ContextBindings)
{
    /// <summary>
    /// The most entries of each list that count; the assistant leaves out the rest.
    /// </summary>
    public const int MaxEntries = 1_000;

    /// <summary>
    /// The longest the assistant waits for an answer; a later answer is a failed read.
    /// </summary>
    public static readonly TimeSpan ReadTimeLimit = TimeSpan.FromSeconds(10);

    /// <summary>
    /// No stored links: only the code registrations count.
    /// </summary>
    public static NhAssistantMcpLinkList Empty { get; } = new([], []);
}

/// <summary>
/// Why the assistant left out an entry of the application's stored MCP links
/// (<see cref="INhAssistantMcpLinks"/>). The warning names the reason, the kind of entry, its position and,
/// when it is valid, its application or server id.
/// </summary>
public static class NhAssistantMcpLinkProblems
{
    /// <summary>The entry is <see langword="null"/>.</summary>
    public const string Missing = "entry-missing";

    /// <summary>The list has more than <see cref="NhAssistantMcpLinkList.MaxEntries"/> entries.</summary>
    public const string TooMany = "too-many-entries";

    /// <summary>The caller's name in chains is no application id (<see cref="NhAssistantMcpContext.IsApplicationId"/>).</summary>
    public const string ApplicationIdInvalid = "application-id-invalid";

    /// <summary>The caller has no issuer or issuer claim type, or one longer than 256 characters.</summary>
    public const string IssuerInvalid = "issuer-invalid";

    /// <summary>The caller names its client with only one of the client claim type and the client id, or one longer than 256 characters.</summary>
    public const string ClientInvalid = "client-invalid";

    /// <summary>A caller, or a binding with <see cref="NhAssistantMcpContextBinding.IncludeChain"/>, while the application has no name in chains.</summary>
    public const string ChainApplicationMissing = "chain-application-missing";

    /// <summary>The caller takes the application's own name in chains.</summary>
    public const string OwnApplicationName = "own-application-name";

    /// <summary>The caller's issuer and client are reviewed for another application, in code or in another stored entry.</summary>
    public const string CallerConflict = "caller-conflict";

    /// <summary>The binding's server URL is not an absolute http or https URL without user information.</summary>
    public const string ServerUrlInvalid = "server-url-invalid";

    /// <summary>The binding's server id is not dash-case, or its remote tool name is empty or longer than 128 characters.</summary>
    public const string ToolInvalid = "tool-invalid";

    /// <summary>The binding has no snapshot request, or its bounds are outside the hard limits.</summary>
    public const string SnapshotInvalid = "snapshot-invalid";

    /// <summary>Code binds the same server id and tool; the code binding applies.</summary>
    public const string CodeBinding = "code-binding";

    /// <summary>Another stored binding names the same server id and tool differently; neither applies.</summary>
    public const string BindingConflict = "binding-conflict";
}

/// <summary>
/// The links that count now: the code registrations and the stored entries that passed their checks.
/// </summary>
internal sealed class NhAssistantMcpLinkView
{
    private readonly NhAssistantMcpChainCaller[] _callers;
    private readonly NhAssistantMcpContextBinding[] _storedBindings;

    public NhAssistantMcpLinkView(
        string? applicationId,
        IEnumerable<NhAssistantMcpChainCaller> callers,
        IEnumerable<NhAssistantMcpContextBinding> storedBindings)
    {
        ApplicationId = applicationId;
        // A caller reviewed for one client of an issuer is recognized before the issuer's other callers,
        // code registrations before stored ones.
        _callers = callers
            .OrderBy(caller => caller.ClientId is null ? 1 : 0)
            .ToArray();
        _storedBindings = storedBindings.ToArray();
    }

    public string? ApplicationId { get; }

    /// <summary>
    /// Every reviewed caller, in the order a request is matched against them.
    /// </summary>
    public IReadOnlyList<NhAssistantMcpChainCaller> Callers => _callers;

    /// <summary>
    /// The stored bindings that apply; code bindings are not among them.
    /// </summary>
    public IReadOnlyList<NhAssistantMcpContextBinding> StoredBindings => _storedBindings;

    /// <summary>
    /// Whether a consultation that an MCP caller starts may continue its chain.
    /// </summary>
    public bool ContinuesChains => ApplicationId is not null && _callers.Length > 0;

    public NhAssistantMcpChainCaller? FindCaller(ClaimsPrincipal? principal)
    {
        return NhAssistantMcpChains.FindCaller(_callers, principal);
    }

    /// <summary>
    /// The stored binding of a tool on an administrator-connected server, when its id, URL and tool name
    /// all match.
    /// </summary>
    public NhAssistantMcpContextBinding? FindStoredBinding(AssistantMcpServer server, string remoteName)
    {
        return NhAssistantMcpContextBindings.Find(_storedBindings, server, remoteName);
    }

    /// <summary>
    /// The stored binding of a server id and tool, for a call whose import checked the server's URL.
    /// </summary>
    public NhAssistantMcpContextBinding? FindStoredBinding(string serverId, string remoteName)
    {
        return NhAssistantMcpContextBindings.Find(_storedBindings, serverId, remoteName);
    }
}

/// <summary>
/// Reads the application's stored MCP links (<see cref="INhAssistantMcpLinks"/>) in a scope of its own,
/// checks every entry as a code registration is checked at startup and keeps the result for
/// <see cref="NhAssistantMcpOptions.LinkCacheDuration"/> on the application's clock. One read runs at a
/// time; a failed read counts as no stored links until the next one.
/// </summary>
internal sealed partial class NhAssistantMcpLinkCache
{
    private const string CallerKind = "chain-caller";
    private const string BindingKind = "context-binding";

    private readonly IServiceScopeFactory _scopes;
    private readonly NhAssistantMcpChains _chains;
    private readonly NhAssistantMcpContextBindings _codeBindings;
    private readonly IOptionsMonitor<NhAssistantOptions> _options;
    private readonly ILogger<NhAssistantMcpLinkCache> _logger;
    private readonly TimeProvider _clock;
    private readonly bool _hasSource;
    private readonly NhAssistantMcpLinkView _codeOnly;
    private readonly SemaphoreSlim _read = new(1, 1);
    private HashSet<string> _reported = new(StringComparer.Ordinal);
    private Cached? _cached;

    public NhAssistantMcpLinkCache(
        IServiceProvider services,
        IServiceScopeFactory scopes,
        NhAssistantMcpChains chains,
        NhAssistantMcpContextBindings codeBindings,
        IOptionsMonitor<NhAssistantOptions> options,
        ILogger<NhAssistantMcpLinkCache> logger)
    {
        _scopes = scopes;
        _chains = chains;
        _codeBindings = codeBindings;
        _options = options;
        _logger = logger;
        _clock = services.GetService<TimeProvider>() ?? TimeProvider.System;
        _hasSource = services.GetService<IServiceProviderIsService>()?.IsService(typeof(INhAssistantMcpLinks)) ?? true;
        _codeOnly = new NhAssistantMcpLinkView(chains.ApplicationId, chains.Callers, []);
    }

    /// <summary>
    /// The links that count now. Without an <see cref="INhAssistantMcpLinks"/> these are the code
    /// registrations, without a read.
    /// </summary>
    public async ValueTask<NhAssistantMcpLinkView> GetAsync(CancellationToken cancellationToken)
    {
        if (!_hasSource)
        {
            return _codeOnly;
        }

        if (Fresh() is { } fresh)
        {
            return fresh;
        }

        await _read.WaitAsync(cancellationToken);
        try
        {
            // Another caller may have read while this one waited.
            if (Fresh() is { } read)
            {
                return read;
            }

            var view = await ReadAsync(cancellationToken);
            _cached = new Cached(view, _clock.GetUtcNow() + _options.CurrentValue.Mcp.BoundedLinkCacheDuration());
            return view;
        }
        finally
        {
            _read.Release();
        }
    }

    private NhAssistantMcpLinkView? Fresh()
    {
        var cached = _cached;
        return cached is not null && cached.ExpiresAt > _clock.GetUtcNow()
            ? cached.View
            : null;
    }

    private async Task<NhAssistantMcpLinkView> ReadAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var source = scope.ServiceProvider.GetService<INhAssistantMcpLinks>();
        if (source is null)
        {
            return _codeOnly;
        }

        NhAssistantMcpLinkList? list;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(NhAssistantMcpLinkList.ReadTimeLimit);
        try
        {
            list = await source.ListAsync(limit.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Stored links fail closed; the code registrations still count.
            LogReadFailed(source.GetType().FullName ?? source.GetType().Name, exception.GetType().FullName ?? exception.GetType().Name);
            return _codeOnly;
        }

        return Check(list ?? NhAssistantMcpLinkList.Empty);
    }

    /// <summary>
    /// The stored entries that a registration in code would accept, next to the code registrations.
    /// </summary>
    private NhAssistantMcpLinkView Check(NhAssistantMcpLinkList list)
    {
        var problems = new HashSet<string>(StringComparer.Ordinal);
        var callers = CheckCallers(list.ChainCallers ?? [], problems);
        var bindings = CheckBindings(list.ContextBindings ?? [], problems);
        _reported = problems;
        return new NhAssistantMcpLinkView(_chains.ApplicationId, _chains.Callers.Concat(callers), bindings);
    }

    private List<NhAssistantMcpChainCaller> CheckCallers(
        IReadOnlyList<NhAssistantMcpChainCaller> entries,
        HashSet<string> problems)
    {
        var applicationId = _chains.ApplicationId;
        var valid = new List<(int Index, NhAssistantMcpChainCaller Caller)>();
        for (var index = 0; index < entries.Count; index++)
        {
            var caller = entries[index];
            var problem = index >= NhAssistantMcpLinkList.MaxEntries
                ? NhAssistantMcpLinkProblems.TooMany
                : caller is null
                    ? NhAssistantMcpLinkProblems.Missing
                    : NhAssistantMcpContext.CallerProblem(caller)
                      ?? (applicationId is null
                          ? NhAssistantMcpLinkProblems.ChainApplicationMissing
                          : string.Equals(caller.ApplicationId, applicationId, StringComparison.Ordinal)
                              ? NhAssistantMcpLinkProblems.OwnApplicationName
                              : null);
            if (problem is not null)
            {
                Report(problems, CallerKind, index, NameOf(caller), problem);
                continue;
            }

            valid.Add((index, caller!));
        }

        // An entry the code registers already adds nothing. Issuer and client name one application: a
        // stored entry that names another application than code, or than another stored entry, is left out.
        var code = _chains.Callers;
        var stored = valid
            .DistinctBy(item => item.Caller)
            .Where(item => !code.Contains(item.Caller))
            .ToList();
        var conflicting = stored
            .GroupBy(item => Key(item.Caller))
            .Where(group => group.Count() > 1 || code.Any(registered => Key(registered) == group.Key))
            .SelectMany(group => group)
            .ToList();
        foreach (var (index, caller) in conflicting)
        {
            Report(problems, CallerKind, index, caller.ApplicationId, NhAssistantMcpLinkProblems.CallerConflict);
        }

        return stored
            .Except(conflicting)
            .Select(item => item.Caller)
            .ToList();
    }

    private List<NhAssistantMcpContextBinding> CheckBindings(
        IReadOnlyList<NhAssistantMcpContextBinding> entries,
        HashSet<string> problems)
    {
        var valid = new List<(int Index, NhAssistantMcpContextBinding Binding)>();
        for (var index = 0; index < entries.Count; index++)
        {
            var binding = entries[index];
            var problem = index >= NhAssistantMcpLinkList.MaxEntries
                ? NhAssistantMcpLinkProblems.TooMany
                : binding is null
                    ? NhAssistantMcpLinkProblems.Missing
                    : NhAssistantMcpContext.BindingProblem(binding)
                      ?? (binding.IncludeChain && _chains.ApplicationId is null
                          ? NhAssistantMcpLinkProblems.ChainApplicationMissing
                          : _codeBindings.Find(binding.ServerId, binding.RemoteName) is not null
                              ? NhAssistantMcpLinkProblems.CodeBinding
                              : null);
            if (problem is not null)
            {
                Report(problems, BindingKind, index, NameOf(binding), problem);
                continue;
            }

            valid.Add((index, binding!));
        }

        // One server id and tool has one binding, so a call finds the one its import applied.
        var distinct = valid.DistinctBy(item => item.Binding).ToList();
        var conflicting = distinct
            .GroupBy(item => (item.Binding.ServerId, item.Binding.RemoteName))
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .ToList();
        foreach (var (index, binding) in conflicting)
        {
            Report(problems, BindingKind, index, binding.ServerId, NhAssistantMcpLinkProblems.BindingConflict);
        }

        return distinct
            .Except(conflicting)
            .Select(item => item.Binding)
            .ToList();
    }

    /// <summary>
    /// Records a left-out entry and logs it when its reason first appears, not on every read.
    /// </summary>
    private void Report(HashSet<string> problems, string kind, int index, string? name, string problem)
    {
        var shown = name ?? "-";
        var fingerprint = string.Join('|', kind, index.ToString(System.Globalization.CultureInfo.InvariantCulture), shown, problem);
        if (problems.Add(fingerprint) && !_reported.Contains(fingerprint))
        {
            LogEntryLeftOut(kind, index, shown, problem);
        }
    }

    private static (string Issuer, string IssuerClaimType, string? ClientClaimType, string? ClientId) Key(NhAssistantMcpChainCaller caller)
    {
        return (caller.Issuer, caller.IssuerClaimType, caller.ClientClaimType, caller.ClientId);
    }

    /// <summary>
    /// The name a warning shows: only a valid application or server id, never other stored text.
    /// </summary>
    private static string? NameOf(NhAssistantMcpChainCaller? caller)
    {
        return caller is not null && NhAssistantMcpContext.IsApplicationId(caller.ApplicationId) ? caller.ApplicationId : null;
    }

    private static string? NameOf(NhAssistantMcpContextBinding? binding)
    {
        return binding is not null && NhAiNames.IsSegment(binding.ServerId) ? binding.ServerId : null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The application's MCP links ({Source}) could not be read ({ExceptionType}); until the next read only the code registrations count.")]
    private partial void LogReadFailed(string source, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A stored MCP link was left out: {Kind} {Index} ({Name}): {Problem}.")]
    private partial void LogEntryLeftOut(string kind, int index, string name, string problem);

    private sealed record Cached(NhAssistantMcpLinkView View, DateTimeOffset ExpiresAt);
}
