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
            new NhAiMcpInvocationBinding(binding.ServerId, binding.RemoteName, BinderId)
            {
                Version = Version
            });
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
        if (!IsApplicationId(caller.ApplicationId))
        {
            throw new ArgumentException(
                $"The caller's name in consultation chains needs 1 to {MaxApplicationIdLength} characters without spaces or control characters.",
                nameof(caller));
        }

        if (string.IsNullOrWhiteSpace(caller.Issuer)
            || caller.Issuer.Length > 256
            || string.IsNullOrWhiteSpace(caller.IssuerClaimType)
            || caller.IssuerClaimType.Length > 256)
        {
            throw new ArgumentException("A reviewed MCP caller needs an issuer and an issuer claim type.", nameof(caller));
        }

        if ((caller.ClientId is null) != (caller.ClientClaimType is null)
            || caller.ClientId is { Length: 0 or > 256 }
            || caller.ClientClaimType is { Length: 0 or > 256 })
        {
            throw new ArgumentException("A reviewed MCP caller names its client with both a claim type and a client id, or neither.", nameof(caller));
        }

        builder.Services.AddSingleton(new NhAssistantMcpChainCallerRegistration(caller));
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

    private static void AddChains(IServiceCollection services)
    {
        services.TryAddSingleton<NhAssistantMcpChains>();
        services.TryAddScoped<NhAssistantMcpChainReader>();
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
        var url = Normalize(server.Url);
        return url is null
            ? null
            : _bindings.FirstOrDefault(binding =>
                string.Equals(binding.ServerId, server.Id, StringComparison.Ordinal)
                && string.Equals(binding.RemoteName, remoteName, StringComparison.Ordinal)
                && string.Equals(Normalize(binding.ServerUrl), url, StringComparison.Ordinal));
    }

    public NhAssistantMcpContextBinding? Find(string serverId, string remoteName)
    {
        return _bindings.FirstOrDefault(binding =>
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
/// running call and carries no snapshot.
/// </summary>
internal sealed class NhAssistantMcpContextBinder(
    NhAssistantMcpContextBindings bindings,
    INhAssistantConversationSnapshotProvider snapshots,
    NhAssistantMcpChains chains) : INhAiMcpInvocationBinder
{
    public const string BinderId = NhAssistantMcpContext.BinderId;

    public async ValueTask<TaskResult<NhAiMcpRequestMetadata>> BindAsync(
        NhAiMcpInvocationBindingContext context,
        CancellationToken cancellationToken = default)
    {
        var binding = bindings.Find(context.ServerId, context.RemoteName);
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
