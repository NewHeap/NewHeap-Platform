using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
using SampleProjectManagement.Core.Services;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-318 and SPM-319: what administrators may connect and try as data, within ceilings in code.
/// SPM-318: agents created in the administration, such as the project starter, may accept hand-offs
/// about projects as data from other agents created there and from the project planner, at most five
/// per source and project a day. A hand-off defined as data works as one in code (SPM-287): a source's
/// model gets the generated tool <c>assistant.handoff.{agentId}.project</c> when its tool selectors
/// select it, the target works on the project in a run of its own, and the depth, tenant, daily limit
/// and shadow rules hold. One that no longer fits, for example after a deploy narrowed this ceiling,
/// is out of effect with a problem the administration shows. SPM-319: administrators may create up
/// to two shadow variants of the project planner, and of agents created in the administration, as
/// data: an agent created there that names its live agent once, receives every trigger of it and
/// always works in shadow, next to the planner's variant in code (SPM-288). Call it after
/// <c>AddSampleProjectPlanner</c>, whose agent the ceilings name.
/// </summary>
public static class SampleAgentLinksAsDataComposition
{
    /// <summary>
    /// SPM-318: the most hand-offs one source may make about one project a day, as data.
    /// </summary>
    public const int MaxHandOffsPerProjectPerDay = 5;

    /// <summary>
    /// SPM-319: the most shadow variants of one agent administrators create as data.
    /// </summary>
    public const int MaxVariantsPerAgent = 2;

    public static IServiceCollection AddSampleAgentLinksAsData(this IServiceCollection services)
    {
        services.AddNewHeapAssistant(assistant => assistant
            .UseHandOffsAsData(handOffs => handOffs
                .Allow(ProjectSubjects.Type, rule => rule
                    .ToAgentsCreatedInAdministration()
                    .FromAgentsCreatedInAdministration()
                    .From(SampleProjectPlannerComposition.AgentId))
                .MaxPerSubjectPerDay(MaxHandOffsPerProjectPerDay))
            .UseShadowVariantsAsData(variants => variants
                .Of(SampleProjectPlannerComposition.AgentId)
                .OfAgentsCreatedInAdministration()
                .MaxPerAgent(MaxVariantsPerAgent)));
        return services;
    }
}
