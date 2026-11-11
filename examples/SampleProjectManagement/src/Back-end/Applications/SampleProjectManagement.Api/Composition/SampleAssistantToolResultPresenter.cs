using System.Globalization;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.AI.Chat;
using SampleProjectManagement.Core.Models.AI;
using SampleProjectManagement.Core.Services;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// Presents what the project assistant's project reads returned as trusted result cards (generative UI):
/// <c>projects.get</c> as the project with its status and deadline and a link to its page, which the
/// portal shows in the default card, and <c>projects.search</c> as the projects found, which the portal
/// shows with its own renderer. The card is built from the data the authorized call returned, in the
/// culture the assistant asks for, and never repeats the raw result; the texts share the resources of
/// <see cref="SampleAssistantToolPresenter"/>. Every card keeps within <see cref="NhAssistantPresentationLimits"/>:
/// at most eight fields, so a search shows its first eight projects and says how many matched.
/// </summary>
public sealed class SampleAssistantToolResultPresenter(
    IStringLocalizer<SampleAssistantToolPresenter> localizer) : INhAssistantToolResultPresenter
{
    /// <summary>The portal's project board, which the card of a search links to.</summary>
    public const string ProjectBoardRoute = "/management/project-management";

    public ValueTask<NhAssistantToolResultPresentation?> PresentResultAsync(
        NhAssistantToolResultPresentationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var presentation = context.Descriptor.Id switch
        {
            ProjectOverviewAiTools.GetToolId => PresentProject(context.ReadData<ProjectAiProjectItem>(), context.Culture),
            ProjectAiTools.SearchToolId => PresentSearch(context.ReadData<ProjectAiSearchItem[]>()),
            _ => null
        };
        return ValueTask.FromResult(presentation);
    }

    private NhAssistantToolResultPresentation? PresentProject(ProjectAiProjectItem? project, CultureInfo culture)
    {
        if (project is null)
        {
            return null;
        }

        var deadline = project.Deadline is { } due
            ? due.ToString("D", culture)
            : localizer["ResultNoDeadline"].Value;
        // The name is the title; the key a field, so the longest name and key keep within the title's cap.
        return new NhAssistantToolResultPresentation(
            project.Name,
            [
                new NhAssistantPresentationField(localizer["ApprovalFieldProject"].Value, project.Key),
                new NhAssistantPresentationField(localizer["ApprovalFieldStatus"].Value, localizer[$"ProjectStatus{project.Status}"].Value),
                new NhAssistantPresentationField(localizer["ResultFieldDeadline"].Value, deadline)
            ],
            Link: new NhAssistantPresentationLink(
                ProjectSubjects.LinkTemplate.Replace("{id}", project.Id.ToString("D"), StringComparison.Ordinal),
                localizer["ResultOpenProject"].Value));
    }

    private NhAssistantToolResultPresentation? PresentSearch(ProjectAiSearchItem[]? found)
    {
        if (found is null)
        {
            return null;
        }

        var shown = found.Take(NhAssistantPresentationLimits.MaxFields).ToArray();
        var summary = found.Length switch
        {
            0 => localizer["ResultProjectsNone"].Value,
            1 => localizer["ResultProjectsOne"].Value,
            _ when found.Length > shown.Length => localizer["ResultProjectsShown", found.Length, shown.Length].Value,
            _ => localizer["ResultProjectsMany", found.Length].Value
        };
        return new NhAssistantToolResultPresentation(
            localizer["ResultProjectsTitle"].Value,
            shown.Select(project => new NhAssistantPresentationField(project.Key, project.Name)).ToArray(),
            summary,
            new NhAssistantPresentationLink(ProjectBoardRoute, localizer["ResultOpenProjects"].Value));
    }
}
