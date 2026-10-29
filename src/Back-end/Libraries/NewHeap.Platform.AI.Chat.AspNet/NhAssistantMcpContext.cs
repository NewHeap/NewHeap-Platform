using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Mcp;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>
/// A reviewed MCP tool that receives the assistant context as request metadata (<c>_meta</c> key
/// <see cref="NhAssistantMcpContext.MetaKey"/>). For an administrator-connected server the binding
/// applies only while the server keeps this id and URL; a code-owned import opts in with
/// <c>InvocationBinderId = </c><see cref="NhAssistantMcpContext.BinderId"/> for this server id and tool.
/// Other servers and tools never receive it.
/// </summary>
public sealed record NhAssistantMcpContextBinding(
    string ServerId,
    string ServerUrl,
    string RemoteName)
{
    /// <summary>
    /// Adds the bounded conversation snapshot. Without it the metadata only carries provenance.
    /// </summary>
    public bool IncludeSnapshot { get; init; } = true;

    public NhAssistantConversationSnapshotRequest Snapshot { get; init; } = new();

    /// <summary>
    /// Adds the consultation chain of the calling turn as <c>chain</c>: the agents that asked, outermost
    /// first and each with the application it belongs to, and the turn's consultation depth. An
    /// application that continues the chain of this one (<see cref="NhAssistantMcpContext.ContinueMcpChain"/>)
    /// then refuses a loop between the two as one host does. Off by default; it needs the application's
    /// own name in chains (<see cref="NhAssistantMcpContext.UseMcpChainApplication"/>).
    /// </summary>
    /// <remarks>
    /// With the chain the binding also binds a call from a consultation turn, which works in the
    /// consulted agent's thread and stores no messages: its <c>conversation</c> names that thread and
    /// the consultation as turn, and it carries no snapshot. A receiver that does not know <c>chain</c>
    /// reads the rest as before; the version stays <see cref="NhAssistantMcpContext.Version"/>.
    /// </remarks>
    public bool IncludeChain { get; init; }
}

public static class NhAssistantMcpContext
{
    public const string MetaKey = "com.newheap/assistant-context";

    /// <summary>
    /// The version of the metadata shape below <see cref="MetaKey"/>.
    /// </summary>
    public const int Version = 1;

    /// <summary>
    /// The invocation binder of the assistant context, for the <c>InvocationBinderId</c> of a code-owned
    /// MCP import whose server id and tool name <see cref="AddMcpContextBinding"/> names.
    /// </summary>
    public const string BinderId = "newheap-assistant-context";

    /// <summary>
    /// The most agents a received consultation chain may name; a longer one is ignored.
    /// </summary>
    public const int MaxChainAgents = 16;

    /// <summary>
    /// The longest name of an application in consultation chains.
    /// </summary>
    public const int MaxApplicationIdLength = 256;

    /// <summary>
    /// The longest agent id in a received consultation chain, as for an agent of this application.
    /// </summary>
    internal const int MaxAgentIdLength = 128;

