using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-321: the intake desk and the kickoff desk of SPM-318, which hand projects between them, travel
/// as one agent package. The operator exports both desks with these options: the package keeps the
/// kickoff desk's instructions and the instructions of its review skill as the package has them, so an
/// administrator's change of them is refused with <c>locked</c> until the part is unlinked. The
/// endpoints come with the assistant's administration (<c>POST admin/agent-packages/export</c>,
/// <c>preview</c>, <c>import</c> and <c>POST admin/agents/{id}/package/unlink</c>); nothing has to be
/// registered for them.
/// </summary>
public static class SampleAgentPackagesComposition
{
    /// <summary>
    /// The package of the two desks.
    /// </summary>
    public const string PackageId = "sample-project-desks";

    /// <summary>
    /// The intake desk of SPM-318.
    /// </summary>
    public const string IntakeDeskId = "sample-project-intake-desk";

    /// <summary>
    /// The kickoff desk of SPM-318, which accepts the intake desk's hand-offs about projects.
    /// </summary>
    public const string KickoffDeskId = "sample-project-kickoff-desk";

    /// <summary>
    /// The kickoff desk's skill defined as data that reviews a project before it starts.
    /// </summary>
    public const string KickoffReviewSkillId = "kickoff-review";

    /// <summary>
    /// What the package locks: the kickoff desk's instructions and those of its review skill.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> Locks { get; } =
        new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>
        {
            [KickoffDeskId] = new Dictionary<string, IReadOnlyList<string>>
            {
                [NhAssistantPackagePartKinds.Agent] = ["instructions"],
                [NhAssistantPackagePartKinds.PartOf(NhAssistantPackagePartKinds.Skill, KickoffReviewSkillId)] = ["instructions"]
            }
        };

    /// <summary>
    /// The options of an export of the two desks; the version is the next after the one the host
    /// imported when it is left out.
    /// </summary>
    public static NhAssistantAgentPackageExportOptions ExportOptions(int? version = null)
    {
        return new NhAssistantAgentPackageExportOptions(
            PackageId,
            version,
            "The intake and kickoff desks of the sample, which hand projects between them.",
            Locks);
    }
}
