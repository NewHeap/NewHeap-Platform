using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;

namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>
/// An authenticated MCP caller of another application whose consultation chain this application
/// continues (<see cref="NhAssistantMcpContext.ContinueMcpChain"/>). A request is from this caller when
/// its authenticated principal carries exactly one <see cref="IssuerClaimType"/> claim with the value
/// <see cref="Issuer"/> and, when <see cref="ClientId"/> is set, exactly one
/// <see cref="ClientClaimType"/> claim with that value.
/// </summary>
/// <param name="ApplicationId">The caller's own name in consultation chains, as it names itself with
/// <see cref="NhAssistantMcpContext.UseMcpChainApplication"/>. Its calling agent must carry this name.</param>
/// <param name="Issuer">The exact issuer the caller's tokens carry, for example the application's own
/// token issuer when it sends its people's delegated tokens.</param>
public sealed record NhAssistantMcpChainCaller(string ApplicationId, string Issuer)
{
    /// <summary>
    /// The claim that carries the issuer: <c>iss</c> by default.
    /// </summary>
    public string IssuerClaimType { get; init; } = "iss";

    /// <summary>
    /// The claim that names the calling client, such as <c>azp</c>, for an issuer that serves several
    /// applications; set it together with <see cref="ClientId"/>.
    /// </summary>
    public string? ClientClaimType { get; init; }

    /// <summary>
    /// The calling client's id in <see cref="ClientClaimType"/>; <see langword="null"/> accepts every
    /// caller of the issuer.
    /// </summary>
    public string? ClientId { get; init; }
}

internal sealed record NhAssistantMcpChainApplicationRegistration(string ApplicationId);

internal sealed record NhAssistantMcpChainCallerRegistration(NhAssistantMcpChainCaller Caller);

/// <summary>
/// How an agent appears in a consultation chain of this host. An agent of this application keeps its
/// own id; an agent of another application is <c>agentId@application</c>. Agent ids are dash-case and
/// never contain <c>@</c>, so the two never meet, and two applications with an agent of the same id never
/// refuse each other.
/// </summary>
internal static class NhAssistantChainAgents
{
    public const char Separator = '@';

    public static string Qualify(string agentId, string applicationId)
    {
        return agentId + Separator + applicationId;
    }

    /// <summary>
    /// The agent id of a chain entry and its application, which is <see langword="null"/> for an agent of
    /// this application.
    /// </summary>
    public static (string AgentId, string? ApplicationId) Split(string entry)
    {
        var separator = entry.IndexOf(Separator, StringComparison.Ordinal);
        return separator < 0
            ? (entry, null)
            : (entry[..separator], entry[(separator + 1)..]);
    }
}

/// <summary>
/// The consultation chain across applications of this host (FB28): the name it gives its own agents in
/// the chains it sends, and the reviewed callers whose chains it continues. It writes the <c>chain</c> of
/// <see cref="NhAssistantMcpContext.MetaKey"/> and reads it back. It is loop protection between
/// cooperating applications, not authorization.
/// </summary>
internal sealed class NhAssistantMcpChains
{
    public const string ChainKey = "chain";
    public const string AgentsKey = "agents";
    public const string ApplicationKey = "application";
    public const string AgentIdKey = "agentId";
    public const string ConsultationDepthKey = "consultationDepth";

    private readonly NhAssistantMcpChainCaller[] _callers;

    public NhAssistantMcpChains(
        IEnumerable<NhAssistantMcpChainApplicationRegistration> applications,
        IEnumerable<NhAssistantMcpChainCallerRegistration> callers)
    {
        var names = applications.Select(item => item.ApplicationId).Distinct(StringComparer.Ordinal).ToArray();
        if (names.Length > 1)
        {
            throw new InvalidOperationException(
                "The application has more than one name in consultation chains. Call UseMcpChainApplication once.");
        }

        ApplicationId = names.SingleOrDefault();
        var registered = callers.Select(item => item.Caller).Distinct().ToArray();
        foreach (var group in registered.GroupBy(caller => (caller.Issuer, caller.IssuerClaimType, caller.ClientClaimType, caller.ClientId)))
        {
            if (group.Count() > 1)
            {
                throw new InvalidOperationException(
                    $"MCP callers of issuer '{group.Key.Issuer}' are reviewed for more than one application in consultation chains.");
            }
        }

        // A caller reviewed for one client of an issuer is recognized before the issuer's other callers.
        _callers = registered
            .OrderBy(caller => caller.ClientId is null ? 1 : 0)
            .ToArray();
    }

