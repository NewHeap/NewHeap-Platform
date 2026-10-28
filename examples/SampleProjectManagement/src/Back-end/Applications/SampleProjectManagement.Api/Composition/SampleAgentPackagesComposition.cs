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
/// SPM-344: the next version of the kickoff desk arrives as a package of its own in which it waits as
/// a shadow variant of the live desk (<see cref="NextKickoffVersion"/>). Once evaluated, the operator
/// promotes it as the package's next version (<c>POST admin/agents/{id}/promote</c> with
/// <c>asPackageVersion</c>): its instructions, run settings and review skill apply to the live desk
/// with the package's locks, the live desk keeps its name and is linked to that package version, and
/// the variant is deleted with its links, in one transaction and one audit event.
/// SPM-352: the next version carries no hand-off, so the live desk's hand-off from the intake desk
/// loses its link on such a promotion; promoted with <c>uncarriedParts</c> <c>relink</c>
/// (<c>NhAssistantUncarriedParts.Relink</c>), the hand-off stays as it is and stays linked, to the next
/// package version, with the locks of its link, reported as <c>kept-linked</c>.
/// SPM-353: an administrator compares a shipped package with the host over HTTP with
/// <c>POST admin/agent-packages/read</c> and <c>GET admin/agent-packages/{packageId}</c>, which export
/// and audit nothing.
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
    /// SPM-344: the package that carries the next version of the kickoff desk as a shadow variant.
    /// </summary>
    public const string NextVersionPackageId = "sample-project-desks-next";

    /// <summary>
    /// SPM-344: the shadow variant in which the next version of the kickoff desk is evaluated.
    /// </summary>
    public const string KickoffTrialId = "sample-project-kickoff-trial";

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

    /// <summary>
    /// SPM-344: the next version of the kickoff desk as an exported package holds it, under the trial's
    /// id as a shadow variant of the live desk, in a package of its own. It carries the desk's parts
    /// and locks without its hand-off, which stays with the live desk; it is never offered in the chat.
    /// </summary>
    public static NhAssistantAgentPackage NextKickoffVersion(NhAssistantPackageAgent kickoff, int version)
    {
        ArgumentNullException.ThrowIfNull(kickoff);
        var variant = kickoff with
        {
            Id = KickoffTrialId,
            Definition = kickoff.Definition! with
            {
                DisplayName = "Kickoff desk (next version)",
                OfferedInChat = false,
                ShadowOf = KickoffDeskId
            },
            HandOffs = null,
            Groups = null,
            Hashes = null
        };
        return new NhAssistantAgentPackage(
            NhAssistantAgentPackageFormat.Name,
            NhAssistantAgentPackageFormat.Version,
            new NhAssistantAgentPackageInfo(NextVersionPackageId, version, "The next version of the kickoff desk, evaluated in shadow."),
            [variant]);
    }
}
