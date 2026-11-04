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
    /// The longest guidance of <see cref="UseGuidance"/>, in characters, so the guidance, the application
    /// context and the builder's own instructions fit one prompt.
    /// </summary>
    public const int MaxGuidanceLength = 4_000;

    /// <summary>
    /// The model profile of the agent builder's model calls, a registered profile that chats, calls functions
    /// and streams; <see langword="null"/> uses the assistant's chat profile (<c>UseChatProfile</c>), which must
    /// then be set before <c>AddAgentBuilder</c>.
    /// </summary>
    public string? ProfileName { get; set; }

    /// <summary>
    /// The application's own guidance for the agent builder; <see langword="null"/> without it. Set it with
    /// <see cref="UseGuidance"/>.
    /// </summary>
    public NhAiTextAsset? Guidance { get; private set; }

    /// <summary>
    /// Whether the agent builder may change an agent's rights; <see langword="true"/> unless
    /// <see cref="WithoutRightsChanges"/> set its ceiling.
    /// </summary>
    public bool AllowsRightsChanges { get; private set; } = true;

    /// <summary>
    /// Keeps an agent's rights out of the agent builder (FB97), so the application switches it on for what it is
    /// good at, such as instructions, skills, triggers, consultations, tools and rule scripts, while whose rights
    /// an agent uses, who reaches it, who decides its proposals and what runs without approval stay in the
    /// administration's screens. <c>builder_read_catalog</c> shows the ceiling, so the builder names the screen
    /// instead of proposing the change, and a call that names such a setting is refused before anyone sees an
    /// approval card. More tools or more autonomy on an agent whose runs use another account's rights count too,
    /// because they put those rights to more use. What only takes something away, such as removing an MCP server,
    /// stays open. This is a ceiling on the builder, not on a person: the same person still changes these settings
    /// in the screens. <c>UseAgentRightsPolicy</c> decides who may change them anywhere.
    /// </summary>
    public NhAssistantAgentBuilderOptions WithoutRightsChanges()
    {
        AllowsRightsChanges = false;
        return this;
    }

    /// <summary>
    /// Gives the agent builder guidance of the application's own (FB95): a text asset in code, such as one
    /// loaded with <c>NhAiTextAsset.FromEmbeddedResource</c>, that only the builder reads, after the
    /// platform's rules and before the application context, in a section framed as background, so it
    /// explains the application's domain, data and conventions but never changes the platform's rules, the
    /// builder's instructions, its tools or the approvals. It is checked as instructions assets are: trusted
    /// system instructions of the application, not blank, at most <see cref="MaxGuidanceLength"/>
    /// characters, and its manifest's hash is the hash of its content. Its id and version are part of the
    /// builder's prompt version. With the builder's own instructions and the application context it fits
    /// the prompt of one turn; a turn whose instructions together are longer is refused as any turn whose
    /// instructions are too long.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="asset"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The asset is not usable as guidance.</exception>
    public NhAssistantAgentBuilderOptions UseGuidance(NhAiTextAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(asset.Manifest, nameof(asset));
        if (asset.Manifest.Role != NhAiAssetRole.SystemInstructions
            || asset.Manifest.Trust != NhAiContextTrust.TrustedApplication)
        {
            throw new ArgumentException(
                "The agent builder's guidance must be trusted system instructions of the application (NhAiAssetRole.SystemInstructions, NhAiContextTrust.TrustedApplication).",
                nameof(asset));
        }
        if (string.IsNullOrWhiteSpace(asset.Content) || asset.Content.Length > MaxGuidanceLength)
        {
            throw new ArgumentException(
                $"The agent builder's guidance must contain text of at most {MaxGuidanceLength:N0} characters.",
                nameof(asset));
        }
        if (!string.Equals(asset.Manifest.ContentHash, NhAssistantInstructionAssets.HashOf(asset.Content), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The agent builder's guidance does not match the content hash of its manifest.",
                nameof(asset));
        }

        Guidance = asset;
        return this;
    }
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
    /// Switches on the agent builder (RB-7 to RB-9, FB94, FB95): an assistant agent of the platform,
    /// <see cref="NhAssistantAgentBuilderIds.AgentId"/>, that helps an administrator build a whole agent in
    /// people's chats and can do for an agent what the administrator can do in the administration's screens,
    /// except secrets: its definition and instructions, tools, required policy and MCP servers, run settings
    /// with whose rights it uses, technical account, skills, triggers, consultations, hand-offs, metadata,
    /// groups, shadow variants, switches, state and rule scripts, and removing and discarding them. It reads
    /// the catalog, the agents with their blockers, the application's accounts and, with an API gateway, the
    /// gateway's resources without asking; it runs dry runs, which change nothing; and every change is a
    /// governed call that waits for the administrator's approval in the chat, whose card says what changes and
    /// says plainly when a change alters whose rights an agent uses or removes something. It acts with the
    /// administrator's own rights through the services the administration's endpoints use, so their checks,
    /// versions, ceilings, package locks, four eyes and audit apply unchanged; no tool takes or returns MCP
    /// credentials, keys or other secrets, and none goes beyond what the code allows. Only a person who passes
    /// the assistant's access policy and its administration policy sees it, chats with it and gets its tools
    /// (<see cref="NhAssistantAgentBuilderIds.Policy"/>); no other agent is offered its tools, and its id,
    /// tools, policy and definition never change in the administration. The administration offers it on an
    /// agent's page and with "New agent with help". <see cref="NhAssistantAgentBuilderOptions.WithoutRightsChanges"/>
    /// keeps an agent's rights out of it, and <c>UseAgentRightsPolicy</c> says who may change them anywhere. Without
    /// this call nothing changes.
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

        builder.State.AgentBuilderGuidance = options.Guidance;
        builder.State.AgentBuilderWithoutRightsChanges = !options.AllowsRightsChanges;

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
/// the administration policy in effect, the latter without a resource, as the administration's endpoints
/// check it. The authorization service is resolved when a requirement is checked,
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
        // The administration policy is checked as the administration's endpoints check it, without a
        // resource: the builder does what the screens do, so a person passes it only where the screens let
        // them in, also with handlers that decide by a tenant, division or subject.
        if ((await authorization.AuthorizeAsync(context.User, context.Resource, access)).Succeeded
            && (await authorization.AuthorizeAsync(context.User, null, admin)).Succeeded)
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