    /// <summary>
    /// The application's own name in consultation chains, from
    /// <see cref="NhAssistantMcpContext.UseMcpChainApplication"/>.
    /// </summary>
    public string? ApplicationId { get; }

    public IReadOnlyList<NhAssistantMcpChainCaller> Callers => _callers;

    /// <summary>
    /// Whether a consultation that an MCP caller starts may continue its chain.
    /// </summary>
    public bool ContinuesChains => ApplicationId is not null && _callers.Length > 0;

    /// <summary>
    /// Fails host start when a binding includes the chain or a caller is reviewed while the application
    /// has no name in chains, or when a reviewed caller takes the application's own name.
    /// </summary>
    public void Validate(bool bindingsIncludeChain)
    {
        if (ApplicationId is null && (bindingsIncludeChain || _callers.Length > 0))
        {
            throw new InvalidOperationException(
                "IncludeChain and ContinueMcpChain need the application's name in consultation chains. Call UseMcpChainApplication.");
        }

        var ownName = _callers.FirstOrDefault(caller => string.Equals(caller.ApplicationId, ApplicationId, StringComparison.Ordinal));
        if (ownName is not null)
        {
            throw new InvalidOperationException(
                $"The reviewed MCP caller of issuer '{ownName.Issuer}' takes the application's own name '{ownName.ApplicationId}' in consultation chains.");
        }
    }

    /// <summary>
    /// The reviewed caller of the request's principal, or <see langword="null"/> when the principal is
    /// not authenticated or no reviewed caller matches it.
    /// </summary>
    public NhAssistantMcpChainCaller? FindCaller(ClaimsPrincipal? principal)
    {
        return FindCaller(_callers, principal);
    }

    /// <summary>
    /// The first of <paramref name="callers"/> that the request's principal is, in their order, or
    /// <see langword="null"/> when the principal is not authenticated or none matches it.
    /// </summary>
    public static NhAssistantMcpChainCaller? FindCaller(IEnumerable<NhAssistantMcpChainCaller> callers, ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        return callers.FirstOrDefault(caller =>
            HasSingleClaim(principal, caller.IssuerClaimType, caller.Issuer)
            && (caller.ClientId is null || HasSingleClaim(principal, caller.ClientClaimType!, caller.ClientId)));
    }

    /// <summary>
    /// The chain of the running turn as this host sends it: the agents that asked, outermost first and
    /// each with its application, followed by the turn's own agent, and the turn's consultation depth.
    /// </summary>
    public JsonObject ChainOf(NhAssistantTurnScope turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        var application = ApplicationId
            ?? throw new InvalidOperationException("The application has no name in consultation chains.");
        var agents = new JsonArray();
        foreach (var entry in turn.Consultation?.Chain ?? [turn.Agent.Id])
        {
            var (agentId, owner) = NhAssistantChainAgents.Split(entry);
            agents.Add(new JsonObject
            {
                [ApplicationKey] = owner ?? application,
                [AgentIdKey] = agentId
            });
        }

        return new JsonObject
        {
            [AgentsKey] = agents,
            [ConsultationDepthKey] = turn.Consultation?.Depth ?? 0
        };
    }

    /// <summary>
    /// Reads the chain of a reviewed caller from the assistant context it sent. Returns
    /// <see langword="null"/> with a fixed <paramref name="problem"/> code when the chain is malformed,
    /// so the call continues as one without a chain; a chain is never a reason to refuse a call.
    /// Members it does not know are left alone, so a later sender may add some.
    /// </summary>
    public static NhAssistantInheritedChain? Parse(
        JsonObject context,
        NhAssistantMcpChainCaller caller,
        string applicationId,
        out string? problem)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(caller);
        problem = null;
        if (!TryGetInt(context["version"], out var version) || version != NhAssistantMcpContext.Version)
        {
            problem = "version-unsupported";
            return null;
        }