    /// <summary>
    /// Sends the assistant context with calls of one reviewed MCP tool: the acting agent, the
    /// accountable person, the conversation, turn and invocation ids and, optionally, a bounded
    /// conversation snapshot and the consultation chain. Each call verifies the running turn before
    /// the context is bound.
    /// </summary>
    public static NhAssistantBuilder AddMcpContextBinding(
        this NhAssistantBuilder builder,
        NhAssistantMcpContextBinding binding)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(binding.Snapshot);
        if (NhAssistantMcpContextBindings.Normalize(binding.ServerUrl) is null)
        {
            throw new ArgumentException("The MCP context binding needs an absolute http or https server URL.", nameof(binding));
        }
        binding.Snapshot.Validate();
        builder.Services.AddNewHeapPlatformAIMcpInvocationBinder<NhAssistantMcpContextBinder>(
            NhAssistantMcpContextBinder.InvocationBindingOf(binding.ServerId, binding.RemoteName));
        builder.Services.AddSingleton(new NhAssistantMcpContextBindingRegistration(binding));
        builder.Services.TryAddSingleton<NhAssistantMcpContextBindings>();
        AddChains(builder.Services);
        return builder;
    }

    /// <summary>
    /// Names this application in consultation chains across applications, for example with its public
    /// origin or token issuer. The chains it sends (<see cref="NhAssistantMcpContextBinding.IncludeChain"/>)
    /// name its agents with it, and in the chains it continues (<see cref="ContinueMcpChain"/>) an agent
    /// with this name is one of its own. Every application of a chain needs its own name.
    /// </summary>
    public static NhAssistantBuilder UseMcpChainApplication(this NhAssistantBuilder builder, string applicationId)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!IsApplicationId(applicationId))
        {
            throw new ArgumentException(
                $"The name in consultation chains needs 1 to {MaxApplicationIdLength} characters without spaces or control characters.",
                nameof(applicationId));
        }

        var existing = builder.Services
            .Select(descriptor => descriptor.ImplementationInstance)
            .OfType<NhAssistantMcpChainApplicationRegistration>()
            .FirstOrDefault();
        if (existing is not null)
        {
            if (!string.Equals(existing.ApplicationId, applicationId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The application is named '{existing.ApplicationId}' in consultation chains already.");
            }

            return builder;
        }

        builder.Services.AddSingleton(new NhAssistantMcpChainApplicationRegistration(applicationId));
        AddChains(builder.Services);
        return builder;
    }

    /// <summary>
    /// Continues the consultation chain that a reviewed, authenticated MCP caller of another application
    /// sends (FB28). A consultation that this caller starts then begins where its chain ended: one
    /// deeper than the deeper of the caller's depth and this application's own, with the caller's agents
    /// counted as askers, so the depth limit and an agent already in the chain refuse it with
    /// <c>assistant-consult-depth-exceeded</c> as in one host. A chain from any other caller, or a
    /// malformed one, is ignored, never refused: the call is then a direct call. It is loop protection
    /// between cooperating applications, not authorization; the agent's budget, the daily consultation
    /// limit per person and the call's deadline still bound every call.
    /// </summary>
    public static NhAssistantBuilder ContinueMcpChain(this NhAssistantBuilder builder, NhAssistantMcpChainCaller caller)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(caller);
        switch (CallerProblem(caller))
        {
            case NhAssistantMcpLinkProblems.ApplicationIdInvalid:
                throw new ArgumentException(
                    $"The caller's name in consultation chains needs 1 to {MaxApplicationIdLength} characters without spaces or control characters.",
                    nameof(caller));
            case NhAssistantMcpLinkProblems.IssuerInvalid:
                throw new ArgumentException("A reviewed MCP caller needs an issuer and an issuer claim type.", nameof(caller));
            case NhAssistantMcpLinkProblems.ClientInvalid:
                throw new ArgumentException("A reviewed MCP caller names its client with both a claim type and a client id, or neither.", nameof(caller));
        }

        builder.Services.AddSingleton(new NhAssistantMcpChainCallerRegistration(caller));
        AddChains(builder.Services);
        return builder;
    }

    /// <summary>
    /// Lets the application store the chain callers and context bindings it reviewed
    /// (<see cref="INhAssistantMcpLinks"/>, FB59), next to those registered in code: a link it creates or
    /// revokes while the host runs counts within <see cref="NhAssistantMcpOptions.LinkCacheDuration"/>,
    /// without a restart. Each stored entry is checked as the same registration in code is checked at
    /// startup; an entry such a registration would refuse is left out with a warning. One source serves
    /// the host; a later call replaces an earlier one. It is a scoped service, resolved in a scope of its
    /// own. Without one nothing changes: only the code registrations count.
    /// </summary>
    public static NhAssistantBuilder UseMcpLinks<TLinks>(this NhAssistantBuilder builder)
        where TLinks : class, INhAssistantMcpLinks
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.Replace(ServiceDescriptor.Scoped<INhAssistantMcpLinks, TLinks>());
        // A stored binding binds with the assistant context binder, also where code binds no tool.
        builder.Services.AddNewHeapPlatformAIMcp();
        builder.Services.TryAddScoped<NhAssistantMcpContextBinder>();
        builder.Services.TryAddSingleton<NhAssistantMcpContextBindings>();
        AddChains(builder.Services);
        return builder;
    }

    /// <summary>
    /// Whether <paramref name="value"/> can name an application in consultation chains: 1 to
    /// <see cref="MaxApplicationIdLength"/> characters without white space or control characters.
    /// </summary>
    public static bool IsApplicationId(string? value)
    {
        return !string.IsNullOrEmpty(value)
               && value.Length <= MaxApplicationIdLength
               && !value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character));
    }

    /// <summary>
    /// Why <see cref="ContinueMcpChain"/> refuses <paramref name="caller"/>, as a
    /// <see cref="NhAssistantMcpLinkProblems"/> code, or <see langword="null"/> when it accepts it.
    /// </summary>
    internal static string? CallerProblem(NhAssistantMcpChainCaller caller)
    {
        if (!IsApplicationId(caller.ApplicationId))
        {
            return NhAssistantMcpLinkProblems.ApplicationIdInvalid;
        }

        if (string.IsNullOrWhiteSpace(caller.Issuer)
            || caller.Issuer.Length > 256
            || string.IsNullOrWhiteSpace(caller.IssuerClaimType)
            || caller.IssuerClaimType.Length > 256)
        {
            return NhAssistantMcpLinkProblems.IssuerInvalid;
        }

        if ((caller.ClientId is null) != (caller.ClientClaimType is null)
            || caller.ClientId is { Length: 0 or > 256 }
            || caller.ClientClaimType is { Length: 0 or > 256 })
        {
            return NhAssistantMcpLinkProblems.ClientInvalid;
        }

        return null;
    }

    /// <summary>
    /// Why <see cref="AddMcpContextBinding"/> refuses <paramref name="binding"/>, as a
    /// <see cref="NhAssistantMcpLinkProblems"/> code, or <see langword="null"/> when it accepts it: the
    /// same checks of the server URL, the snapshot bounds and the invocation binding.
    /// </summary>
    internal static string? BindingProblem(NhAssistantMcpContextBinding binding)
    {
        if (binding.Snapshot is null)
        {
            return NhAssistantMcpLinkProblems.SnapshotInvalid;
        }

        if (NhAssistantMcpContextBindings.Normalize(binding.ServerUrl) is null)
        {
            return NhAssistantMcpLinkProblems.ServerUrlInvalid;
        }

        try
        {
            binding.Snapshot.Validate();
        }
        catch (ArgumentOutOfRangeException)
        {
            return NhAssistantMcpLinkProblems.SnapshotInvalid;
        }

        try
        {
            NhAiMcpInvocationBinderRegistry.Validate(NhAssistantMcpContextBinder.InvocationBindingOf(binding.ServerId, binding.RemoteName));
        }
        catch (ArgumentException)
        {
            return NhAssistantMcpLinkProblems.ToolInvalid;
        }

        return null;
    }

    private static void AddChains(IServiceCollection services)
    {
        services.TryAddSingleton<NhAssistantMcpChains>();
        services.TryAddScoped<NhAssistantMcpChainReader>();
        services.TryAddSingleton<NhAssistantMcpContextBindings>();
        services.TryAddSingleton<NhAssistantMcpLinkCache>();
    }
}

