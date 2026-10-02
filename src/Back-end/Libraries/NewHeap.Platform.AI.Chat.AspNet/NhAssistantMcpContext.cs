using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Mcp;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>
/// A reviewed administrator-connected MCP tool that receives the assistant context as request
/// metadata (<c>_meta</c> key <see cref="NhAssistantMcpContext.MetaKey"/>). The binding applies only
/// while the server keeps this id and URL; other servers and tools never receive it.
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
}

public static class NhAssistantMcpContext
{
    public const string MetaKey = "com.newheap/assistant-context";

    /// <summary>
    /// The version of the metadata shape below <see cref="MetaKey"/>.
    /// </summary>
    public const int Version = 1;

    /// <summary>
    /// Sends the assistant context with calls of one reviewed MCP tool: the acting agent, the
    /// accountable person, the conversation, turn and invocation ids and, optionally, a bounded
    /// conversation snapshot. Each call verifies the running turn before the context is bound.
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
            new NhAiMcpInvocationBinding(binding.ServerId, binding.RemoteName, NhAssistantMcpContextBinder.BinderId)
            {
                Version = Version
            });
        builder.Services.AddSingleton(new NhAssistantMcpContextBindingRegistration(binding));
        builder.Services.TryAddSingleton<NhAssistantMcpContextBindings>();
        return builder;
    }
}

internal sealed record NhAssistantMcpContextBindingRegistration(NhAssistantMcpContextBinding Binding);

internal sealed class NhAssistantMcpContextBindings(IEnumerable<NhAssistantMcpContextBindingRegistration> registrations)
{
    private readonly NhAssistantMcpContextBinding[] _bindings = registrations.Select(item => item.Binding).ToArray();

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
/// the turn and the accountable actor's access, so a stale or foreign call fails before sending.
/// </summary>
internal sealed class NhAssistantMcpContextBinder(
    NhAssistantMcpContextBindings bindings,
    INhAssistantConversationSnapshotProvider snapshots) : INhAiMcpInvocationBinder
{
    public const string BinderId = "newheap-assistant-context";

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
        var snapshot = await snapshots.GetSnapshotAsync(context.Invocation, binding.Snapshot, cancellationToken);
        if (!snapshot.Success)
        {
            return TaskResult<NhAiMcpRequestMetadata>.Failed(snapshot);
        }

        var data = snapshot.Data;
        var json = new JsonObject
        {
            ["version"] = NhAssistantMcpContext.Version,
            ["invocation"] = context.Identity.ToJson(),
            ["conversation"] = new JsonObject
            {
                ["id"] = data.ConversationId.ToString(),
                ["turnId"] = data.TurnId.ToString(),
                ["ownerActorId"] = data.ConversationOwnerActorId,
                ["accountableActorId"] = data.AccountableActorId
            }
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
        return TaskResult<NhAiMcpRequestMetadata>.Succeeded(
            NhAiMcpRequestMetadata.Create(NhAssistantMcpContext.MetaKey, json));
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
