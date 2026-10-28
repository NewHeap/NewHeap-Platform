using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Core.Models.AI;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL;
using SampleProjectManagement.DAL.Entities;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-347: a unit test of the sample's tool presenter checks what it presents against the assistant's
/// presentation limits with <see cref="NhAssistantPresentationLimits.FindBrokenRule"/>, the check by which
/// the assistant drops a presentation for the generic summary. It presents the status change of a
/// project with the longest key and name the sample stores, in every supported culture, so a presenter
/// that grows a field or a text past a limit fails here instead of showing approvers the generic summary
/// in production. No host and no database: the presenter reads the project through
/// <see cref="IProjectAiReadService"/>.
/// </summary>
public sealed class AssistantToolPresenterSamplesTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("nl-NL")]
    public async Task SPM_347_the_status_change_presentation_keeps_within_the_limits_for_the_longest_project(string cultureName)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);
        var divisionId = Guid.NewGuid();
        var project = new ProjectAiApprovalItem(
            Guid.NewGuid(),
            new string('K', MaxLengthOf(nameof(Project.Key))),
            new string('N', MaxLengthOf(nameof(Project.Name))),
            ProjectStatus.OnHold);
        var presenter = CreatePresenter(new OneProject(divisionId, project));

        var presentation = await PresentInAsync(
            culture,
            () => presenter.PresentApprovalAsync(
                new NhAssistantApprovalPresentationContext(
                    ChangeStatusDescriptor(),
                    new ProjectAiStatusChangeInput(project.Id, ProjectStatus.Archived),
                    InvocationIn(divisionId),
                    culture),
                TestContext.Current.CancellationToken));

        Assert.NotNull(presentation);
        Assert.Contains(project.Name, presentation.Summary, StringComparison.Ordinal);
        Assert.Equal(3, presentation.Fields.Count);
        // The check the assistant drops a presentation by finds no broken rule.
        Assert.Null(NhAssistantPresentationLimits.FindBrokenRule(presentation));
    }

    [Fact]
    public async Task SPM_347_the_check_names_the_rule_a_grown_presentation_would_be_dropped_by()
    {
        var culture = CultureInfo.GetCultureInfo("en-US");
        var divisionId = Guid.NewGuid();
        var project = new ProjectAiApprovalItem(Guid.NewGuid(), "PRJ-347", "Presenter limits", ProjectStatus.Draft);
        var presenter = CreatePresenter(new OneProject(divisionId, project));
        var presentation = await PresentInAsync(
            culture,
            () => presenter.PresentApprovalAsync(
                new NhAssistantApprovalPresentationContext(
                    ChangeStatusDescriptor(),
                    new ProjectAiStatusChangeInput(project.Id, ProjectStatus.Active),
                    InvocationIn(divisionId),
                    culture),
                TestContext.Current.CancellationToken));
        Assert.NotNull(presentation);

        // A presenter that later showed every task of the project would break the field cap: the test
        // names the rule, as the warning in production would, before an approver reads the fallback.
        var grown = presentation with
        {
            Fields = presentation.Fields
                .Concat(Enumerable.Range(1, NhAssistantPresentationLimits.MaxFields)
                    .Select(index => new NhAssistantPresentationField($"Task {index}", "Open")))
                .ToArray()
        };

        Assert.Equal(NhAssistantPresentationRules.TooManyFields, NhAssistantPresentationLimits.FindBrokenRule(grown));
    }

    /// <summary>
    /// The longest text the sample stores in a project's property, from its <see cref="StringLengthAttribute"/>,
    /// so a longer column in the model makes this test present the longer text.
    /// </summary>
    private static int MaxLengthOf(string propertyName)
    {
        return typeof(Project).GetProperty(propertyName)!.GetCustomAttribute<StringLengthAttribute>()!.MaximumLength;
    }

    private static SampleAssistantToolPresenter CreatePresenter(IProjectAiReadService projects)
    {
        var localizers = new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions()),
            NullLoggerFactory.Instance);
        // The status change reads the project through the read service; only the task completion queries
        // the context, which this test does not present, so it needs no database provider.
        var dbContext = new SampleProjectManagementDbContext(new DbContextOptionsBuilder<SampleProjectManagementDbContext>().Options);
        return new SampleAssistantToolPresenter(
            projects,
            dbContext,
            new StringLocalizer<SampleAssistantToolPresenter>(localizers));
    }

    private static NhAiToolDescriptor ChangeStatusDescriptor()
    {
        return Assert.Single(new ProjectAiToolsNhAiCatalog().Descriptors, tool => tool.Id == ProjectAiTools.ChangeStatusToolId);
    }

    private static NhAiInvocationContext InvocationIn(Guid divisionId)
    {
        return new NhAiInvocationContext(
            "spm-347-user",
            "project-assistance",
            new Dictionary<string, string>
            {
                [ProjectAiTools.DivisionScopeKey] = divisionId.ToString("D")
            });
    }

    /// <summary>
    /// Presents with <paramref name="culture"/> current, as the assistant runs a presenter in each culture
    /// it presents in.
    /// </summary>
    private static async Task<NhAssistantApprovalPresentation?> PresentInAsync(
        CultureInfo culture,
        Func<ValueTask<NhAssistantApprovalPresentation?>> present)
    {
        var (previousCulture, previousUiCulture) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        try
        {
            return await present();
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    private sealed class OneProject(Guid divisionId, ProjectAiApprovalItem project) : IProjectAiReadService
    {
        public Task<ProjectAiApprovalItem?> GetForAiApprovalAsync(
            Guid division,
            Guid projectId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(division == divisionId && projectId == project.Id ? project : null);
        }

        public Task<IReadOnlyList<ProjectAiSearchItem>> SearchForAiAsync(
            Guid division,
            string? query,
            int limit,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ProjectAiSearchItem>>([]);
        }
    }
}