internal sealed record NhAssistantMcpContextBindingRegistration(NhAssistantMcpContextBinding Binding);

internal sealed class NhAssistantMcpContextBindings(IEnumerable<NhAssistantMcpContextBindingRegistration> registrations)
{
    private readonly NhAssistantMcpContextBinding[] _bindings = registrations.Select(item => item.Binding).ToArray();

    /// <summary>
    /// Whether a binding sends the consultation chain.
    /// </summary>
    public bool IncludeChain => _bindings.Any(binding => binding.IncludeChain);

    /// <summary>
    /// The binding of a tool on an administrator-connected server, when its id, URL and tool name
    /// all match the reviewed binding.
    /// </summary>
    public NhAssistantMcpContextBinding? Find(AssistantMcpServer server, string remoteName)
    {
        return Find(_bindings, server, remoteName);
    }

    public NhAssistantMcpContextBinding? Find(string serverId, string remoteName)
    {
        return Find(_bindings, serverId, remoteName);
    }

    /// <summary>
    /// The binding among <paramref name="bindings"/> of a tool on an administrator-connected server, when
    /// its id, URL and tool name all match.
    /// </summary>
    public static NhAssistantMcpContextBinding? Find(
        IEnumerable<NhAssistantMcpContextBinding> bindings,
        AssistantMcpServer server,
        string remoteName)
    {
        var url = Normalize(server.Url);
        return url is null
            ? null
            : bindings.FirstOrDefault(binding =>
                string.Equals(binding.ServerId, server.Id, StringComparison.Ordinal)
                && string.Equals(binding.RemoteName, remoteName, StringComparison.Ordinal)
                && string.Equals(Normalize(binding.ServerUrl), url, StringComparison.Ordinal));
    }

