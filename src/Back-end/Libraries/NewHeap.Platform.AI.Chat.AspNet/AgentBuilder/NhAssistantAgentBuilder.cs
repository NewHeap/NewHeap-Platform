using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentBuilder;

namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>
/// How the application switches on the agent builder with
/// <see cref="NhAssistantAgentBuilderExtensions.AddAgentBuilder"/>.
/// </summary>
public sealed class NhAssistantAgentBuilderOptions
{
    /// <summary>
    /// The model profile of the agent builder's model calls, a registered profile that chats, calls functions
    /// and streams; <see langword="null"/> uses the assistant's chat profile (<c>UseChatProfile</c>), which must
    /// then be set before <c>AddAgentBuilder</c>.
    /// </summary>
    public string? ProfileName { get; set; }
}

public static class NhAssistantAgentBuilderExtensions
{
    /// <summary>
    /// The front-end translation key of the agent builder's name.
    /// </summary>
    public const string DisplayNameKey = "nh-assistant.agent-builder.name";

    /// <summary>
    /// The front-end translation key of the agent builder's description.
    /// </summary>
    public const string DescriptionKey = "nh-assistant.agent-builder.description";

    /// <summary>
    /// Switches on the agent builder (RB-7 to RB-9): an assistant agent of the platform,
    /// <see cref="NhAssistantAgentBuilderIds.AgentId"/>, that helps an administrator build a whole agent in
    /// people's chats: its definition and instructions, tools, run settings, skills, triggers and rule scripts.
    /// It reads the catalog and the agents without asking; it runs dry runs, which change nothing; and every
    /// save and every step live is a governed call that waits for the administrator's approval in the chat,
    /// whose card says what changes. It acts with the administrator's own rights through the services the
    /// administration's endpoints use, so their checks, versions, ceilings, package locks, four eyes and audit
    /// apply unchanged, and it never changes grants, technical accounts, MCP servers or their credentials, or
    /// the code's ceilings, and never deletes. Only a person who passes the assistant's access policy and its
    /// administration policy sees it, chats with it and gets its tools
    /// (<see cref="NhAssistantAgentBuilderIds.Policy"/>); no other agent is offered its tools, and its
    /// definition never changes in the administration. The administration offers it on an agent's page and
    /// with "New agent with help". Without this call nothing changes.
    /// </summary>
    public static NhAssistantBuilder AddAgentBuilder(
        this NhAssistantBuilder builder,
        Action<NhAssistantAgentBuilderOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new NhAssistantAgentBuilderOptions();
        configure?.Invoke(options);
        var profileName = options.ProfileName ?? builder.State.ChatProfileName
            ?? throw new InvalidOperationException(
                "AddAgentBuilder needs a model profile: set NhAssistantAgentBuilderOptions.ProfileName or call UseChatProfile before AddAgentBuilder.");

        builder.AddAgent(new NhAssistantAgentDefinition(
            NhAssistantAgentBuilderIds.AgentId,
            1,
            DisplayNameKey,
            DescriptionKey,
            profileName,
            NhAssistantAgentBuilderInstructions.Asset,
            [NhAssistantAgentBuilderIds.ToolIdPrefix + "*"],
            // Its writes run in the administrator's own chat after their approval; nothing runs on its own.
            NhAiAutonomyLevel.Execute,
            NhAssistantAgentBuilderIds.Policy));

        var services = builder.Services;
        services.AddAuthorizationBuilder()
            .AddPolicy(NhAssistantAgentBuilderIds.Policy, policy => policy.AddRequirements(new NhAssistantAgentBuilderRequirement()));
        services.TryAddEnumerable(ServiceDescriptor.Transient<IAuthorizationHandler, NhAssistantAgentBuilderAuthorizationHandler>());
        services.TryAddScoped<NhAssistantAgentBuilderToolHandler>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INhAiToolCatalog, NhAssistantAgentBuilderToolCatalog>());
        // They act for the person who chats: an agent grant or an accountable account never covers them.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INhAiPersonRightsTools, NhAssistantAgentBuilderPersonRightsTools>());
        builder.AddToolPresenter<NhAssistantAgentBuilderPresenter>();
        return builder;
    }
}

/// <summary>
/// The requirement of <see cref="NhAssistantAgentBuilderIds.Policy"/>: the assistant's access policy and its
/// administration policy, as the application configured them.
/// </summary>
internal sealed class NhAssistantAgentBuilderRequirement : IAuthorizationRequirement;

/// <summary>
/// Passes <see cref="NhAssistantAgentBuilderRequirement"/> for a person who passes both the access policy and
/// the administration policy in effect. The authorization service is resolved when a requirement is checked,
/// not when the handler is created, as the service creates its handlers itself.
/// </summary>
internal sealed class NhAssistantAgentBuilderAuthorizationHandler(IServiceProvider services)
    : AuthorizationHandler<NhAssistantAgentBuilderRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        NhAssistantAgentBuilderRequirement requirement)
    {
        var state = services.GetRequiredService<NhAssistantRegistrationState>();
        var options = services.GetRequiredService<IOptionsMonitor<NhAssistantOptions>>().CurrentValue;
        var authorization = services.GetRequiredService<IAuthorizationService>();
        var access = NhAssistantEndpointOptions.ResolveAccessPolicy(state, options);
        var admin = NhAssistantEndpointOptions.ResolveAdminPolicy(state, options);
        if ((await authorization.AuthorizeAsync(context.User, context.Resource, access)).Succeeded
            && (await authorization.AuthorizeAsync(context.User, context.Resource, admin)).Succeeded)
        {
            context.Succeed(requirement);
        }
    }
}

/// <summary>
/// The agent builder's tools use the rights of the person who chats only: in an agent turn the gate would
/// allow them only through a delegating initiator's rights, never through an accountable account or a grant,
/// and their own handler refuses every turn but a person's chat with the builder.
/// </summary>
internal sealed class NhAssistantAgentBuilderPersonRightsTools : INhAiPersonRightsTools
{
    public bool UsesPersonRightsOnly(NhAiToolDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return NhAssistantAgentBuilderIds.IsAgentBuilderTool(descriptor.Id);
    }
}
