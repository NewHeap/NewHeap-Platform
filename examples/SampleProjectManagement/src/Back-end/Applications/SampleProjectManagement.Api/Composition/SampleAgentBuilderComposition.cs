using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-394: the agent builder (RB-7 to RB-9). An administrator asks it in plain language, for example "warn me
/// when a project has no document three days before its deadline", and it builds the agent with them: it
/// reads the building blocks and what the document check's rule script may call, saves the script, dry-runs
/// it on a real project and sets it live. Every save and every step live waits for the administrator's
/// approval in the chat, and it works with their own rights through the administration's own services, so
/// the document check's ceiling and four eyes (SPM-393) hold for it: the administrator who saved a version
/// through the builder does not set it live, another one does. Only people with the administration policy
/// see it; the administration offers it on an agent's Build tab and with "New agent with help". It answers
/// on the assistant's chat profile, the model the application names for it.
/// Call it after <c>AddSampleAssistant</c>, which registers the profile and the administration policy.
/// </summary>
public static class SampleAgentBuilderComposition
{
    /// <summary>The agent builder's id, which the platform reserves.</summary>
    public const string AgentId = NhAssistantAgentBuilderIds.AgentId;

    public static IServiceCollection AddSampleAgentBuilder(this IServiceCollection services)
    {
        services.AddNewHeapAssistant(assistant => assistant
            .AddAgentBuilder(builder => builder.ProfileName = SampleAssistantComposition.ProfileName));
        return services;
    }
}