    public static NhAssistantMcpContextBinding? Find(
        IEnumerable<NhAssistantMcpContextBinding> bindings,
        string serverId,
        string remoteName)
    {
        return bindings.FirstOrDefault(binding =>
            string.Equals(binding.ServerId, serverId, StringComparison.Ordinal)
            && string.Equals(binding.RemoteName, remoteName, StringComparison.Ordinal));
    }

    public static string? Normalize(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }
        return uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped).ToLowerInvariant()
            + uri.AbsolutePath.TrimEnd('/')
            + uri.Query;
    }
}

/// <summary>
/// Binds the assistant context to a reviewed MCP call. The snapshot provider verifies the call,
/// the turn and the accountable actor's access, so a stale or foreign call fails before sending. A
/// call from a consultation turn, which only a binding with the chain binds, is verified against the
/// running call and carries no snapshot. A tool of an administrator-connected server that a stored
/// binding bound when the turn imported it fails to bind once that binding is revoked.
/// </summary>
internal sealed class NhAssistantMcpContextBinder(
    NhAssistantMcpContextBindings bindings,
    INhAssistantConversationSnapshotProvider snapshots,
    NhAssistantMcpChains chains,
    NhAssistantMcpLinkCache links) : INhAiMcpInvocationBinder
{
    public const string BinderId = NhAssistantMcpContext.BinderId;

    /// <summary>
    /// The invocation binding of the assistant context for a server id and tool: what
    /// <see cref="NhAssistantMcpContext.AddMcpContextBinding"/> registers, and what an import of an
    /// administrator-connected server names for a stored binding.
    /// </summary>
    public static NhAiMcpInvocationBinding InvocationBindingOf(string serverId, string remoteName)
    {
        return new NhAiMcpInvocationBinding(serverId, remoteName, BinderId)
        {
            Version = NhAssistantMcpContext.Version
        };
    }

    /// <summary>
    /// The binder of a stored binding, which the importing tool source checked for this server and tool.
    /// </summary>
    public static NhAiMcpInvocationBinderRegistration RegistrationOf(string serverId, string remoteName)
    {
        return new NhAiMcpInvocationBinderRegistration(InvocationBindingOf(serverId, remoteName), typeof(NhAssistantMcpContextBinder));
    }

    public async ValueTask<TaskResult<NhAiMcpRequestMetadata>> BindAsync(
        NhAiMcpInvocationBindingContext context,
        CancellationToken cancellationToken = default)
    {
        var binding = bindings.Find(context.ServerId, context.RemoteName)
            ?? (await links.GetAsync(cancellationToken)).FindStoredBinding(context.ServerId, context.RemoteName);
        if (binding is null)
        {
            return TaskResult<NhAiMcpRequestMetadata>.Failed(
                NhAiMcpInvocationBindingCodes.BindingFailed,
                "The MCP tool has no reviewed assistant context binding.");
        }
        if (binding.IncludeChain && chains.ApplicationId is null)
        {
            return TaskResult<NhAiMcpRequestMetadata>.Failed(
                NhAiMcpInvocationBindingCodes.BindingFailed,
                "The application has no name in consultation chains.");
        }

        var json = new JsonObject
        {
            ["version"] = NhAssistantMcpContext.Version,
            ["invocation"] = context.Identity.ToJson()
        };
        var turn = NhAssistantExecutionScope.CurrentTurn;
        if (binding.IncludeChain && turn?.Consultation is not null)
        {
            // A consultation turn works in the consulted agent's thread and stores no messages, so the
            // call is checked against the running call itself and sends no snapshot.
            if (!IsRunningCall(context.Invocation, turn))
            {
                return TaskResult<NhAiMcpRequestMetadata>.Failed(
                    NhAssistantSnapshotCodes.Forbidden,
                    "The invocation does not belong to the running assistant tool call.");
            }

            json["conversation"] = new JsonObject
            {
                ["id"] = turn.ConversationId.ToString(),
                ["turnId"] = turn.TurnId.ToString(),
                ["ownerActorId"] = turn.ConversationOwnerActorId ?? turn.AccountableOwnerId,
                ["accountableActorId"] = turn.AccountableOwnerId
            };
        }
        else
        {
            var snapshot = await snapshots.GetSnapshotAsync(context.Invocation, binding.Snapshot, cancellationToken);
            if (!snapshot.Success)
            {
                return TaskResult<NhAiMcpRequestMetadata>.Failed(snapshot);
            }

            var data = snapshot.Data;
            json["conversation"] = new JsonObject
            {
                ["id"] = data.ConversationId.ToString(),
                ["turnId"] = data.TurnId.ToString(),
                ["ownerActorId"] = data.ConversationOwnerActorId,
                ["accountableActorId"] = data.AccountableActorId
            };
            if (binding.IncludeSnapshot)
            {
                json["snapshot"] = new JsonObject
                {
                    ["firstQuestion"] = data.FirstQuestion is null ? null : ToJson(data.FirstQuestion),
                    ["recentMessages"] = new JsonArray(data.RecentMessages.Select(message => (JsonNode)ToJson(message)).ToArray()),
                    ["omittedMessages"] = data.OmittedMessages,
                    ["omittedBytes"] = data.OmittedBytes
                };
            }
        }

        if (binding.IncludeChain)
        {
            // The snapshot provider found the running turn, or the consultation turn was checked above.
            json[NhAssistantMcpChains.ChainKey] = chains.ChainOf(NhAssistantExecutionScope.CurrentTurn!);
        }

        return TaskResult<NhAiMcpRequestMetadata>.Succeeded(
            NhAiMcpRequestMetadata.Create(NhAssistantMcpContext.MetaKey, json));
    }

    /// <summary>
    /// Whether the invocation is the bound context of exactly the running call of the consultation turn:
    /// the consulted agent acting for the asker in this turn.
    /// </summary>
    private static bool IsRunningCall(NhAiInvocationContext invocation, NhAssistantTurnScope turn)
    {
        var call = NhAssistantExecutionScope.CurrentCall;
        return call is not null
               && invocation.InvocationId == call.InvocationId
               && invocation.ActorKind == NhAiActorKind.Agent
               && string.Equals(invocation.ActorId, turn.Agent.ActorId, StringComparison.Ordinal)
               && string.Equals(invocation.AccountableOwnerId, turn.AccountableOwnerId, StringComparison.Ordinal);
    }

    private static JsonObject ToJson(NhAssistantSnapshotMessage message)
    {
        var json = new JsonObject
        {
            ["sequence"] = message.Sequence,
            ["role"] = message.Role,
            ["text"] = message.Text
        };
        if (message.AuthorActorId is not null)
        {
            json["authorActorId"] = message.AuthorActorId;
        }
        if (message.Truncated)
        {
            json["truncated"] = true;
        }
        return json;
    }
}