        if (context[ChainKey] is not JsonObject chain)
        {
            problem = "chain-invalid";
            return null;
        }

        if (!TryGetInt(chain[ConsultationDepthKey], out var depth) || depth < 0)
        {
            problem = "depth-invalid";
            return null;
        }

        if (chain[AgentsKey] is not JsonArray agents
            || agents.Count is 0 or > NhAssistantMcpContext.MaxChainAgents)
        {
            problem = "agents-invalid";
            return null;
        }

        var entries = new List<string>(agents.Count);
        string? callingApplication = null;
        foreach (var node in agents)
        {
            if (node is not JsonObject agent
                || !TryGetString(agent[ApplicationKey], out var owner)
                || !NhAssistantMcpContext.IsApplicationId(owner)
                || !TryGetString(agent[AgentIdKey], out var agentId)
                || agentId.Length > NhAssistantMcpContext.MaxAgentIdLength
                || !NhAiNames.IsSegment(agentId))
            {
                problem = "agent-invalid";
                return null;
            }

            callingApplication = owner;
            entries.Add(string.Equals(owner, applicationId, StringComparison.Ordinal)
                ? agentId
                : NhAssistantChainAgents.Qualify(agentId, owner));
        }

        // The calling agent is the caller's own: a chain whose last agent names another application was
        // not sent by the application the caller was reviewed as.
        if (!string.Equals(callingApplication, caller.ApplicationId, StringComparison.Ordinal))
        {
            problem = "caller-mismatch";
            return null;
        }

        return new NhAssistantInheritedChain(entries, depth);
    }

    private static bool HasSingleClaim(ClaimsPrincipal principal, string claimType, string value)
    {
        var claims = principal.FindAll(claimType).Take(2).ToArray();
        return claims.Length == 1 && string.Equals(claims[0].Value, value, StringComparison.Ordinal);
    }

    private static bool TryGetInt(JsonNode? node, out int value)
    {
        value = 0;
        return node is JsonValue json
               && json.GetValueKind() == JsonValueKind.Number
               && json.TryGetValue(out value);
    }

    private static bool TryGetString(JsonNode? node, out string value)
    {
        value = string.Empty;
        if (node is not JsonValue json || json.GetValueKind() != JsonValueKind.String)
        {
            return false;
        }

        value = json.GetValue<string>();
        return true;
    }
}

/// <summary>
/// Reads the chain that the MCP caller of the running call sent with the assistant context. Only a
/// caller this application reviewed, in code or in its stored links (<see cref="INhAssistantMcpLinks"/>),
/// is believed; the chain of any other caller is dropped, never refused, so an ordinary MCP client keeps
/// working. Nothing of the payload is logged.
/// </summary>
internal sealed class NhAssistantMcpChainReader(
    NhAssistantMcpChains chains,
    NhAssistantMcpLinkCache links,
    IHttpContextAccessor httpContextAccessor,
    ILogger<NhAssistantMcpChainReader> logger)
{
    public async ValueTask<NhAssistantInheritedChain?> ReadAsync(CancellationToken cancellationToken)
    {
        if (chains.ApplicationId is null
            || NhAiMcpInboundCall.Meta?[NhAssistantMcpContext.MetaKey] is not JsonObject context
            || !context.ContainsKey(NhAssistantMcpChains.ChainKey))
        {
            return null;
        }

        var view = await links.GetAsync(cancellationToken);
        var caller = view.FindCaller(httpContextAccessor.HttpContext?.User);
        if (caller is null)
        {
            logger.LogDebug("The consultation chain of an MCP caller that is not reviewed was ignored.");
            return null;
        }

        var inherited = NhAssistantMcpChains.Parse(context, caller, chains.ApplicationId!, out var problem);
        if (inherited is null)
        {
            logger.LogWarning(
                "The consultation chain of the reviewed MCP caller {ApplicationId} was ignored: {Problem}.",
                caller.ApplicationId,
                problem);
        }

        return inherited;
    }
}
