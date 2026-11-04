using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-394: the agent builder (RB-7 to RB-9, FB94, FB95). An administrator asks it in plain language, for
/// example "warn me when a project has no document three days before its deadline", and it builds the agent
/// with them: it reads the building blocks and what the document check's rule script may call, saves the
/// script, dry-runs it on a real project and sets it live. It can do for an agent what the administrator can
/// do in the administration's screens, except secrets, such as choosing the technical account it answers to
/// and switching on gateway reads; it reads the API gateway's resources with the administrator's own rights
/// and names what blocks an agent. Every change waits for the administrator's approval in the chat, on a card
/// that says so plainly when it changes whose rights an agent uses or removes something, and it works with
/// their own rights through the administration's own services, so the document check's ceiling and four eyes
/// (SPM-393) hold for it: the administrator who saved a version through the builder does not set it live,
/// another one does. Only people with the administration policy see it; the administration offers it on an
/// agent's Build tab and with "New agent with help". It answers on the assistant's chat profile, the model the
/// application names for it, and reads <see cref="Guidance"/>, the application's own guidance.
/// <para>
/// A project manager passes the administration policy and builds agents, and only an administrator changes an
/// agent's rights (<see cref="AgentRightsPolicy"/>): whose rights it uses, who reaches it, who decides its proposals
/// and what runs without approval, in the screens, in the builder and through an agent package alike. An
/// application that wants those settings out of the builder altogether and in the screens only says
/// <c>WithoutRightsChanges</c> (FB97), which <c>rightsInScreensOnly</c> shows.
/// </para>
/// Call it after <c>AddSampleAssistant</c>, which registers the profile and the administration policy.
/// </summary>
public static class SampleAgentBuilderComposition
{
    /// <summary>The agent builder's id, which the platform reserves.</summary>
    public const string AgentId = NhAssistantAgentBuilderIds.AgentId;

    /// <summary>
    /// The policy a person needs on top of the administration policy to change an agent's rights: the role
    /// administrator, while a project manager passes the administration policy with app.project.manage.
    /// </summary>
    public const string AgentRightsPolicy = "app.project.agent-rights";

    /// <summary>The role that passes <see cref="AgentRightsPolicy"/>.</summary>
    public const string AgentRightsRole = "administrator";

    /// <summary>
    /// The application's guidance for the agent builder (FB95): background about this application's data and
    /// conventions that only the builder reads, after the platform's rules and before the application context.
    /// It never changes the builder's rules; the builder treats it as background, as the application context.
    /// </summary>
    public static NhAiTextAsset Guidance { get; } = NhAiTextAssetFactory.Create(
        "sample-agent-builder-guidance",
        1,
        """
        - Projects and their tasks are read through the API gateway: the resources project and project-task.
          Describe a resource before a rule script queries it; a count needs countOnly and the filter fields
          the description lists.
        - A check that runs for the whole application, such as a weekly count, answers to a technical account
          with gateway reads on, because no project's responsible person stands for the whole application.
        - Alerts go to the people the alert kind chooses: for a project its responsible person. The whole
          application has no responsible person, so a kind about it, such as weekly-task-changes, warns the people
          its rule script names.
        - Agents of this application are named in English, in lowercase dash-case ids.
        """,
        "sample:agent-builder-guidance",
        NhAiAssetRole.SystemInstructions,
        NhAiContextTrust.TrustedApplication,
        NhAiModelCapability.Chat,
        [],
        "default",
        NhAiDataClassification.Internal,
        NhAiRetentionCategory.Operational,
        "sample-agent-builder-guidance-v1");

    public static IServiceCollection AddSampleAgentBuilder(this IServiceCollection services, bool rightsInScreensOnly = false)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(AgentRightsPolicy, policy => policy.RequireRole(AgentRightsRole));
        services.AddNewHeapAssistant(assistant => assistant
            .UseAgentRightsPolicy(AgentRightsPolicy)
            .AddAgentBuilder(builder =>
            {
                builder.ProfileName = SampleAssistantComposition.ProfileName;
                builder.UseGuidance(Guidance);
                if (rightsInScreensOnly)
                {
                    builder.WithoutRightsChanges();
                }
            }));
        return services;
    }
}
